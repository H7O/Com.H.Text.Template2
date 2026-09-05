using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Com.H.Data.Common;

namespace Com.H.Text.Template2
{
    /// <summary>
    /// The rendering engine. Template-file compatible with the original
    /// <c>Com.H.Text.Template</c> engine — same tags, markers, date placeholders and
    /// repeat-per-row semantics, pinned by the test suite — reimplemented natively async with
    /// the legacy sharp edges removed (markers are regex-escaped, attributes are parsed
    /// tolerantly rather than as XML, a second data tag in one file is a loud error instead of
    /// being silently ignored, and include cycles are detected).
    /// </summary>
    internal static class TemplateEngine
    {
        /// <summary>
        /// Nested-template depth limit. Deep enough for any real composition; shallow enough to
        /// turn an include cycle into a clear error rather than a hang.
        /// </summary>
        internal const int MaxDepth = 32;

        private const RegexOptions TagOptions =
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled;

        // Attribute sections tolerate '>' inside quoted values (e.g. close-marker="%>"), which
        // both raw XML parsing and the legacy non-greedy tag regex could not.
        private const string AttrsSection = @"(?<attrs>(?:[^>""]|""[^""]*"")*?)";

        // (?![\w-]) stops <h-embedded-data-extra> being taken for <h-embedded-data>.
        // The CDATA body may not span another tag of the same kind, so a malformed terminator
        // fails loudly instead of silently swallowing the document up to the next one.
        private static readonly Regex DataTagRegex = new(
            @"<\s*h-embedded-data(?![\w-])" + AttrsSection
            + @"(?:/\s*>|>\s*(?:<!\[CDATA\[(?<query>(?:(?!\]\]>|<\s*/?\s*h-embedded-data(?![\w-])).)*)\]\]>)?\s*<\s*/\s*h-embedded-data\s*>)",
            TagOptions);

        private static readonly Regex TemplateTagRegex = new(
            @"<\s*h-embedded-template(?![\w-])" + AttrsSection
            + @">\s*<!\[CDATA\[(?<uri>(?:(?!\]\]>|<\s*/?\s*h-embedded-template(?![\w-])).)*)\]\]>\s*<\s*/\s*h-embedded-template\s*>",
            TagOptions);

        /// <summary>Detects a data tag the strict pattern could not parse, so it can be reported.</summary>
        private static readonly Regex LooseDataTagRegex = new(
            @"<\s*h-embedded-data(?![\w-])", TagOptions);

        private static readonly Regex AttributeRegex = new(
            @"(?<name>[A-Za-z_][\w\-]*)\s*=\s*""(?<value>[^""]*)""",
            RegexOptions.Compiled);


        /// <summary>Transforms a value for the place in the document it is being written into.</summary>
        internal delegate string Encoder(string value);

        /// <summary>
        /// Encoding markers. The engine writes values verbatim by default, because it does not
        /// know whether the output is HTML, CSV or plain text — these let a template say so at
        /// the point of use, where the answer is known.
        /// </summary>
        /// <remarks>
        /// They follow the same convention as the rest of the family: the open marker carries
        /// the meaning, <c>}}</c> closes everything. They address whatever data models are in
        /// scope, so they are unaffected by a block's own <c>open-marker</c>.
        /// </remarks>
        private static readonly (Regex Regex, Encoder Encode)[] Encoders =
        {
            (EncoderRegex("{html{"), WebUtility.HtmlEncode),
            (EncoderRegex("{url{"), WebUtility.UrlEncode),
        };

