using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Com.H.Data.Common;

namespace Com.H.Text.Template2
{
    /// <summary>
    /// Renders Com.H templates. Values are <c>{{markers}}</c>; an optional
    /// <c>&lt;h-embedded-data&gt;</c> block runs a query (its rows repeat the template body);
    /// <c>&lt;h-embedded-template&gt;</c> nests other templates.
    /// </summary>
    /// <remarks>
    /// Every overload funnels into one implementation that takes an
    /// <see cref="ITemplateDataProvider"/>. The others differ only in how they obtain one: from a
    /// connection, from a <see cref="TemplateConnectionFactory"/>, or not at all.
    /// </remarks>
    public static class TemplateExtensions
    {
        // ------------------------------------------------------------- with a DbConnection

        /// <summary>
        /// Renders template content, executing any embedded query against the supplied connection.
        /// </summary>
        /// <param name="content">The template text.</param>
        /// <param name="connection">
        /// The connection embedded queries run on. Opened if not already open, and never disposed
        /// — its lifetime stays with you.
        /// </param>
        /// <param name="dataModel">
        /// Values for the template's <c>{{markers}}</c>. Anonymous object, dictionary, JSON string,
        /// <c>JsonElement</c>, or any object with matching properties. Values reaching a query are
        /// bound as SQL parameters, never substituted as text.
        /// </param>
        /// <param name="options">Occasional settings; see <see cref="TemplateOptions"/>.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>The rendered content.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
        public static string? RenderContent(
            this string content,
            DbConnection connection,
            object? dataModel = null,
            TemplateOptions? options = null,
            CancellationToken? cancellationToken = null)
            => RenderContentAsync(content, connection, dataModel, options, cancellationToken)
                .GetAwaiter().GetResult();

        /// <summary>Async form of
        /// <see cref="RenderContent(string, DbConnection, object?, TemplateOptions?, CancellationToken?)"/>.
        /// </summary>
        public static Task<string?> RenderContentAsync(
            this string content,
            DbConnection connection,
            object? dataModel = null,
            TemplateOptions? options = null,
            CancellationToken? cancellationToken = null)
        {
            if (connection is null) throw new ArgumentNullException(nameof(connection));
            options ??= TemplateOptions.Default;

            return RenderContentAsync(
                content,
                new DbTemplateDataProvider(connection, options.CommandTimeout),
                dataModel, options, cancellationToken);
        }

        /// <summary>
        /// Renders the template at a URI, executing any embedded query against the supplied connection.
        /// </summary>
        /// <param name="uri">Location of the template. Local paths and http(s) are both supported.</param>
        /// <param name="connection">The connection embedded queries run on; never disposed by the engine.</param>
        /// <param name="dataModel">Values for the template's <c>{{markers}}</c>.</param>
        /// <param name="options">Occasional settings; see <see cref="TemplateOptions"/>.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>The rendered content.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="uri"/> or <paramref name="connection"/> is null.</exception>
        public static string? RenderContent(
            this Uri uri,
            DbConnection connection,
            object? dataModel = null,
            TemplateOptions? options = null,
            CancellationToken? cancellationToken = null)
            => RenderContentAsync(uri, connection, dataModel, options, cancellationToken)
                .GetAwaiter().GetResult();

        /// <summary>Async form of
        /// <see cref="RenderContent(Uri, DbConnection, object?, TemplateOptions?, CancellationToken?)"/>.
        /// </summary>
        public static Task<string?> RenderContentAsync(
            this Uri uri,
            DbConnection connection,
            object? dataModel = null,
            TemplateOptions? options = null,
            CancellationToken? cancellationToken = null)
        {
            if (connection is null) throw new ArgumentNullException(nameof(connection));
            options ??= TemplateOptions.Default;

            return RenderContentAsync(
                uri,
                new DbTemplateDataProvider(connection, options.CommandTimeout),
                dataModel, options, cancellationToken);
        }

        // ------------------------------------------------------- with a connection factory

