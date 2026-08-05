using System;

namespace Com.H.Text.Template2
{
    /// <summary>
    /// The occasional settings for a render. Every one has a sensible default, so most calls
    /// never mention it.
    /// </summary>
    /// <remarks>
    /// These live in one object rather than as a tail of optional parameters: the render methods
    /// already take a template, a data source and a data model, and appending a setting to every
    /// overload each time one is added produces signatures nobody can read at the call site.
    /// </remarks>
    /// <example>
    /// <code>
    /// var html = await template.RenderContentAsync(connection, model, new TemplateOptions
    /// {
    ///     BasePath = templateFolder,
    ///     CommandTimeout = 30,
    ///     ThrowOnUnresolvedMarker = env.IsDevelopment(),
    /// });
    /// </code>
    /// </example>
    public sealed class TemplateOptions
    {
        /// <summary>
        /// Base path for resolving nested template references. Defaults to the application base
        /// directory. Ignored when rendering from a <see cref="Uri"/>, which resolves relative to
        /// itself.
        /// </summary>
        public string? BasePath { get; set; }

        /// <summary>Command timeout for embedded queries, in seconds. Null uses the provider default.</summary>
        public int? CommandTimeout { get; set; }

        /// <summary>Referrer header for templates fetched over http(s).</summary>
        public string? Referrer { get; set; }

        /// <summary>User-agent header for templates fetched over http(s).</summary>
        public string? UserAgent { get; set; }

        /// <summary>
        /// When true, a template containing an <c>&lt;h-embedded-data&gt;</c> query rendered
        /// <i>without</i> a data source throws instead of skipping the query.
        /// </summary>
        /// <remarks>
        /// Off by default, so one template can be rendered both with and without a database — the
        /// query is skipped and the template renders once from the models in scope. Turn it on for
        /// templates that must never render without their data.
        /// </remarks>
        public bool ThrowIfQueryPresent { get; set; }

        /// <summary>
        /// When true, a marker no data model in scope can fill throws instead of rendering as an
        /// empty string.
        /// </summary>
        /// <remarks>
        /// Off by default, because a report should not show a placeholder word to its reader. The
        /// cost of that is silence: a mistyped <c>{{naem}}</c> simply disappears. Turning this on
        /// in development — and leaving it off in production — buys the diagnostic without the
        /// ugly output.
        /// </remarks>
        public bool ThrowOnUnresolvedMarker { get; set; }

        internal static readonly TemplateOptions Default = new();
    }
}