        private static Regex EncoderRegex(string openMarker)
            => new(Regex.Escape(openMarker) + @"(?<param>.*?)?" + Regex.Escape("}}"),
                   RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>
        /// When set, a marker no model in scope can fill throws instead of collapsing to empty.
        /// </summary>
        /// <remarks>
        /// Ambient rather than a parameter on every method: it is a whole-render setting, and
        /// threading it through the recursion would add a parameter to signatures that otherwise
        /// have nothing to do with it. <see cref="AsyncLocal{T}"/> rather than a static field so
        /// concurrent renders with different settings cannot see each other's value.
        /// </remarks>
        private static readonly AsyncLocal<bool> ThrowOnUnresolved = new();

        internal static bool ThrowOnUnresolvedMarker
        {
            get => ThrowOnUnresolved.Value;
            set => ThrowOnUnresolved.Value = value;
        }

        /// <summary>Ambient for the same reason as <see cref="ThrowOnUnresolvedMarker"/>.</summary>
        private static readonly AsyncLocal<TemplateContentResolver?> Resolver = new();

        internal static TemplateContentResolver? ContentResolver
        {
            get => Resolver.Value;
            set => Resolver.Value = value;
        }

        /// <summary>
        /// Asks for a template's text. The engine has no idea whether that means a file, an HTTP
        /// call, a cache or a database — only the resolver does.
        /// </summary>
        internal static ValueTask<string?> ResolveContentAsync(
            Uri uri, Dictionary<string, string?> attributes, CancellationToken ct)
            => (ContentResolver ?? TemplateContent.FetchAsync)(uri, attributes, ct);

        /// <summary>
        /// Builds the attribute set handed to a content resolver: the tag's own attributes,
        /// marker-filled, with the ambient referrer / user-agent as fallbacks.
        /// </summary>
        private static Dictionary<string, string?> ContentAttributes(
            Dictionary<string, string?> attrs,
            List<DbQueryParams> models,
            string? referrer,
            string? userAgent)
        {
            var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in attrs)
            {
                if (kv.Key.StartsWith(OriginalNameKey, StringComparison.Ordinal)) continue;
                result[kv.Key] = FillAttr(kv.Value, models);
            }

            if (!result.ContainsKey("referrer") && !string.IsNullOrWhiteSpace(referrer))
                result["referrer"] = referrer;
            if (!result.ContainsKey("user-agent") && !string.IsNullOrWhiteSpace(userAgent))
                result["user-agent"] = userAgent;

            // header names are case- and underscore-significant on the wire
            foreach (var kv in attrs)
            {
                if (!kv.Key.StartsWith("header-", StringComparison.OrdinalIgnoreCase)) continue;
                var written = OriginalHeaderName(attrs, kv.Key);
                if (!string.Equals(written, kv.Key, StringComparison.Ordinal))
                {
                    result.Remove(kv.Key);
                    result[written] = FillAttr(kv.Value, models);
                }
            }
            return result;
        }

        // marker patterns are supplied per model and repeat across rows; compiling each once
        // keeps the per-row cost linear
        private static readonly Dictionary<string, Regex> MarkerRegexCache = new();