        /// <summary>
        /// Renders template content, asking a factory for the connection each data block runs on.
        /// </summary>
        /// <param name="content">The template text.</param>
        /// <param name="connectionFactory">
        /// Receives the block's attributes and returns the connection plus who disposes it. The
        /// engine attaches no meaning to attributes it doesn't parse itself, so this is where a
        /// template's own vocabulary — <c>database</c>, <c>tenant</c>, <c>timeout</c> — is
        /// interpreted. Return null to leave the block without a data source.
        /// </param>
        /// <param name="dataModel">Values for the template's <c>{{markers}}</c>.</param>
        /// <param name="options">Occasional settings; see <see cref="TemplateOptions"/>.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>The rendered content.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="connectionFactory"/> is null.</exception>
        /// <example>
        /// <code>
        /// var html = await template.RenderContentAsync(
        ///     (attrs, ct) =>
        ///     {
        ///         var name = attrs.TryGetValue("database", out var v) ? v : "default";
        ///         return new ValueTask&lt;TemplateConnection?&gt;(
        ///             TemplateConnection.Owned(factory.Create(name)));
        ///     },
        ///     new { country = "JO" });
        /// </code>
        /// </example>
        public static string? RenderContent(
            this string content,
            TemplateConnectionFactory connectionFactory,
            object? dataModel = null,
            TemplateOptions? options = null,
            CancellationToken? cancellationToken = null)
            => RenderContentAsync(content, connectionFactory, dataModel, options, cancellationToken)
                .GetAwaiter().GetResult();

        /// <summary>Async form of
        /// <see cref="RenderContent(string, TemplateConnectionFactory, object?, TemplateOptions?, CancellationToken?)"/>.
        /// </summary>
        public static Task<string?> RenderContentAsync(
            this string content,
            TemplateConnectionFactory connectionFactory,
            object? dataModel = null,
            TemplateOptions? options = null,
            CancellationToken? cancellationToken = null)
        {
            if (connectionFactory is null) throw new ArgumentNullException(nameof(connectionFactory));
            options ??= TemplateOptions.Default;

            return RenderContentAsync(
                content,
                new DbTemplateDataProvider(connectionFactory, options.CommandTimeout),
                dataModel, options, cancellationToken);
        }

        /// <summary>
        /// Renders the template at a URI, asking a factory for the connection each data block runs on.
        /// </summary>
        /// <param name="uri">Location of the template.</param>
        /// <param name="connectionFactory">See the string overload.</param>
        /// <param name="dataModel">Values for the template's <c>{{markers}}</c>.</param>
        /// <param name="options">Occasional settings; see <see cref="TemplateOptions"/>.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>The rendered content.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="uri"/> or <paramref name="connectionFactory"/> is null.</exception>
        public static string? RenderContent(
            this Uri uri,
            TemplateConnectionFactory connectionFactory,
            object? dataModel = null,
            TemplateOptions? options = null,
            CancellationToken? cancellationToken = null)
            => RenderContentAsync(uri, connectionFactory, dataModel, options, cancellationToken)
                .GetAwaiter().GetResult();

        /// <summary>Async form of
        /// <see cref="RenderContent(Uri, TemplateConnectionFactory, object?, TemplateOptions?, CancellationToken?)"/>.
        /// </summary>
        public static Task<string?> RenderContentAsync(
            this Uri uri,
            TemplateConnectionFactory connectionFactory,
            object? dataModel = null,
            TemplateOptions? options = null,
            CancellationToken? cancellationToken = null)
        {
            if (connectionFactory is null) throw new ArgumentNullException(nameof(connectionFactory));
            options ??= TemplateOptions.Default;

            return RenderContentAsync(
                uri,
                new DbTemplateDataProvider(connectionFactory, options.CommandTimeout),
                dataModel, options, cancellationToken);
        }

        // ------------------------------------------------------------- without a database

        /// <summary>
        /// Renders template content that needs no data source.
        /// </summary>
        /// <param name="content">The template text.</param>
        /// <param name="dataModel">Values for the template's <c>{{markers}}</c>.</param>
        /// <param name="options">Occasional settings; see <see cref="TemplateOptions"/>.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>The rendered content.</returns>
        /// <remarks>
        /// If the template (or one it nests) does contain an <c>&lt;h-embedded-data&gt;</c> query,
        /// the query is skipped and the template renders once from the supplied model — so one
        /// template works both with and without a database. Set
        /// <see cref="TemplateOptions.ThrowIfQueryPresent"/> to make that an error instead.
        /// <c>content-type="json"</c> blocks are self-contained and still render fully.
        /// </remarks>
        /// <example>
        /// <code>
        /// var text = "Hello {{name}}.".RenderContent(new { name = "Ali" });
        /// </code>
        /// </example>
        public static string? RenderContent(
            this string content,
            object? dataModel,
            TemplateOptions? options = null,
            CancellationToken? cancellationToken = null)
            => RenderContentAsync(content, dataModel, options, cancellationToken)
                .GetAwaiter().GetResult();

