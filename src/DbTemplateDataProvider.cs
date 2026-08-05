using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Com.H.Data.Common;

namespace Com.H.Text.Template2
{
    /// <summary>
    /// Runs a template's embedded queries against any ADO.NET database, obtaining the connection
    /// for each block from a <see cref="TemplateConnectionFactory"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every query goes through <c>Com.H.Data.Common</c>, which turns <c>{{marker}}</c> into a
    /// real <see cref="DbParameter"/>. No value is ever substituted into SQL as text, so safety
    /// is structural rather than a matter of remembering to escape.
    /// </para>
    /// <para>
    /// The engine passes the block's attributes to the factory and attaches no meaning to them
    /// itself — which database to open, and on what terms, is entirely the caller's decision.
    /// </para>
    /// </remarks>
    public sealed class DbTemplateDataProvider : ITemplateDataProvider
    {
        private readonly TemplateConnectionFactory _connectionFactory;
        private readonly int? _commandTimeout;

        /// <summary>
        /// Creates a provider that runs every block on the supplied connection, which it never
        /// opens beyond what executing a query requires and never disposes.
        /// </summary>
        /// <param name="connection">The connection to query.</param>
        /// <param name="commandTimeout">Optional command timeout, in seconds.</param>
        /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
        public DbTemplateDataProvider(DbConnection connection, int? commandTimeout = null)
        {
            if (connection is null) throw new ArgumentNullException(nameof(connection));

            _connectionFactory = (_, _) =>
                new ValueTask<TemplateConnection?>(TemplateConnection.Borrowed(connection));
            _commandTimeout = commandTimeout;
        }

        /// <summary>
        /// Creates a provider that asks a factory for the connection to use for each block.
        /// </summary>
        /// <param name="connectionFactory">
        /// Receives the block's attributes and returns the connection plus who disposes it.
        /// Return null to leave the block without a data source, which renders the template once
        /// from the models already in scope.
        /// </param>
        /// <param name="commandTimeout">Optional command timeout, in seconds.</param>
        /// <exception cref="ArgumentNullException"><paramref name="connectionFactory"/> is null.</exception>
        public DbTemplateDataProvider(TemplateConnectionFactory connectionFactory, int? commandTimeout = null)
        {
            _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _commandTimeout = commandTimeout;
        }

        /// <inheritdoc/>
        public async ValueTask<IEnumerable<dynamic>?> GetDataAsync(
            TemplateDataRequest request, CancellationToken cancellationToken = default)
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Query)) return null;

            var lease = await _connectionFactory(request.Attributes, cancellationToken)
                .ConfigureAwait(false);
            if (lease is null) return null;

            var parameters = request.DataModels as List<DbQueryParams> ?? request.DataModels?.ToList();

            try
            {
                if (lease.Connection.State != System.Data.ConnectionState.Open)
                    await lease.Connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                await using var result = await lease.Connection.ExecuteQueryAsync(
                    request.Query!,
                    parameters,
                    commandTimeout: _commandTimeout,
                    closeConnectionOnExit: false,
                    cToken: cancellationToken).ConfigureAwait(false);

                // Materialised before returning: the reader must be closed before the next block
                // runs, and a template builds its whole document in memory anyway, so there is no
                // streaming to give up.
                var rows = new List<dynamic>();
                await foreach (var row in result.AsAsyncEnumerable().WithCancellation(cancellationToken))
                    rows.Add(row);

                return rows;
            }
            finally
            {
                if (lease.DisposeWhenDone)
                {
                    // disposing a failed connection must not mask the original error
#if NETSTANDARD2_0
                    try { lease.Connection.Dispose(); } catch { }
#else
                    try { await lease.Connection.DisposeAsync().ConfigureAwait(false); } catch { }
#endif
                }
            }
        }
    }
}