        private static Regex MarkerRegex(string? pattern)
        {
            var key = string.IsNullOrWhiteSpace(pattern) ? TemplateMarkers.DefaultPattern : pattern!;
            lock (MarkerRegexCache)
            {
                if (!MarkerRegexCache.TryGetValue(key, out var regex))
                {
                    regex = new Regex(key, RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    MarkerRegexCache[key] = regex;
                }
                return regex;
            }
        }

        // ------------------------------------------------------------------ core

        internal static async Task<string?> RenderAsync(
            string? content,
            Uri? parentUri,
            List<DbQueryParams> models,
            ITemplateDataProvider? provider,
            string? referrer,
            string? userAgent,
            int depth,
            CancellationToken ct)
        {
            if (depth > MaxDepth)
                throw new InvalidOperationException(
                    $"Nested template depth exceeded {MaxDepth}. "
                    + "This usually means two templates include each other in a cycle.");

            if (string.IsNullOrWhiteSpace(content)) return content;
            ct.ThrowIfCancellationRequested();

            content = FillDates(content!);

            var dataMatches = DataTagRegex.Matches(content).Cast<Match>().ToList();
            if (dataMatches.Count > 1)
                throw new NotSupportedException(
                    "A template may contain only one <h-embedded-data> block, because its rows "
                    + "repeat the whole template. To compose multiple queries in one document, "
                    + "put each query in its own file and include them with <h-embedded-template>.");

            // Every opening tag must have been parsed. An unparseable one must not fall through
            // and be rendered verbatim — that would publish the query, and any connection-string
            // attribute, straight into the output.
            if (LooseDataTagRegex.Matches(content).Count > dataMatches.Count)
                throw new FormatException(
                    "An <h-embedded-data> tag could not be parsed. Expected "
                    + "<h-embedded-data …><![CDATA[ query ]]></h-embedded-data>, a self-closing "
                    + "<h-embedded-data … />, or a src attribute. Check that the CDATA section is "
                    + "terminated with ]]> and that the closing tag is present.");

            if (dataMatches.Count == 0)
                return await RenderPassAsync(
                    content, parentUri, models, provider, referrer, userAgent, depth, ct)
                    .ConfigureAwait(false);

            var tag = dataMatches[0];
            var attrs = ParseAttributes(tag.Groups["attrs"].Value);

            var markerPattern = MarkerPatternFromAttributes(attrs);
            var contentType = GetAttr(attrs, "content-type");

            var query = tag.Groups["query"].Success ? tag.Groups["query"].Value : null;
            var src = GetAttr(attrs, "src");
            if (string.IsNullOrWhiteSpace(query) && !string.IsNullOrWhiteSpace(src))
            {
                var srcUri = ResolveUri(src!, parentUri, models);
                query = await ResolveContentAsync(
                    srcUri, ContentAttributes(attrs, models, referrer, userAgent), ct)
                    .ConfigureAwait(false);
            }

            var body = content.Remove(tag.Index, tag.Length);

            // The engine has no idea what a data source is. SQL, REST, a queue, a file — every
            // one of those is a provider's business, selected by whatever attributes that
            // provider chooses to honour.
            IReadOnlyList<dynamic>? rows;
            if (string.IsNullOrWhiteSpace(query))
            {
                rows = null;
            }
            else if (provider is not null)
            {
                // The query text is handed over UN-substituted so the provider can bind markers
                // as real parameters. There is deliberately no way to ask for textual
                // substitution instead — that was `pre-render`, and it was an injection vector
                // whose only legitimate use (interpolating an identifier) a caller can do itself.
                rows = await provider.GetDataAsync(new TemplateDataRequest
                {
                    Query = query,
                    ContentType = contentType,
                    Attributes = attrs,
                    DataModels = models.ToList(),
                    CancellationToken = ct
                }, ct).ConfigureAwait(false);
            }
            else
            {
                rows = null; // no data source: render once with the existing models
            }

            if (rows is null)
                return await RenderPassAsync(
                    body, parentUri, models, provider, referrer, userAgent, depth, ct)
                    .ConfigureAwait(false);

            var sb = new StringBuilder();
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                var rowModels = new List<DbQueryParams>(models)
                {
                    new DbQueryParams
                    {
                        DataModel = (object?)row,
                        QueryParamsRegex = markerPattern
                    }
                };
                sb.Append(await RenderPassAsync(
                    body, parentUri, rowModels, provider, referrer, userAgent, depth, ct)
                    .ConfigureAwait(false));
            }
            return sb.ToString();
        }

        /// <summary>One pass over query-free content: fill markers, then resolve includes.</summary>
        private static async Task<string> RenderPassAsync(
            string text,
            Uri? parentUri,
            List<DbQueryParams> models,
            ITemplateDataProvider? provider,
            string? referrer,
            string? userAgent,
            int depth,
            CancellationToken ct)
        {
            // Includes are located in the ORIGINAL text, before any value is substituted. Filling
            // first and searching afterwards would let a database row or REST payload containing
            // <h-embedded-template> trigger a fetch — an arbitrary file read / SSRF primitive.
            // Only text the template author wrote can name a template.
            var includes = TemplateTagRegex.Matches(text).Cast<Match>().ToList();
            if (includes.Count == 0) return FillModels(text, models);

            var sb = new StringBuilder(text.Length);
            var cursor = 0;
            foreach (var m in includes)
            {
                ct.ThrowIfCancellationRequested();

                // literal text between includes is marker-filled; substituted values land in the
                // output and are never revisited
                sb.Append(FillModels(text.Substring(cursor, m.Index - cursor), models));

                sb.Append(await RenderIncludeAsync(
                    m, parentUri, models, provider, referrer, userAgent, depth, ct)
                    .ConfigureAwait(false));

                cursor = m.Index + m.Length;
            }
            sb.Append(FillModels(text.Substring(cursor), models));
            return sb.ToString();
        }

