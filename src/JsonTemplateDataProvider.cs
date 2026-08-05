using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Com.H.Text.Template2
{
    /// <summary>
    /// Supplies a template's rows from a JSON payload — written inline in the block, or fetched
    /// into it by the block's <c>src</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Answers only for blocks whose <c>content-type</c> is <c>json</c>, returning null for
    /// everything else, so it composes with other providers via
    /// <see cref="TemplateDataProviders.Compose"/>.
    /// </para>
    /// <para>
    /// It performs no I/O. A block that names a <c>src</c> has already had that URI resolved by
    /// the engine's content resolver before this provider is asked — which is why REST-backed
    /// data needs no HTTP code here, and why swapping
    /// <see cref="TemplateOptions.ContentResolver"/> changes how a REST payload is obtained
    /// (caching, auth, retries) without touching this class.
    /// </para>
    /// </remarks>
    /// <example>
    /// Inline data:
    /// <code>
    /// &lt;h-embedded-data content-type="json"&gt;&lt;![CDATA[
    ///   [ { "name": "Ali" }, { "name": "Sara" } ]
    /// ]]&gt;&lt;/h-embedded-data&gt;
    /// &lt;li&gt;{{name}}&lt;/li&gt;
    /// </code>
    /// The same data from a REST endpoint — the engine fetches it, this provider parses it:
    /// <code>
    /// &lt;h-embedded-data content-type="json" src="https://api.example.com/users"
    ///                  header-Authorization="Bearer {{token}}"&gt;&lt;/h-embedded-data&gt;
    /// &lt;li&gt;{{name}}&lt;/li&gt;
    /// </code>
    /// </example>
    public sealed class JsonTemplateDataProvider : ITemplateDataProvider
    {
        /// <summary>The <c>content-type</c> value this provider answers for.</summary>
        public const string ContentType = "json";

        /// <inheritdoc/>
        public ValueTask<IReadOnlyList<dynamic>?> GetDataAsync(
            TemplateDataRequest request, CancellationToken cancellationToken = default)
        {
            if (request is null
                || !string.Equals(request.ContentType, ContentType, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(request.Query))
            {
                return new ValueTask<IReadOnlyList<dynamic>?>((IReadOnlyList<dynamic>?)null);
            }

            return new ValueTask<IReadOnlyList<dynamic>?>(ParseRows(request.Query!));
        }

        /// <summary>
        /// Turns a JSON payload into rows: array elements each become a row; a single object
        /// becomes one row.
        /// </summary>
        /// <param name="json">The payload.</param>
        /// <returns>One row per array element, or a single row for an object.</returns>
        /// <exception cref="JsonException">The payload is not valid JSON.</exception>
        public static IReadOnlyList<dynamic> ParseRows(string json)
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var rows = new List<dynamic>();

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in doc.RootElement.EnumerateArray())
                    rows.Add(element.Clone());
            }
            else
            {
                rows.Add(doc.RootElement.Clone());
            }
            return rows;
        }
    }
}
