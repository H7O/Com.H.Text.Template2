using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Com.H.Text.Template2
{
    /// <summary>
    /// The built-in template-text resolver: local files and http(s).
    /// </summary>
    /// <remarks>
    /// Exposed so a custom <see cref="TemplateContentResolver"/> can delegate to it rather than
    /// reimplement it — a caching or fallback resolver usually wants the standard behaviour for
    /// anything it does not handle itself.
    /// </remarks>
    public static class TemplateContent
    {
        private static readonly Lazy<HttpClient> Shared = new(() => new HttpClient());

        /// <summary>
        /// Reads a template from a local path or an http(s) URL.
        /// </summary>
        /// <param name="uri">Where the template lives.</param>
        /// <param name="attributes">
        /// The requesting tag's attributes. <c>referrer</c>, <c>user-agent</c> and any
        /// <c>header-*</c> entries are applied to an http(s) request; everything else is ignored.
        /// Pass an empty dictionary for the root template.
        /// </param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The template text.</returns>
        /// <exception cref="NotSupportedException">The URI scheme is neither file nor http(s).</exception>
        /// <exception cref="FormatException">A header value is unusable — for example it contains a line break.</exception>
        public static async ValueTask<string?> FetchAsync(
            Uri uri,
            IReadOnlyDictionary<string, string?> attributes,
            CancellationToken cancellationToken = default)
        {
            if (uri is null) throw new ArgumentNullException(nameof(uri));
            cancellationToken.ThrowIfCancellationRequested();

            if (uri.IsFile)
            {
                using var reader = new StreamReader(uri.LocalPath);
                return await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                throw new NotSupportedException(
                    $"Unsupported template uri scheme '{uri.Scheme}' for {uri}. "
                    + "Supported: file, http, https. Supply a TemplateOptions.ContentResolver to "
                    + "read templates from anywhere else.");

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);

            var referrer = Attr(attributes, "referrer");
            if (!string.IsNullOrWhiteSpace(referrer)
                && Uri.TryCreate(referrer, UriKind.Absolute, out var referrerUri))
                request.Headers.Referrer = referrerUri;

            var userAgent = Attr(attributes, "user-agent");
            if (!string.IsNullOrWhiteSpace(userAgent))
                request.Headers.TryAddWithoutValidation("User-Agent", userAgent);

            foreach (var kv in attributes)
            {
                if (!kv.Key.StartsWith("header-", StringComparison.OrdinalIgnoreCase)
                    || kv.Key.Length <= 7
                    || kv.Value is null) continue;

                var name = kv.Key.Substring(7);
                if (kv.Value.IndexOf('\r') >= 0 || kv.Value.IndexOf('\n') >= 0)
                    throw new FormatException(
                        $"The value for header '{name}' contains a line break, which would split "
                        + "the HTTP request. Header values cannot contain CR or LF.");

                if (request.Headers.TryAddWithoutValidation(name, kv.Value)) continue;

                // Content-Type and friends are content headers, which HttpRequestHeaders rejects.
                // A GET has no body to hang them on, so attach an empty one rather than dropping
                // the header silently.
                request.Content ??= new ByteArrayContent(Array.Empty<byte>());
                if (!request.Content.Headers.TryAddWithoutValidation(name, kv.Value))
                    throw new FormatException(
                        $"Header '{name}' could not be added to the template fetch request. "
                        + "Check the header name and value.");
            }

            using var response = await Shared.Value.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

#if NETSTANDARD2_0
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
#else
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
#endif
        }

        private static string? Attr(IReadOnlyDictionary<string, string?> attrs, string name)
            => attrs.TryGetValue(name, out var v) ? v : null;
    }
}