        private static async Task<string?> RenderIncludeAsync(
            Match tag,
            Uri? parentUri,
            List<DbQueryParams> models,
            ITemplateDataProvider? provider,
            string? referrer,
            string? userAgent,
            int depth,
            CancellationToken ct)
        {
            var uriText = tag.Groups["uri"].Value;
            if (string.IsNullOrWhiteSpace(uriText)) return "";

            var attrs = ParseAttributes(tag.Groups["attrs"].Value);
            var subReferrer = FillAttr(GetAttr(attrs, "referrer"), models) ?? referrer;
            var subUserAgent = FillAttr(GetAttr(attrs, "user-agent"), models) ?? userAgent;

            var uri = ResolveUri(uriText, parentUri, models);
            var fetched = await ResolveContentAsync(
                uri, ContentAttributes(attrs, models, referrer, userAgent), ct)
                .ConfigureAwait(false);

            return await RenderAsync(
                fetched, new Uri(uri, "."), models, provider,
                subReferrer, subUserAgent, depth + 1, ct).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------ marker filling

        /// <summary>
        /// Fills markers in a single left-to-right pass over the original text.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Substituted values are never re-examined. That is a security property, not an
        /// optimisation: a value arriving from a database row or a REST payload may itself
        /// contain <c>{{marker}}</c> or <c>&lt;h-embedded-template&gt;</c> text, and re-scanning
        /// would let untrusted data pull other values out of the caller's model — or trigger a
        /// file read. Data is data.
        /// </para>
        /// <para>
        /// Resolution is per key, innermost model first — the same merge
        /// <c>Com.H.Data.Common</c>'s <c>ReduceToUnique</c> applies to query parameters. A row
        /// value wins a name the caller also has; a caller value the row lacks stays reachable.
        /// A <i>dedicated</i> marker stops at the model that declared it, because naming a model
        /// is a promise about which one answered.
        /// </para>
        /// <para>
        /// A name nothing in scope has renders as an empty string, or throws when
        /// <see cref="ThrowOnUnresolvedMarker"/> is set. A name a model <i>declares</i> with a null
        /// value renders as an empty string and never throws: a NULL is data, not a typo. Names
        /// match case-insensitively, and values format with the current culture, as the original
        /// engine did.
        /// </para>
        /// </remarks>
        internal static string FillModels(string text, List<DbQueryParams> models)
        {
            if (models.Count == 0 || string.IsNullOrEmpty(text)) return text;

            // resolve every candidate span against the ORIGINAL text
            var candidates = new List<(int Start, int Length, int ModelIndex, string Name, Encoder? Encode, bool Dedicated)>();
            for (var i = 0; i < models.Count; i++)
            {
                var markerRegex = MarkerRegex(models[i].QueryParamsRegex);

                foreach (var m in markerRegex.Matches(text).Cast<Match>())
                {
                    var name = m.Groups["param"].Value;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // Which alternative of the pattern fired decides how the name is resolved.
                    // The generic {{ }} means "wherever this lives", so it walks the chain. A
                    // dedicated marker such as {invoice{ } names one model on purpose, and that
                    // promise is the whole reason to declare one — so it never falls back.
                    var dedicated = m.Groups["open_marker"].Value != TemplateMarkers.GenericOpenMarker;
                    candidates.Add((m.Index, m.Length, i, name, null, dedicated));
                }

                // encoding markers such as {html{name}} address the models generically, so they
                // work regardless of any dedicated marker a block declares
                foreach (var encoder in Encoders)
                {
                    foreach (var m in encoder.Regex.Matches(text).Cast<Match>())
                    {
                        var name = m.Groups["param"].Value;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        candidates.Add((m.Index, m.Length, i, name, encoder.Encode, false));
                    }
                }
            }
            if (candidates.Count == 0) return text;

            // earliest span first; on a tie the newest model, then the longest match
            candidates.Sort((a, b) =>
            {
                var c = a.Start.CompareTo(b.Start);
                if (c != 0) return c;
                c = b.ModelIndex.CompareTo(a.ModelIndex);
                if (c != 0) return c;
                return b.Length.CompareTo(a.Length);
            });

            var valuesByModel = new IDictionary<string, object>?[models.Count];
            var resolved = new bool[models.Count];

            var sb = new StringBuilder(text.Length);
            var cursor = 0;
            foreach (var c in candidates)
            {
                if (c.Start < cursor) continue; // overlaps an already-emitted substitution

                // A generic marker resolves down the chain, innermost first: a row value wins a
                // name the caller also has, but a caller value the row lacks stays reachable —
                // the same per-key merge Com.H.Data.Common applies to query parameters.
                //
                // A dedicated marker stops at the model that declared it. Naming a model is a
                // promise about which one answered, and a fallback would quietly break it.
                object? value = null;
                var keyKnown = false; // some model in scope declares the name, even if its value is null
                var floor = c.Dedicated ? c.ModelIndex : 0;
                for (var i = c.ModelIndex; i >= floor; i--)
                {
                    if (!resolved[i])
                    {
                        var model = models[i].DataModel;
                        valuesByModel[i] = model is null
                            ? null
                            : DataExtensions.GetDataModelParameters(model);
                        resolved[i] = true;
                    }

                    var values = valuesByModel[i];
                    if (values is not null && values.TryGetValue(c.Name, out var v))
                    {
                        keyKnown = true;
                        if (v is not null)
                        {
                            value = v;
                            break;
                        }
                        // a null here is not an answer; an outer model may still have one
                    }
                }

                sb.Append(text, cursor, c.Start - cursor);
                if (value is null)
                {
                    // Nothing in scope has a value. Collapse to empty rather than emitting a
                    // placeholder word — a report should not show "null" to its reader. A caller
                    // wanting "n/a" says so in the query (coalesce), where the meaning is known.
                    //
                    // The strict check is a typo detector, so it fires only when NO model even
                    // declares the name. A column that exists and is NULL is data, not a mistake,
                    // and must not turn a development render of real data into an error.
                    if (ThrowOnUnresolvedMarker && !keyKnown)
                        throw new KeyNotFoundException(
                            $"No data model in scope has a value for marker '{c.Name}'. "
                            + "This check is on because throwOnUnresolvedMarker was set; without "
                            + "it the marker renders as an empty string.");
                }
                else
                {
                    var rendered = Convert.ToString(value, CultureInfo.CurrentCulture) ?? "";
                    sb.Append(c.Encode is null ? rendered : c.Encode(rendered));
                }
                cursor = c.Start + c.Length;
            }
            sb.Append(text, cursor, text.Length - cursor);
            return sb.ToString();
        }

        // ------------------------------------------------------------------ date placeholders

        private static string FillDates(string content)
            => FillDate(
                FillDate(
                    FillDate(content, "{now{", () => DateTime.Now),
                    "{tomorrow{", () => DateTime.Today.AddDays(1)),
                "{yesterday{", () => DateTime.Today.AddDays(-1));

        private static string FillDate(string content, string open, Func<DateTime> dateFactory)
        {
            if (content.IndexOf(open, StringComparison.Ordinal) < 0) return content;

            var regex = new Regex(Regex.Escape(open) + @"(?<f>.*?)\}\}");
            DateTime? date = null;
            return regex.Replace(content, m =>
            {
                var format = m.Groups["f"].Value;
                if (string.IsNullOrEmpty(format)) return m.Value;
                date ??= dateFactory();
                // current culture, as the original engine used, so localised month and day
                // names in existing templates keep rendering the same way
                return date.Value.ToString(format, CultureInfo.CurrentCulture);
            });
        }

        // ------------------------------------------------------------------ attributes & headers

        /// <summary>
        /// Parses a tag's attributes with a tolerant regex rather than an XML parser, so values
        /// containing characters XML forbids (a bare <c>&lt;</c>, for instance) still work.
        /// Keys are case-insensitive with <c>_</c> normalised to <c>-</c>.
        /// </summary>
        internal static Dictionary<string, string?> ParseAttributes(string attrsText)
        {
            var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(attrsText)) return result;

            foreach (var m in AttributeRegex.Matches(attrsText).Cast<Match>())
            {
                var written = m.Groups["name"].Value;
                var key = written.Replace('_', '-');
                result[key] = m.Groups["value"].Value;
                if (!string.Equals(written, key, StringComparison.Ordinal))
                    result[OriginalNameKey + key] = written;
            }
            return result;
        }

