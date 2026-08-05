using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Com.H.Text.Template2
{
    /// <summary>
    /// A connection for one <c>&lt;h-embedded-data&gt;</c> block, and who is responsible for
    /// disposing it.
    /// </summary>
    /// <remarks>
    /// The engine cannot infer ownership: a factory may hand back one long-lived connection for
    /// every block, or open a fresh one each time. Saying so explicitly avoids both leaking and
    /// closing a connection the caller is still using.
    /// </remarks>
    public sealed class TemplateConnection
    {
        /// <summary>Creates a connection the engine will dispose once the block's rows are read.</summary>
        /// <param name="connection">The connection to use. Opened by the engine if not already open.</param>
        public TemplateConnection(DbConnection connection) : this(connection, true) { }

        /// <summary>Creates a connection, stating who disposes it.</summary>
        /// <param name="connection">The connection to use. Opened by the engine if not already open.</param>
        /// <param name="disposeWhenDone">
        /// True to let the engine dispose it once the block's rows have been read — right for a
        /// factory that opens one per block. False to keep ownership, which is right for a shared
        /// connection, an ambient transaction, or a container-managed lifetime.
        /// </param>
        public TemplateConnection(DbConnection connection, bool disposeWhenDone)
        {
            Connection = connection ?? throw new ArgumentNullException(nameof(connection));
            DisposeWhenDone = disposeWhenDone;
        }

        /// <summary>The connection the block's query runs on.</summary>
        public DbConnection Connection { get; }

        /// <summary>Whether the engine disposes <see cref="Connection"/> after the block.</summary>
        public bool DisposeWhenDone { get; }

        /// <summary>Wraps a caller-owned connection: used, never disposed.</summary>
        public static TemplateConnection Borrowed(DbConnection connection)
            => new TemplateConnection(connection, false);

        /// <summary>Wraps a connection the engine should dispose once the block is done.</summary>
        public static TemplateConnection Owned(DbConnection connection)
            => new TemplateConnection(connection, true);
    }

    /// <summary>
    /// Supplies the connection for one data block, given that block's attributes.
    /// </summary>
    /// <param name="attributes">
    /// Every attribute on the tag, keyed case-insensitively with <c>_</c> normalised to <c>-</c>.
    /// The engine attaches no meaning to any of them — invent whatever your templates need
    /// (<c>database</c>, <c>tenant</c>, <c>timeout</c>, <c>retries</c>) and decide here which are
    /// required.
    /// </param>
    /// <param name="cancellationToken">Cancellation for the render in progress.</param>
    /// <returns>The connection to use, and who disposes it.</returns>
    /// <remarks>
    /// Returning null tells the engine there is no data source for this block, so the template
    /// renders once from the models already in scope rather than failing.
    /// </remarks>
    public delegate ValueTask<TemplateConnection?> TemplateConnectionFactory(
        IReadOnlyDictionary<string, string?> attributes,
        CancellationToken cancellationToken);

    /// <summary>
    /// Supplies the <see cref="HttpClient"/> used to fetch a template or a block's <c>src</c>.
    /// </summary>
    /// <param name="attributes">
    /// Every attribute on the tag, as for <see cref="TemplateConnectionFactory"/>. Use them to
    /// pick a named client, attach auth, or choose a retry policy.
    /// </param>
    /// <param name="cancellationToken">Cancellation for the render in progress.</param>
    /// <returns>The client to use.</returns>
    /// <remarks>
    /// The engine never disposes the returned client — unlike a connection, an
    /// <see cref="HttpClient"/> is normally long-lived, and disposing one per request exhausts
    /// sockets. Return a client from <c>IHttpClientFactory</c> or a cached singleton.
    /// </remarks>
    public delegate ValueTask<HttpClient> TemplateHttpClientFactory(
        IReadOnlyDictionary<string, string?> attributes,
        CancellationToken cancellationToken);
}
