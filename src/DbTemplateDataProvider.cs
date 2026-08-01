using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Com.H.Data.Common;

namespace Com.H.Text.Template2
{
    /// <summary>
    /// The ADO.NET data provider: satisfies a template's <c>&lt;h-embedded-data&gt;</c> blocks by
    /// executing their queries through <c>Com.H.Data.Common</c>, which converts
    /// <c>{{marker}}</c> occurrences into real <see cref="DbParameter"/> objects.
    /// </summary>
    /// <remarks>
    /// No value is ever substituted into SQL as text. Safety is structural rather than a matter
    /// of remembering to escape.
    /// </remarks>
    public sealed class DbTemplateDataProvider : ITemplateDataProvider
    {
        private readonly DbConnection? _connection;
        private readonly Func<TemplateDataRequest, DbConnection>? _connectionFactory;
        private readonly bool _allowPreRender;
        private readonly int? _commandTimeout;

        /// <summary>
        /// Creates a provider that runs every embedded query on the supplied connection.
        /// </summary>
        /// <param name="connection">
        /// The connection to query. It is neither opened nor disposed by this class beyond what
        /// executing a query requires, so its lifetime stays with the caller.
        /// </param>
        /// <param name="allowPreRender">
        /// When false (the default) a template requesting <c>pre-render="true"</c> is rejected,
        /// because pre-rendering substitutes values into the SQL as text and reintroduces
        /// injection risk. Enable it only when a template must interpolate an identifier
        /// (a table or column name), which cannot be parameterised — and only for templates you
        /// control.
        /// </param>
        /// <param name="commandTimeout">Optional command timeout, in seconds.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="connection"/> is null.</exception>
        public DbTemplateDataProvider(
            DbConnection connection,
            bool allowPreRender = false,
            int? commandTimeout = null)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _allowPreRender = allowPreRender;
            _commandTimeout = commandTimeout;
        }

        /// <summary>
        /// Creates a provider that obtains a connection per data request.
        /// </summary>
        /// <param name="connectionFactory">
        /// Produces the connection for a given request. The returned connection is closed once the
        /// query's rows have been read. Use this overload if you want to honour the template's
        /// <c>connection-string</c> attribute — see the remarks about why that is off by default.
        /// </param>
        /// <param name="allowPreRender">See the other constructor.</param>
        /// <param name="commandTimeout">Optional command timeout, in seconds.</param>
        /// <remarks>
        /// The <c>connection-string</c> attribute a template may carry is <b>not</b> honoured
        /// automatically. A template is data, and data should not be able to point the
        /// application at an arbitrary database. Reading
        /// <see cref="TemplateDataRequest.ConnectionString"/> inside your factory is an explicit,
        /// deliberate opt-in.
        /// </remarks>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="connectionFactory"/> is null.</exception>
        public DbTemplateDataProvider(
            Func<TemplateDataRequest, DbConnection> connectionFactory,
            bool allowPreRender = false,
            int? commandTimeout = null)
        {
            _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _allowPreRender = allowPreRender;
            _commandTimeout = commandTimeout;
        }

        /// <summary>
        /// Executes the query carried by a template's data block and returns its rows.
        /// </summary>
        /// <param name="request">The request assembled by the engine.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// The result rows, or null when the request carries no query. Rows are fully
        /// materialised so the underlying reader closes before returning.
        /// </returns>
        /// <exception cref="NotSupportedException">
        /// Thrown when the template requests <c>pre-render="true"</c> and pre-rendering was not
        /// explicitly enabled.
        /// </exception>
        public async Task<IEnumerable<dynamic>?> GetDataAsync(
            TemplateDataRequest request,
            CancellationToken cancellationToken = default)
        {
            var query = request?.Query;
            if (request is null || string.IsNullOrWhiteSpace(query)) return null;

            if (request.PreRender && !_allowPreRender)
            {
                throw new NotSupportedException(
                    "This template sets pre-render=\"true\", which substitutes parameter values "
                    + "into the SQL as text and reintroduces SQL injection risk. "
                    + "Com.H.Text.Template2 parameterises queries instead, so pre-rendering "
                    + "is rejected by default. If the template needs to interpolate an identifier "
                    + "(a table or column name), which cannot be parameterised, construct the "
                    + "provider with allowPreRender: true — and only for templates you control.");
            }

            var queryParams = MapQueryParams(request.DataModels);

            if (_connection is not null)
            {
                // Caller owns the connection; leave it open for subsequent (possibly nested) requests.
                return await ExecuteAsync(
                    _connection, query!, queryParams,
                    closeConnectionOnExit: false, cancellationToken).ConfigureAwait(false);
            }

            var connection = _connectionFactory!(request)
                ?? throw new InvalidOperationException(
                    "The connection factory returned null for a template data request.");

            try
            {
                return await ExecuteAsync(
                    connection, query!, queryParams,
                    closeConnectionOnExit: true, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // this provider owns a factory-created connection; closeConnectionOnExit only
                // applies once execution succeeds, so a failure here would otherwise leak it
                try { connection.Dispose(); } catch { }
                throw;
            }
        }

        private async Task<List<dynamic>> ExecuteAsync(
            DbConnection connection,
            string query,
            List<DbQueryParams>? queryParams,
            bool closeConnectionOnExit,
            CancellationToken cancellationToken)
        {
            await using var result = await connection.ExecuteQueryAsync(
                query,
                queryParams,
                commandTimeout: _commandTimeout,
                closeConnectionOnExit: closeConnectionOnExit,
                cToken: cancellationToken).ConfigureAwait(false);

            var rows = new List<dynamic>();
            await foreach (var row in result.AsAsyncEnumerable()
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>
        /// Translates the engine's data-model chain into the data layer's parameter model.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A pass-through: <see cref="TemplateDataModel.MarkerPattern"/> is deliberately the same
        /// named-group shape as <c>DbQueryParams.QueryParamsRegex</c>, so a template's markers
        /// address query parameters without any translation.
        /// </para>
        /// <para>
        /// <see cref="TemplateDataModel.NullValue"/> is not carried across. It substitutes text
        /// into the rendered body, whereas parameterised execution binds a genuine <c>DBNull</c> —
        /// both safer and more correct.
        /// </para>
        /// </remarks>
        internal static List<DbQueryParams>? MapQueryParams(IReadOnlyList<TemplateDataModel>? models)
        {
            if (models is null || models.Count == 0) return null;

            var mapped = new List<DbQueryParams>();
            foreach (var entry in models)
            {
                if (entry?.Model is null) continue;
                mapped.Add(new DbQueryParams
                {
                    DataModel = entry.Model,
                    QueryParamsRegex = string.IsNullOrWhiteSpace(entry.MarkerPattern)
                        ? TemplateDataModel.DefaultPattern
                        : entry.MarkerPattern
                });
            }
            return mapped.Count == 0 ? null : mapped;
        }
    }
}
