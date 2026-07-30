using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.RegularExpressions;
using Com.H.Data;
using Com.H.Data.Common;
using Com.H.Text.Template;

namespace Com.H.Text.Template2
{
    /// <summary>
    /// Supplies the Com.H templating engine with data from any ADO.NET database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The engine exposes a <c>Func&lt;TemplateMultiDataRequest, IEnumerable&lt;dynamic&gt;?&gt;</c>
    /// extension point and deliberately hands over the query text <em>un-substituted</em>, so that
    /// the provider can apply its own SQL injection protection. This class is that provider:
    /// every query is executed through <c>Com.H.Data.Common</c>, which converts
    /// <c>{{marker}}</c> occurrences into real <see cref="DbParameter"/> objects.
    /// </para>
    /// <para>
    /// No value is ever substituted into SQL as text. Safety is structural rather than a matter
    /// of remembering to escape.
    /// </para>
    /// </remarks>
    public sealed class DbTemplateDataProvider
    {
        private readonly DbConnection? _connection;
        private readonly Func<TemplateMultiDataRequest, DbConnection>? _connectionFactory;
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
        /// <see cref="TemplateMultiDataRequest.ConnectionString"/> inside your factory is an
        /// explicit, deliberate opt-in.
        /// </remarks>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="connectionFactory"/> is null.</exception>
        public DbTemplateDataProvider(
            Func<TemplateMultiDataRequest, DbConnection> connectionFactory,
            bool allowPreRender = false,
            int? commandTimeout = null)
        {
            _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _allowPreRender = allowPreRender;
            _commandTimeout = commandTimeout;
        }

        /// <summary>
        /// The delegate to hand to <c>RenderContent</c>'s <c>dataProviders</c> parameter.
        /// </summary>
        /// <example>
        /// <code>
        /// var provider = new DbTemplateDataProvider(connection);
        /// var html = template.RenderContent(dataProviders: provider.GetData);
        /// </code>
        /// </example>
        public Func<TemplateMultiDataRequest, IEnumerable<dynamic>?> GetData => Execute;

        /// <summary>
        /// Executes the query carried by a template's data tag and returns its rows.
        /// </summary>
        /// <param name="request">The request assembled by the templating engine.</param>
        /// <returns>
        /// The result rows, or null when the request carries no query. Rows are fully materialised
        /// so that the underlying reader is closed before returning — the engine's delegate
        /// signature gives the provider no opportunity to dispose afterwards.
        /// </returns>
        /// <exception cref="NotSupportedException">
        /// Thrown when the template requests <c>pre-render="true"</c> and pre-rendering was not
        /// explicitly enabled.
        /// </exception>
        public IEnumerable<dynamic>? Execute(TemplateMultiDataRequest request)
        {
            if (request is null) return null;

            var query = request.Request;
            if (string.IsNullOrWhiteSpace(query)) return null;

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

            var queryParams = MapQueryParams(request.QueryParamsList);

            if (_connection is not null)
            {
                // Caller owns the connection; leave it open for subsequent (possibly nested) requests.
                using var result = _connection.ExecuteQuery(
                    query!,
                    queryParams,
                    commandTimeout: _commandTimeout,
                    closeConnectionOnExit: false,
                    cToken: request.CancellationToken ?? default);

                return result.AsEnumerable().ToList();
            }

            var connection = _connectionFactory!(request)
                ?? throw new InvalidOperationException(
                    "The connection factory returned null for a template data request.");

            using var factoryResult = connection.ExecuteQuery(
                query!,
                queryParams,
                commandTimeout: _commandTimeout,
                closeConnectionOnExit: true,
                cToken: request.CancellationToken ?? default);

            return factoryResult.AsEnumerable().ToList();
        }

        /// <summary>
        /// Translates the engine's parameter model into the one the data layer understands.
        /// </summary>
        /// <remarks>
        /// <see cref="QueryParams.NullReplacement"/> is deliberately not carried across. It exists
        /// to substitute the literal text "null" into a query, whereas parameterised execution
        /// binds a genuine <c>DBNull</c> — which is both safer and more correct.
        /// </remarks>
        internal static List<DbQueryParams>? MapQueryParams(IEnumerable<QueryParams>? queryParamsList)
        {
            if (queryParamsList is null) return null;

            var mapped = queryParamsList
                .Where(x => x is not null && x.DataModel is not null)
                .Select(x => new DbQueryParams
                {
                    DataModel = x.DataModel,
                    QueryParamsRegex = BuildMarkerRegex(x.OpenMarker, x.CloseMarker)
                })
                .ToList();

            return mapped.Count == 0 ? null : mapped;
        }

        /// <summary>
        /// Builds the data layer's named-group regex from the engine's open/close marker pair.
        /// </summary>
        internal static string BuildMarkerRegex(string? openMarker, string? closeMarker)
        {
            var open = string.IsNullOrEmpty(openMarker) ? "{{" : openMarker!;
            var close = string.IsNullOrEmpty(closeMarker) ? "}}" : closeMarker!;

            return "(?<open_marker>" + Regex.Escape(open) + ")"
                 + "(?<param>.*?)?"
                 + "(?<close_marker>" + Regex.Escape(close) + ")";
        }
    }
}