        /// <summary>Async form of
        /// <see cref="RenderContent(string, object?, TemplateOptions?, CancellationToken?)"/>.
        /// </summary>
        public static Task<string?> RenderContentAsync(
            this string content,
            object? dataModel,
            TemplateOptions? options = null,
            CancellationToken? cancellationToken = null)
            => RenderContentAsync(
                content, provider: null, dataModel, options, cancellationToken);

        /// <summary>
        /// Renders the template at a URI, where the template needs no data source.
        /// </summary>
        /// <param name="uri">Location of the template.</param>
        /// <param name="dataModel">Values for the template's <c>{{markers}}</c>.</param>
        /// <param name="options">Occasional settings; see <see cref="TemplateOptions"/>.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>The rendered content.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="uri"/> is null.</exception>
        public static string? RenderContent(
            this Uri uri,
            object? dataModel,
            TemplateOptions? options = null,
            CancellationToken? cancellationToken = null)
            => RenderContentAsync(uri, dataModel, options, cancellationToken)
                .GetAwaiter().GetResult();

        /// <summary>Async form of
        /// <see cref="RenderContent(Uri, object?, TemplateOptions?, CancellationToken?)"/>.
        /// </summary>
        public static Task<string?> RenderContentAsync(
            this Uri uri,
            object? dataModel,
            TemplateOptions? options = null,
            CancellationToken? cancellationToken = null)
            => RenderContentAsync(uri, provider: null, dataModel, options, cancellationToken);

        // ---------------------------------------------------------------- with a provider
        //
        // The two methods every other overload ends up calling.

        /// <summary>
        /// Renders template content using a caller-supplied provider — for data from somewhere
        /// other than a database, or with rules the connection factory cannot express.
        /// </summary>
        /// <param name="content">The template text.</param>
        /// <param name="provider">
        /// Satisfies embedded data blocks. Null means the template has no data source: queries
        /// are skipped and the template renders once from the models in scope.
        /// </param>
        /// <param name="dataModel">Values for the template's <c>{{markers}}</c>.</param>
        /// <param name="options">Occasional settings; see <see cref="TemplateOptions"/>.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>The rendered content.</returns>
        public static string? RenderContent(
            this string content,
            ITemplateDataProvider? provider,
            object? dataModel = null,
            TemplateOptions? options = null,
            CancellationToken? cancellationToken = null)
            => RenderContentAsync(content, provider, dataModel, options, cancellationToken)
                .GetAwaiter().GetResult();