        private static string? GetAttr(Dictionary<string, string?> attrs, string name)
            => attrs.TryGetValue(name, out var v) ? v : null;

        /// <summary>
        /// Builds a block's marker pattern from its attributes.
        /// </summary>
        /// <remarks>
        /// Two tiers. <c>marker="{invoice{"</c> — optionally with <c>close-marker="…"</c> when a
        /// symmetric pair reads better — is the everyday form, generating a pattern that accepts
        /// both the generic <c>{{name}}</c> and the dedicated form.
        /// <c>marker-pattern="…"</c> takes a regex directly, for anything the sugar cannot express
        /// — and is validated, because a pattern missing a named group would otherwise match
        /// nothing at all and fail silently.
        /// </remarks>
        internal static string MarkerPatternFromAttributes(Dictionary<string, string?> attrs)
        {
            var explicitPattern = GetAttr(attrs, "marker-pattern");
            if (!string.IsNullOrWhiteSpace(explicitPattern))
            {
                TemplateMarkers.Validate(explicitPattern!, "The marker-pattern attribute");
                return explicitPattern!;
            }

            var marker = GetAttr(attrs, "marker");
            var closeMarker = GetAttr(attrs, "close-marker");
            return string.IsNullOrEmpty(marker) && string.IsNullOrEmpty(closeMarker)
                ? TemplateMarkers.DefaultPattern
                : TemplateMarkers.PatternFor(marker, closeMarker);
        }

