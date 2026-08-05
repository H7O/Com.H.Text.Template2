using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Com.H.Text.Template2
{
    /// <summary>
    /// Supplies the rows for a template's <c>&lt;h-embedded-data&gt;</c> blocks.
    /// </summary>
    /// <remarks>
    /// <see cref="DbTemplateDataProvider"/> is the ADO.NET implementation. Implement this
    /// yourself to serve template data from anywhere else — a cache, a message store, a service.
    /// </remarks>
    public interface ITemplateDataProvider
    {
        /// <summary>
        /// Returns the rows for one data block.
        /// </summary>
        /// <param name="request">The block's query, attributes, and the current data-model chain.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// The rows (the block's body renders once per row) — an empty sequence collapses the
        /// block's template to nothing. Return <b>null</b> to signal "no data source": the
        /// template then renders once using the existing data models, with the query skipped.
        /// Rows must be fully materialised before returning; the engine gives the provider no
        /// later opportunity to dispose a reader.
        /// </returns>
        ValueTask<IReadOnlyList<dynamic>?> GetDataAsync(
            TemplateDataRequest request,
            CancellationToken cancellationToken = default);
    }
}