        /// <summary>Async form of
        /// <see cref="RenderContent(string, ITemplateDataProvider?, object?, TemplateOptions?, CancellationToken?)"/>.
        /// </summary>
        public static async Task<string?> RenderContentAsync(
            this string content,
            ITemplateDataProvider? provider,
            object? dataModel = null,
            TemplateOptions? options = null,
            CancellationToken? cancellationToken = null)
        {
            options ??= TemplateOptions.Default;
            TemplateEngine.ThrowOnUnresolvedMarker = options.ThrowOnUnresolvedMarker;
            TemplateEngine.ContentResolver = options.ContentResolver;

            return await TemplateEngine.RenderAsync(
                content,
                ParentPathToUri(options.BasePath),
                ToModels(dataModel),
                Effective(provider, options),
                options.Referrer,
                options.UserAgent,
                depth: 0,
                cancellationToken ?? CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// Renders the template at a URI using a caller-supplied provider.
        /// </summary>
        /// <param name="uri">Location of the template.</param>
        /// <param name="provider">Satisfies embedded data blocks; null means no data source.</param>
        /// <param name="dataModel">Values for the template's <c>{{markers}}</c>.</param>
        /// <param name="options">Occasional settings; see <see cref="TemplateOptions"/>.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>The rendered content.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="uri"/> is null.</exception>
        public static string? RenderContent(
            this Uri uri,
            ITemplateDataProvider? provider,
            object? dataModel = null,
            TemplateOptions? options = null,
            CancellationToken? cancellationToken = null)
            => RenderContentAsync(uri, provider, dataModel, options, cancellationToken)
                .GetAwaiter().GetResult();

        /// <summary>Async form of
        /// <see cref="RenderContent(Uri, ITemplateDataProvider?, object?, TemplateOptions?, CancellationToken?)"/>.
        /// </summary>
        public static async Task<string?> RenderContentAsync(
            this Uri uri,
            ITemplateDataProvider? provider,
            object? dataModel = null,
            TemplateOptions? options = null,
            CancellationToken? cancellationToken = null)
        {
            if (uri is null) throw new ArgumentNullException(nameof(uri));
            options ??= TemplateOptions.Default;
            TemplateEngine.ThrowOnUnresolvedMarker = options.ThrowOnUnresolvedMarker;
            TemplateEngine.ContentResolver = options.ContentResolver;

            var ct = cancellationToken ?? CancellationToken.None;
            var models = ToModels(dataModel);

            // the URI itself may carry markers, e.g. .../reports/{{reportName}}.html
            var resolved = TemplateEngine.ResolveUri(uri.OriginalString, null, models);

            // the root template was introduced by no tag, so it has no attributes of its own
            var rootAttributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(options.Referrer))
                rootAttributes["referrer"] = options.Referrer;
            if (!string.IsNullOrWhiteSpace(options.UserAgent))
                rootAttributes["user-agent"] = options.UserAgent;

            var content = await TemplateEngine.ResolveContentAsync(resolved, rootAttributes, ct)
                .ConfigureAwait(false);

            return await TemplateEngine.RenderAsync(
                content,
                new Uri(resolved, "."),
                models,
                Effective(provider, options),
                options.Referrer,
                options.UserAgent,
                depth: 0,
                ct).ConfigureAwait(false);
        }

        // ------------------------------------------------------------- shared plumbing

        /// <summary>
        /// Picks the provider actually used.
        /// </summary>
        /// <remarks>
        /// With no provider supplied, a <c>content-type="json"</c> block is still
        /// self-contained — it needs no database — so <see cref="JsonTemplateDataProvider"/> stands
        /// in. It declines every other block by returning null, which the engine reads as "no data
        /// source" and renders the template once. When
        /// <see cref="TemplateOptions.ThrowIfQueryPresent"/> is set, the strict provider sits
        /// behind it and turns that decline into an error.
        /// </remarks>
        private static ITemplateDataProvider Effective(
            ITemplateDataProvider? provider, TemplateOptions options)
        {
            if (provider is not null)
                return options.ThrowIfQueryPresent
                    ? TemplateDataProviders.Compose(provider, StrictNoDataProvider.Instance)
                    : provider;

            return options.ThrowIfQueryPresent
                ? TemplateDataProviders.Compose(SelfContained, StrictNoDataProvider.Instance)
                : SelfContained;
        }

        /// <summary>Handles blocks that carry their own data, needing no external source.</summary>
        private static readonly JsonTemplateDataProvider SelfContained = new();

        /// <summary>
        /// Wraps the caller's data model. A null model still yields one entry, so a chain always
        /// exists and unmatched markers collapse rather than leaking raw <c>{{marker}}</c> syntax.
        /// </summary>
        private static List<DbQueryParams> ToModels(object? dataModel)
            => new List<DbQueryParams> { new DbQueryParams { DataModel = dataModel } };

        private static Uri? ParentPathToUri(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            // a base path always denotes a directory, so it needs a trailing separator for
            // relative resolution — http(s) bases included, or the last segment is discarded
            if (!path!.EndsWith("/", StringComparison.Ordinal)
                && !path.EndsWith("\\", StringComparison.Ordinal))
            {
                path += Uri.TryCreate(path, UriKind.Absolute, out var asUri) && !asUri.IsFile
                    ? '/'
                    : Path.DirectorySeparatorChar;
            }
            return new Uri(path);
        }

        /// <summary>
        /// Used when <see cref="TemplateOptions.ThrowIfQueryPresent"/> is set. The engine only
        /// consults a provider when a block actually needs one, so reaching this means the
        /// template wanted data and none was supplied.
        /// </summary>
        private sealed class StrictNoDataProvider : ITemplateDataProvider
        {
            public static readonly StrictNoDataProvider Instance = new();

            public ValueTask<IReadOnlyList<dynamic>?> GetDataAsync(
                TemplateDataRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException(
                    "This template contains an <h-embedded-data> query, but it was rendered "
                    + "without a data source and TemplateOptions.ThrowIfQueryPresent was set. "
                    + "Pass a DbConnection, a TemplateConnectionFactory, or an "
                    + "ITemplateDataProvider.");
        }
    }
}
