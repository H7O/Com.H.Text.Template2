using System;
using System.Collections.Generic;
using System.Threading;
using Com.H.Data.Common;

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

        /// <summary>The tag's <c>content-type</c> attribute, verbatim (e.g. <c>sql</c>).</summary>
        public string? ContentType { get; set; }

        /// <summary>
        /// Every attribute on the tag, keyed case-insensitively with <c>_</c> normalised to
        /// <c>-</c> (so <c>content_type</c> and <c>content-type</c> are one key).
        /// </summary>
        /// <remarks>
        /// The engine attaches no meaning to attributes beyond the handful it parses itself, so
        /// this is the extension point: invent whatever your templates need — <c>database</c>,
        /// <c>tenant</c>, <c>timeout</c>, <c>retries</c> — and interpret them in your provider or
        /// connection factory.
        /// </remarks>
        public IReadOnlyDictionary<string, string?> Attributes { get; set; }
            = new Dictionary<string, string?>();

        /// <summary>
        /// The data-model chain in effect where the block appears: the caller's model first, then
        /// one entry per enclosing data block's current row.
        /// </summary>
        /// <remarks>
        /// Passed straight to <c>Com.H.Data.Common</c>, whose <c>ReduceToUnique</c> merges the
        /// chain per key — which is why a nested block's query can bind a value from its parent's
        /// current row, or from the caller's model, without either shadowing the other.
        /// </remarks>
        public IReadOnlyList<DbQueryParams> DataModels { get; set; } = Array.Empty<DbQueryParams>();

        /// <summary>Cancellation for the render in progress.</summary>
        public CancellationToken CancellationToken { get; set; }
    }
}
