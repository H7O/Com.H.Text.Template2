using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Com.H.Text.Template2
{
    /// <summary>
    /// Renders Com.H templates. Values are <c>{{markers}}</c>; an optional
    /// <c>&lt;h-embedded-data&gt;</c> block runs a query (its rows repeat the template body);
    /// <c>&lt;h-embedded-template&gt;</c> nests other templates.
    /// </summary>
    public static class TemplateExtensions
    {
        // ------------------------------------------------------------- with a DbConnection

        /// <summary>
        /// Renders template content, executing any embedded query against the supplied connection.
        /// </summary>
        /// <param name="content">The template text.</param>
        /// <param name="connection">The connection embedded queries run on.</param>
        /// <param name="dataModel">
        /// Values for the template's <c>{{markers}}</c>. Anonymous object, dictionary, JSON string,
        /// <c>JsonElement</c>, or any object with matching properties. Values reaching a query are
        /// bound as SQL parameters, never substituted as text.
        /// </param>
        /// <param name="contentParentAbsolutePath">
        /// Base path used to resolve nested template references. Defaults to the app base directory.
        /// </param>
        /// <param name="allowPreRender">See <see cref="DbTemplateDataProvider"/>. Off by default.</param>
        /// <param name="commandTimeout">Optional command timeout, in seconds.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>The rendered content.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="connection"/> is null.</exception>
        public static string? RenderContent(
            this string content,
            DbConnection connection,
            object? dataModel = null,
            string? contentParentAbsolutePath = null,
            bool allowPreRender = false,
            int? commandTimeout = null,
            CancellationToken? cancellationToken = null)
            => RenderContentAsync(
                content, connection, dataModel, contentParentAbsolutePath,
                allowPreRender, commandTimeout, cancellationToken)
                .GetAwaiter().GetResult();

        /// <summary>Async form of
        /// <see cref="RenderContent(string, DbConnection, object?, string?, bool, int?, CancellationToken?)"/>.
        /// </summary>
        public static Task<string?> RenderContentAsync(
            this string content,
            DbConnection connection,
            object? dataModel = null,
            string? contentParentAbsolutePath = null,
            bool allowPreRender = false,
            int? commandTimeout = null,
            CancellationToken? cancellationToken = null)
        {
            if (connection is null) throw new ArgumentNullException(nameof(connection));

            return TemplateEngine.RenderAsync(
                content,
                ParentPathToUri(contentParentAbsolutePath),
                ToModels(dataModel),
                new DbTemplateDataProvider(connection, allowPreRender, commandTimeout),
                referrer: null,
                userAgent: null,
                depth: 0,
                cancellationToken ?? CancellationToken.None);
        }

        /// <summary>
        /// Renders the template at a URI, executing any embedded query against the supplied connection.
        /// </summary>
        /// <param name="uri">Location of the template. Local paths and http(s) are both supported.</param>
        /// <param name="connection">The connection embedded queries run on.</param>
        /// <param name="dataModel">Values for the template's <c>{{markers}}</c>; bound as SQL parameters when they reach a query.</param>
        /// <param name="allowPreRender">See <see cref="DbTemplateDataProvider"/>. Off by default.</param>
        /// <param name="commandTimeout">Optional command timeout, in seconds.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <param name="referrer">Optional referrer header, for http(s) templates.</param>
        /// <param name="userAgent">Optional user-agent header, for http(s) templates.</param>
        /// <returns>The rendered content.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="uri"/> or <paramref name="connection"/> is null.</exception>
        public static string? RenderContent(
            this Uri uri,
            DbConnection connection,
            object? dataModel = null,
            bool allowPreRender = false,
            int? commandTimeout = null,
            CancellationToken? cancellationToken = null,
            string? referrer = null,
            string? userAgent = null)
            => RenderContentAsync(
                uri, connection, dataModel, allowPreRender, commandTimeout,
                cancellationToken, referrer, userAgent)
                .GetAwaiter().GetResult();

        /// <summary>Async form of
        /// <see cref="RenderContent(Uri, DbConnection, object?, bool, int?, CancellationToken?, string?, string?)"/>.
        /// </summary>
        public static Task<string?> RenderContentAsync(
            this Uri uri,
            DbConnection connection,
            object? dataModel = null,
            bool allowPreRender = false,
            int? commandTimeout = null,
            CancellationToken? cancellationToken = null,
            string? referrer = null,
            string? userAgent = null)
        {
            if (connection is null) throw new ArgumentNullException(nameof(connection));

            return RenderUriAsync(
                uri,
                ToModels(dataModel),
                new DbTemplateDataProvider(connection, allowPreRender, commandTimeout),
                cancellationToken, referrer, userAgent);
        }

        // ------------------------------------------------------------- with a provider

        /// <summary>
        /// Renders template content using a caller-supplied provider — a per-request connection
        /// factory, a caching layer, or any other <see cref="ITemplateDataProvider"/>.
        /// </summary>
        /// <param name="content">The template text.</param>
        /// <param name="provider">The provider satisfying embedded data blocks.</param>
        /// <param name="dataModel">Values for the template's <c>{{markers}}</c>.</param>
        /// <param name="contentParentAbsolutePath">Base path used to resolve nested template references.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>The rendered content.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="provider"/> is null.</exception>
        public static string? RenderContent(
            this string content,
            ITemplateDataProvider provider,
            object? dataModel = null,
            string? contentParentAbsolutePath = null,
            CancellationToken? cancellationToken = null)
            => RenderContentAsync(content, provider, dataModel, contentParentAbsolutePath, cancellationToken)
                .GetAwaiter().GetResult();

        /// <summary>Async form of
        /// <see cref="RenderContent(string, ITemplateDataProvider, object?, string?, CancellationToken?)"/>.
        /// </summary>
        public static Task<string?> RenderContentAsync(
            this string content,
            ITemplateDataProvider provider,
            object? dataModel = null,
            string? contentParentAbsolutePath = null,
            CancellationToken? cancellationToken = null)
        {
            if (provider is null) throw new ArgumentNullException(nameof(provider));

            return TemplateEngine.RenderAsync(
                content,
                ParentPathToUri(contentParentAbsolutePath),
                ToModels(dataModel),
                provider,
                referrer: null,
                userAgent: null,
                depth: 0,
                cancellationToken ?? CancellationToken.None);
        }

        /// <summary>
        /// Renders the template at a URI using a caller-supplied provider.
        /// </summary>
        /// <param name="uri">Location of the template.</param>
        /// <param name="provider">The provider satisfying embedded data blocks.</param>
        /// <param name="dataModel">Values for the template's <c>{{markers}}</c>.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <param name="referrer">Optional referrer header, for http(s) templates.</param>
        /// <param name="userAgent">Optional user-agent header, for http(s) templates.</param>
        /// <returns>The rendered content.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="uri"/> or <paramref name="provider"/> is null.</exception>
        public static string? RenderContent(
            this Uri uri,
            ITemplateDataProvider provider,
            object? dataModel = null,
            CancellationToken? cancellationToken = null,
            string? referrer = null,
            string? userAgent = null)
            => RenderContentAsync(uri, provider, dataModel, cancellationToken, referrer, userAgent)
                .GetAwaiter().GetResult();

        /// <summary>Async form of
        /// <see cref="RenderContent(Uri, ITemplateDataProvider, object?, CancellationToken?, string?, string?)"/>.
        /// </summary>
        public static Task<string?> RenderContentAsync(
            this Uri uri,
            ITemplateDataProvider provider,
            object? dataModel = null,
            CancellationToken? cancellationToken = null,
            string? referrer = null,
            string? userAgent = null)
        {
            if (provider is null) throw new ArgumentNullException(nameof(provider));

            return RenderUriAsync(uri, ToModels(dataModel), provider, cancellationToken, referrer, userAgent);
        }

        // ------------------------------------------------------------- without a database

        /// <summary>
        /// Renders template content that contains no database query.
        /// </summary>
        /// <param name="content">The template text.</param>
        /// <param name="dataModel">
        /// Values for the template's <c>{{markers}}</c>. Anonymous object, dictionary, JSON string,
        /// <c>JsonElement</c>, or any object with matching properties.
        /// </param>
        /// <param name="contentParentAbsolutePath">
        /// Base path used to resolve nested template references. Defaults to the app base directory.
        /// </param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <param name="throwIfQueryPresent">
        /// When true, an <c>InvalidOperationException</c> is thrown if the template (or one it
        /// nests) contains an <c>&lt;h-embedded-data&gt;</c> query, instead of the query being
        /// skipped. Use it for templates that must never render without their data.
        /// </param>
        /// <returns>The rendered content.</returns>
        /// <remarks>
        /// Use this when the template only substitutes values you already have. If the template
        /// (or one it nests) does contain an <c>&lt;h-embedded-data&gt;</c> query, the query is
        /// by default skipped rather than failing: the template renders once with the supplied
        /// data model. That lets one template be used both with and without a database.
        /// <c>content-type="json"</c> data blocks are self-contained and still render fully.
        /// </remarks>
        /// <example>
        /// <code>
        /// var text = "Hello {{name}}.".RenderContent(new { name = "Ali" });
        /// </code>
        /// </example>
        public static string? RenderContent(
            this string content,
            object? dataModel,
            string? contentParentAbsolutePath = null,
            CancellationToken? cancellationToken = null,
            bool throwIfQueryPresent = false)
            => RenderContentAsync(content, dataModel, contentParentAbsolutePath, cancellationToken, throwIfQueryPresent)
                .GetAwaiter().GetResult();

        /// <summary>Async form of
        /// <see cref="RenderContent(string, object?, string?, CancellationToken?, bool)"/>.
        /// </summary>
        public static Task<string?> RenderContentAsync(
            this string content,
            object? dataModel,
            string? contentParentAbsolutePath = null,
            CancellationToken? cancellationToken = null,
            bool throwIfQueryPresent = false)
            => TemplateEngine.RenderAsync(
                content,
                ParentPathToUri(contentParentAbsolutePath),
                ToModels(dataModel),
                throwIfQueryPresent ? StrictNoDatabaseProvider.Instance : null,
                referrer: null,
                userAgent: null,
                depth: 0,
                cancellationToken ?? CancellationToken.None);

        /// <summary>
        /// Renders the template at a URI, where the template contains no database query.
        /// </summary>
        /// <param name="uri">Location of the template. Local paths and http(s) are both supported.</param>
        /// <param name="dataModel">Values for the template's <c>{{markers}}</c>.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <param name="referrer">Optional referrer header, for http(s) templates.</param>
        /// <param name="userAgent">Optional user-agent header, for http(s) templates.</param>
        /// <param name="throwIfQueryPresent">
        /// When true, an <c>InvalidOperationException</c> is thrown if the template (or one it
        /// nests) contains an <c>&lt;h-embedded-data&gt;</c> query, instead of the query being
        /// skipped.
        /// </param>
        /// <returns>The rendered content.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="uri"/> is null.</exception>
        public static string? RenderContent(
            this Uri uri,
            object? dataModel,
            CancellationToken? cancellationToken = null,
            string? referrer = null,
            string? userAgent = null,
            bool throwIfQueryPresent = false)
            => RenderContentAsync(uri, dataModel, cancellationToken, referrer, userAgent, throwIfQueryPresent)
                .GetAwaiter().GetResult();

        /// <summary>Async form of
        /// <see cref="RenderContent(Uri, object?, CancellationToken?, string?, string?, bool)"/>.
        /// </summary>
        public static Task<string?> RenderContentAsync(
            this Uri uri,
            object? dataModel,
            CancellationToken? cancellationToken = null,
            string? referrer = null,
            string? userAgent = null,
            bool throwIfQueryPresent = false)
            => RenderUriAsync(
                uri,
                ToModels(dataModel),
                throwIfQueryPresent ? StrictNoDatabaseProvider.Instance : null,
                cancellationToken, referrer, userAgent);

        // ------------------------------------------------------------- shared plumbing

        private static async Task<string?> RenderUriAsync(
            Uri uri,
            List<TemplateDataModel> models,
            ITemplateDataProvider? provider,
            CancellationToken? cancellationToken,
            string? referrer,
            string? userAgent)
        {
            if (uri is null) throw new ArgumentNullException(nameof(uri));
            var ct = cancellationToken ?? CancellationToken.None;

            // the URI itself may carry markers (e.g. .../reports/{{reportName}}.html)
            var resolved = TemplateEngine.ResolveUri(uri.OriginalString, null, models);

            var content = await TemplateEngine.FetchAsync(
                resolved, referrer, userAgent,
                new Dictionary<string, string>(), ct).ConfigureAwait(false);

            return await TemplateEngine.RenderAsync(
                content, new Uri(resolved, "."), models, provider,
                referrer, userAgent, depth: 0, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Wraps the caller's data model. A null model still yields one entry, so unmatched
        /// markers render as the null-value text rather than leaking raw <c>{{marker}}</c>
        /// syntax into the output — matching the original engine.
        /// </summary>
        private static List<TemplateDataModel> ToModels(object? dataModel)
            => new List<TemplateDataModel> { new TemplateDataModel { Model = dataModel } };

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
        /// The opt-in strict provider (<c>throwIfQueryPresent: true</c>). The engine only consults
        /// a provider when the template actually contains a data block needing one, so reaching
        /// this means a database-less overload was used on a template that needs a database.
        /// </summary>
        private sealed class StrictNoDatabaseProvider : ITemplateDataProvider
        {
            public static readonly StrictNoDatabaseProvider Instance = new();

            public Task<IEnumerable<dynamic>?> GetDataAsync(
                TemplateDataRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException(
                    "This template contains an <h-embedded-data> query, but it was rendered without a "
                    + "database connection and throwIfQueryPresent was set. Use an overload that takes "
                    + "a DbConnection — for example content.RenderContent(connection, dataModel) — or "
                    + "supply a DbTemplateDataProvider.");
        }
    }
}