        /// <summary>Marker-fills an attribute value, so headers can carry template data.</summary>
        private static string? FillAttr(string? value, List<DbQueryParams> models)
            => string.IsNullOrEmpty(value) ? value : FillModels(value!, models);

        /// <summary>
        /// Recovers a header attribute's name as written. Attribute keys are normalised so that
        /// <c>connection_string</c> and <c>connection-string</c> are one key, but an HTTP header
        /// name is case- and underscore-significant, so the original spelling is preserved.
        /// </summary>
        private static string OriginalHeaderName(Dictionary<string, string?> attrs, string normalisedKey)
            => attrs.TryGetValue(OriginalNameKey + normalisedKey, out var original) && original is not null
                ? original
                : normalisedKey;

        private const string OriginalNameKey = "\0orig:";

        // ------------------------------------------------------------------ URIs & fetching

        /// <summary>
        /// Resolves a template-supplied URI: <c>{uri{.}}</c>/<c>{uri{./}}</c> placeholders, then
        /// data-model markers, then — new to this engine — plain relative paths against the
        /// including template's location.
        /// </summary>
        internal static Uri ResolveUri(string uriText, Uri? parentUri, List<DbQueryParams> models)
        {
            var parent = parentUri ?? new Uri(AppendSlash(AppContext.BaseDirectory));
            var parentText = parent.AbsoluteUri;
            if (!parentText.EndsWith("/", StringComparison.Ordinal)) parentText += "/";

            uriText = uriText
                .Replace("{uri{./}}", parentText)
                .Replace("{uri{.}}", parentText.Substring(0, parentText.Length - 1));

            uriText = FillModels(uriText, models).Trim();

            if (Uri.TryCreate(uriText, UriKind.Absolute, out var absolute)) return absolute;
            if (Uri.TryCreate(parent, uriText, out var relative)) return relative;

            throw new FormatException($"Invalid template uri: {uriText}");
        }


        private static string AppendSlash(string path)
            => path.EndsWith("/", StringComparison.Ordinal)
               || path.EndsWith("\\", StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;    }
}
