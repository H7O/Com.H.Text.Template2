using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using Com.H.Data;
using Com.H.Text.Template;
using ComHTemplate = Com.H.Text.Template.TemplateExtensions;

namespace Com.H.Text.Template2
{
    /// <summary>
    /// Renders Com.H templates whose embedded queries run against an ADO.NET database.
    /// </summary>
    /// <remarks>
    /// These are convenience entry points over
    /// <see cref="Com.H.Text.Template.TemplateExtensions"/>. They do no rendering themselves —
    /// they build a <see cref="DbTemplateDataProvider"/> and hand it to the engine. Everything the
    /// engine already supports (nested templates, markers, data tags) behaves identically.
    /// </remarks>
    public static class TemplateExtensions
    {
        /// <summary>
        /// Renders template content, executing any embedded queries against the supplied connection.
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
        /// <example>
        /// <code>
        /// using var conn = connectionString.CreateDbConnection("Microsoft.Data.SqlClient");
        ///
        /// var html = @"
        ///     &lt;h-embedded-data&gt;&lt;![CDATA[
        ///         select name, email from users where country = {{country}}
        ///     ]]&gt;&lt;/h-embedded-data&gt;
        ///     &lt;li&gt;{{name}} - {{email}}&lt;/li&gt;"
        ///     .RenderContent(conn, new { country = "JO" });
        /// </code>
        /// </example>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="connection"/> is null.</exception>
        public static string? RenderContent(
            this string content,
            DbConnection connection,
            object? dataModel = null,
            string? contentParentAbsolutePath = null,
            bool allowPreRender = false,
            int? commandTimeout = null,
            CancellationToken? cancellationToken = null)
        {
            if (connection is null) throw new ArgumentNullException(nameof(connection));

            var provider = new DbTemplateDataProvider(connection, allowPreRender, commandTimeout);

            return ComHTemplate.RenderContent(
                content,
                ToQueryParamsList(dataModel),
                contentParentAbsolutePath,
                provider.GetData,
                cancellationToken);
        }

        /// <summary>
        /// Renders the template at a URI, executing any embedded queries against the supplied connection.
        /// </summary>
        /// <param name="uri">Location of the template. Local paths and http(s) are both supported by the engine.</param>
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
        {
            if (uri is null) throw new ArgumentNullException(nameof(uri));
            if (connection is null) throw new ArgumentNullException(nameof(connection));

            var provider = new DbTemplateDataProvider(connection, allowPreRender, commandTimeout);

            return ComHTemplate.RenderContent(
                uri,
                ToQueryParamsList(dataModel),
                provider.GetData,
                cancellationToken,
                referrer,
                userAgent);
        }

        /// <summary>
        /// Renders template content using a caller-supplied provider, for cases needing more control
        /// than the connection-based overloads offer — a per-request connection factory, for instance.
        /// </summary>
        /// <param name="content">The template text.</param>
        /// <param name="provider">The provider to satisfy embedded data requests with.</param>
        /// <param name="dataModel">Values for the template's <c>{{markers}}</c>.</param>
        /// <param name="contentParentAbsolutePath">Base path used to resolve nested template references.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>The rendered content.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="provider"/> is null.</exception>
        public static string? RenderContent(
            this string content,
            DbTemplateDataProvider provider,
            object? dataModel = null,
            string? contentParentAbsolutePath = null,
            CancellationToken? cancellationToken = null)
        {
            if (provider is null) throw new ArgumentNullException(nameof(provider));

            return ComHTemplate.RenderContent(
                content,
                ToQueryParamsList(dataModel),
                contentParentAbsolutePath,
                provider.GetData,
                cancellationToken);
        }

        /// <summary>
        /// Wraps a caller's data model in the engine's parameter model, preserving the default
        /// <c>{{ }}</c> markers.
        /// </summary>
        private static List<QueryParams>? ToQueryParamsList(object? dataModel)
            => dataModel is null
                ? null
                : new List<QueryParams> { new QueryParams { DataModel = dataModel } };
    }
}
