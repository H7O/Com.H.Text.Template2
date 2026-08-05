using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Com.H.Text.Template2
{
    /// <summary>
    /// Combines data providers so one template can mix sources.
    /// </summary>
    public static class TemplateDataProviders
    {
        /// <summary>
        /// Asks each provider in turn and takes the first answer.
        /// </summary>
        /// <param name="providers">
        /// Tried in order. A provider that does not recognise a block returns null, so ordering
        /// only matters where two providers would both answer.
        /// </param>
        /// <returns>
        /// A provider that routes each block to whichever member claims it, or null if every one
        /// declines — which the engine reads as "no data source", rendering the template once.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="providers"/> is null.</exception>
        /// <example>
        /// <code>
        /// var provider = TemplateDataProviders.Compose(
        ///     new JsonTemplateDataProvider(),                  // content-type="json"
        ///     new DbTemplateDataProvider(connectionFactory));  // everything else
        ///
        /// var html = await template.RenderContentAsync(provider, model);
        /// </code>
        /// </example>
        /// <remarks>
        /// Deliberately separate providers rather than one that does both: a consumer can replace
        /// the JSON half without touching the SQL half, and a SQL-only application never carries
        /// the JSON logic. Routing is by whatever each provider chooses to inspect — usually
        /// <c>content-type</c> — not by anything the engine imposes.
        /// </remarks>
        public static ITemplateDataProvider Compose(params ITemplateDataProvider[] providers)
        {
            if (providers is null) throw new ArgumentNullException(nameof(providers));
            return new CompositeProvider(providers);
        }

        private sealed class CompositeProvider : ITemplateDataProvider
        {
            private readonly ITemplateDataProvider[] _providers;

            public CompositeProvider(ITemplateDataProvider[] providers) => _providers = providers;

            public async ValueTask<IReadOnlyList<dynamic>?> GetDataAsync(
                TemplateDataRequest request, CancellationToken cancellationToken = default)
            {
                foreach (var provider in _providers)
                {
                    if (provider is null) continue;

                    var rows = await provider.GetDataAsync(request, cancellationToken)
                        .ConfigureAwait(false);
                    if (rows is not null) return rows;
                }
                return null;
            }
        }
    }
}
