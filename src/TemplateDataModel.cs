using System;

namespace Com.H.Text.Template2
{
    /// <summary>
    /// One data model in the chain available to a template, together with the marker syntax its
    /// values are addressed by.
    /// </summary>
    /// <remarks>
    /// The chain starts with the model the caller passed to <c>RenderContent</c> (default
    /// <c>{{ }}</c> markers) and grows as data blocks execute: each result row is appended with
    /// the markers declared on its <c>&lt;h-embedded-data&gt;</c> tag. Later entries win when two
    /// models could fill the same marker, so a row value overrides an outer value of the same
    /// name. Data providers receive the whole chain, which is how a nested template's query can
    /// bind values from its parent's current row.
    /// </remarks>
    public sealed class TemplateDataModel
    {
        /// <summary>
        /// The values. Anonymous object, dictionary, JSON string, <c>JsonElement</c>, or any
        /// object with matching property names.
        /// </summary>
        public object? Model { get; set; }

        /// <summary>
        /// The regex addressing this model's values. Must define named groups
        /// <c>open_marker</c>, <c>param</c> and <c>close_marker</c> — the same shape
        /// <c>Com.H.Data.Common</c>'s <c>DbQueryParams.QueryParamsRegex</c> uses, so a model
        /// passes through to a query unchanged.
        /// </summary>
        /// <remarks>
        /// The convention is one close marker (<c>}}</c>) for everything, with the open marker
        /// carrying the meaning, and <c>{{</c> always accepted as the generic form:
        /// <code>
        /// (?&lt;open_marker&gt;\{\{)(?&lt;param&gt;.*?)?(?&lt;close_marker&gt;\}\})           // generic
        /// (?&lt;open_marker&gt;\{\{|\{row\{)(?&lt;param&gt;.*?)?(?&lt;close_marker&gt;\}\})   // generic or {row{…}}
        /// </code>
        /// Build one from a marker pair with <see cref="PatternFor"/>.
        /// </remarks>
        public string MarkerPattern { get; set; } = DefaultPattern;

        /// <summary>The generic marker pattern: <c>{{name}}</c>.</summary>
        public const string DefaultPattern =
            @"(?<open_marker>\{\{)(?<param>.*?)?(?<close_marker>\}\})";

        /// <summary>
        /// Builds a marker pattern from an open marker and an optional close marker, escaping
        /// both. Passing only an open marker yields the usual asymmetric form
        /// (<c>{v1{name}}</c>); passing <paramref name="alsoGeneric"/> additionally accepts
        /// <c>{{name}}</c>.
        /// </summary>
        public static string PatternFor(string? openMarker, string? closeMarker = null, bool alsoGeneric = false)
        {
            var open = string.IsNullOrEmpty(openMarker) ? "{{" : openMarker!;
            var close = string.IsNullOrEmpty(closeMarker) ? "}}" : closeMarker!;

            var opens = System.Text.RegularExpressions.Regex.Escape(open);
            if (alsoGeneric && open != "{{")
                opens = @"\{\{|" + opens;

            return "(?<open_marker>" + opens + ")"
                 + "(?<param>.*?)?"
                 + "(?<close_marker>" + System.Text.RegularExpressions.Regex.Escape(close) + ")";
        }

        /// <summary>
        /// Text substituted when a marker has no matching value, or the value is null
        /// (default <c>null</c>).
        /// </summary>
        public string NullValue { get; set; } = "null";
    }
}
