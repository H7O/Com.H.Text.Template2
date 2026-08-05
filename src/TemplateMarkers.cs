using System;
using System.Text.RegularExpressions;

namespace Com.H.Text.Template2
{
    /// <summary>
    /// The marker syntax a template's values are addressed by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A marker pattern is a regex defining the named groups <c>open_marker</c>, <c>param</c> and
    /// <c>close_marker</c>. That is deliberately the same shape as
    /// <c>Com.H.Data.Common</c>'s <c>DbQueryParams.QueryParamsRegex</c>, so the engine carries
    /// data models as <c>DbQueryParams</c> and a template's markers address query parameters with
    /// no translation in between.
    /// </para>
    /// <para>
    /// The convention: one close marker (<c>}}</c>) for everything, with the open marker carrying
    /// the meaning, and <c>{{</c> always accepted as the generic form.
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Generic</b> — <c>{{name}}</c> resolves through the whole model chain: the current row
    /// first, then enclosing rows, then the caller's model.
    /// </description></item>
    /// <item><description>
    /// <b>Dedicated</b> — <c>{invoice{name}}</c> resolves <i>only</i> from the model that declared
    /// <c>{invoice{</c>. That is the point of naming one: it is a guarantee about which model
    /// answered, immune to a nearer row happening to have the same column name.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static class TemplateMarkers
    {
        /// <summary>The generic open marker. A match on this resolves through the model chain.</summary>
        public const string GenericOpenMarker = "{{";

        /// <summary>The close marker shared by every form.</summary>
        public const string CloseMarker = "}}";

        /// <summary>The generic marker pattern: <c>{{name}}</c>.</summary>
        public const string DefaultPattern =
            @"(?<open_marker>\{\{)(?<param>.*?)?(?<close_marker>\}\})";

        /// <summary>
        /// Builds a pattern accepting both the generic <c>{{name}}</c> and a dedicated marker,
        /// escaping both so regex metacharacters are literal.
        /// </summary>
        /// <param name="openMarker">
        /// The dedicated open marker, e.g. <c>{invoice{</c> or <c>[[</c>. Null or empty yields
        /// <see cref="DefaultPattern"/>.
        /// </param>
        /// <param name="closeMarker">
        /// The matching close marker. Defaults to <c>}}</c>, which is the usual choice — one
        /// close marker for everything, with the open marker carrying the meaning. Supply one
        /// when a symmetric pair reads better, e.g. <c>[[</c> with <c>]]</c>.
        /// </param>
        /// <remarks>
        /// The two marker sets are alternated as <b>complete pairs</b>, not open-against-open and
        /// close-against-close. Alternating each side independently would accept mismatched
        /// markers — <c>{{name]]</c> would match — which is a silent way to get a wrong result.
        /// </remarks>
        /// <example>
        /// <code>
        /// TemplateMarkers.PatternFor("{invoice{")
        /// // (?&lt;open_marker&gt;\{\{)(?&lt;param&gt;.*?)?(?&lt;close_marker&gt;\}\})
        /// // |(?&lt;open_marker&gt;\{invoice\{)(?&lt;param&gt;.*?)?(?&lt;close_marker&gt;\}\})
        /// </code>
        /// </example>
        public static string PatternFor(string? openMarker, string? closeMarker = null)
        {
            var close = string.IsNullOrEmpty(closeMarker) ? CloseMarker : closeMarker!;

            if (string.IsNullOrEmpty(openMarker)
                || (openMarker == GenericOpenMarker && close == CloseMarker))
                return DefaultPattern;

            return DefaultPattern + "|" + Pair(openMarker!, close);
        }

        /// <summary>One complete marker set: open, parameter name, close.</summary>
        private static string Pair(string open, string close)
            => "(?<open_marker>" + Regex.Escape(open) + ")"
             + "(?<param>.*?)?"
             + "(?<close_marker>" + Regex.Escape(close) + ")";

        /// <summary>
        /// Verifies a hand-written pattern defines the groups the engine needs, and reports what
        /// is missing rather than silently matching nothing.
        /// </summary>
        /// <param name="pattern">The regex to check.</param>
        /// <param name="source">Where it came from, for the error message.</param>
        /// <exception cref="FormatException">The pattern is invalid or lacks a required group.</exception>
        internal static void Validate(string pattern, string source)
        {
            Regex regex;
            try { regex = new Regex(pattern); }
            catch (ArgumentException ex)
            {
                throw new FormatException(
                    $"{source} is not a valid regular expression: {ex.Message}", ex);
            }

            var names = regex.GetGroupNames();
            foreach (var required in new[] { "open_marker", "param", "close_marker" })
            {
                if (Array.IndexOf(names, required) < 0)
                    throw new FormatException(
                        $"{source} must define a named group '{required}'. A marker pattern looks "
                        + @"like (?<open_marker>\{\{|\{name\{)(?<param>.*?)?(?<close_marker>\}\}). "
                        + "Without it the pattern would silently match nothing.");
            }
        }
    }
}
