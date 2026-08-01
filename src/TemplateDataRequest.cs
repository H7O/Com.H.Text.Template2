using System;
using System.Collections.Generic;
using System.Threading;

namespace Com.H.Text.Template2
{
    /// <summary>
    /// Everything a data provider needs to satisfy one <c>&lt;h-embedded-data&gt;</c> block.
    /// Assembled by the engine from the block's attributes and the current data-model chain.
    /// </summary>
    public sealed class TemplateDataRequest
    {
        /// <summary>
        /// The block's query text — the CDATA content, or the text fetched from the tag's
        /// <c>src</c> URI. Markers such as <c>{{name}}</c> are left un-substituted so the
        /// provider can bind them as real query parameters.
        /// </summary>
        public string? Query { get; set; }

        /// <summary>
        /// The tag's <c>connection-string</c> attribute, verbatim. <see cref="DbTemplateDataProvider"/>
        /// ignores it by default — a template is data, and data should not choose which database
        /// the application talks to. A caller-supplied connection factory may opt in to reading it.
        /// </summary>
        public string? ConnectionString { get; set; }

        /// <summary>The tag's <c>content-type</c> attribute, verbatim (e.g. <c>sql</c>).</summary>
        public string? ContentType { get; set; }

        /// <summary>
        /// True when the tag sets <c>pre-render="true"</c>, asking for values to be substituted
        /// into the query as text before execution. <see cref="DbTemplateDataProvider"/> rejects
        /// this unless explicitly allowed, because textual substitution reintroduces SQL
        /// injection risk.
        /// </summary>
        public bool PreRender { get; set; }

        /// <summary>
        /// All attributes present on the tag, keyed case-insensitively with <c>_</c> normalised
        /// to <c>-</c> (so <c>connection_string</c> and <c>connection-string</c> are the same key).
        /// </summary>
        public IReadOnlyDictionary<string, string?> Attributes { get; set; }
            = new Dictionary<string, string?>();

        /// <summary>
        /// The data-model chain in effect where the block appears: the caller's model first,
        /// then one entry per enclosing data block's current row. Bind markers from every entry —
        /// each carries its own marker syntax — so a nested template's query can use its parent's
        /// row values.
        /// </summary>
        public IReadOnlyList<TemplateDataModel> DataModels { get; set; }
            = Array.Empty<TemplateDataModel>();

        /// <summary>Cancellation token for the render operation.</summary>
        public CancellationToken CancellationToken { get; set; }
    }
}
