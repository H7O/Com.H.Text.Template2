# Com.H.Text.Template2 — design

**Date:** 2026-08-04
**Status:** implemented, unpublished

## What this package is

A templating engine with **no built-in idea of where data comes from**.

It knows three things: how to fill markers, how to repeat a body per row, and how to include one
template in another. SQL, JSON, HTTP, a cache, a queue — every one of those is a substitutable
provider, and two happen to ship in the box.

```
                     TemplateEngine
        (markers, rows, includes — and nothing else)
                           │
      ┌────────────────────┼─────────────────────┐
      │                    │                     │
ITemplateDataProvider  TemplateConnection   TemplateContentResolver
   "give me rows"         Factory             "give me template text"
      │                "give me a           (files/http by default,
      │                 connection"           anything you like)
      ├── DbTemplateDataProvider     (SQL, parameterised)
      └── JsonTemplateDataProvider   (a JSON payload)
```

The only dependency is `Com.H.Data.Common`.

## History: this began as glue, and stopped being it

Originally this package was ~300 lines wiring `Com.H`'s 2016 `Com.H.Text.Template` engine to
`Com.H.Data.Common`. That engine's extension point had always existed and had never been
implemented; this package was the implementation.

It then became its own engine, because the wrapper inherited constraints that could not be fixed
from outside:

| Legacy engine | Native engine |
|---|---|
| synchronous throughout | natively async end to end |
| markers interpolated into a regex **unescaped** — `[[` silently failed, `<%` threw | escaped; any characters work |
| attributes parsed as XML, so `<` in a value threw | tolerant attribute parsing |
| a second data block silently ignored | a loud error |
| include cycles hung | detected at depth 32 |
| substituted values re-scanned — template injection, SSRF, quadratic blowup | values are never re-examined |
| `Fill` replaced markers model-by-model, so the first model consulted hid every other model's values | per-key resolution down the chain |

`Com.H` is no longer a dependency at all. Existing template files still work — same tags,
markers, date placeholders, repeat-per-row semantics — pinned by `LegacyParityTests`.

## The principle everything else follows from

**SQL for logic, the template for presentation.**

No `if`, no `for`, no expression language. Sorting, filtering, conditional colours, running
totals, placeholder text for nulls — SQL already does all of it, and more people can read it.

This is not a stylistic preference; it has been load-bearing operationally. A DBA with no
software-development background built and ran critical automation on the 2016 engine for years,
and two successive handovers — his, and the supporting DevOps engineer's — absorbed it easily
because the logic was in SQL. A bespoke template DSL would have cost that.

## Decisions, and what was rejected

### Values are data, never template syntax

A substituted value is emitted verbatim and never re-examined. This is a security property, not
an optimisation. Re-scanning meant:

- a database row containing `{{apiToken}}` pulled that value out of the caller's model
- a value containing `<h-embedded-template>` was **fetched** — an arbitrary file read / SSRF
- 120 KB of marker-shaped row data took **63 seconds** to render

All three are `SecurityTests`. The engine now locates includes in the *original* text before
filling anything, and fills in a single left-to-right pass.

### Markers resolve per key, innermost first

`{{name}}` searches the current row, then enclosing rows, then the caller's model. A row value
wins a name both have; a caller value the row lacks stays reachable.

The original engine did not do this, and it produced a silent failure in production: a template
mixing caller values with query results rendered the caller's values as empty. Verified against
`Com.H` 10.2.0 — `Fill([outer, row])` returned `"name=Ali url="`, losing the URL entirely.

This is the same per-key merge `Com.H.Data.Common`'s `ReduceToUnique` applies to query
parameters, which is why `{{id}}` always bound correctly *inside* a query while failing in the
body. The two halves now agree.

### A dedicated marker does not fall back

`{invoice{total}}` resolves **only** from the block that declared `{invoice{`. Naming a model is
a promise about which one answered, and a fallback would quietly break it — which is precisely
why giving an inner block its own marker was how collisions were resolved before per-key chaining
existed.

Rejected: a relative `{outer{…}}` / `{parent{…}}` form. Position-based addressing changes meaning
when a template layer is inserted; a name chosen by the author does not.

Marker sets alternate as **complete pairs**, so `{{name]]` does not match. Alternating each side
independently would accept mismatched markers — a silent way to get a wrong answer.

### No `pre-render`, no `null-value`

`pre-render="true"` substituted values into SQL as text. That is the injection vector this
package exists to remove, and its only legitimate use — interpolating an identifier — a caller
can do before rendering. Removed outright; there is no option to turn parameterisation off.

`null-value` is gone too. An unresolved marker renders as an empty string, because a report
should not show a placeholder word to its reader. A template wanting `(none)` says so in its
query via `coalesce`, where the meaning is known. `TemplateOptions.ThrowOnUnresolvedMarker` makes
the silence loud in development.

That check is a typo detector, so it fires only for a name **no** model in scope declares. A name
a model declares with a null value — a `LEFT JOIN` with no match, say — renders as an empty
string even in strict mode, because a NULL is data, not a mistake. The trial project ran into
this rehearsing real e-mails with strict mode on, and an error there would have pushed the switch
off in development, which is the only place it earns its keep. (2026-09-05, from the trial
project; pinned by the strict-mode tests in `ModelChainTests`.)

### Rows are materialised, not streamed

`ITemplateDataProvider` returns `IReadOnlyList<dynamic>` — the type states the contract.

This is required, not merely convenient. In master-detail, a parent's rows repeat the template
while a *nested* template runs its own query on the same connection. A still-open parent reader
would throw *"There is already an open DataReader associated with this Connection."* Streaming
would break the defining reporting-engine pattern. A template also builds its whole document in
memory regardless, so there is nothing to give up.

### Separate providers plus a composer, not one that does everything

```csharp
TemplateDataProviders.Compose(
    new JsonTemplateDataProvider(),
    new DbTemplateDataProvider(connectionFactory));
```

Each provider declines what isn't its by returning null; the first real answer wins. A consumer
can replace one half without touching the other, and a SQL-only application never carries the
JSON logic.

### Content resolution returns text, not a transport

Rejected: a delegate returning `HttpClient`, or one returning `HttpResponseMessage`.

The engine fetches text in three places, and the **root template has no tag** — so an
attribute-keyed `HttpClient` factory is incoherent there. Returning *content* sidesteps that and
subsumes more: caching, blob storage, a database, canned templates in tests, air-gapped
environments.

It also removed code rather than adding it. A REST-backed data block is `src` plus whatever
resolver you supply, so **this package contains no HTTP client of its own** — an earlier
`HttpTemplateDataProvider` that made its own calls was deleted once it became clear `src` already
went through the resolver.

### Occasional settings live in `TemplateOptions`

Sixteen render overloads with a growing tail of optional parameters became unreadable at the call
site. Every overload is now `(source, dataModel, options?, cancellationToken?)`.

### The connection's owner is stated, not guessed

`TemplateConnection(connection, disposeWhenDone)`. A factory may hand back one long-lived
connection for every block or open a fresh one each time, and the engine cannot tell. Saying so
explicitly avoids both leaking and closing a connection the caller still holds.

### Templates do not choose the database

A template's `connection-string` attribute is ignored. A template is data, and data should not
point the application at an arbitrary database. Since the connection now comes from a factory,
this is structural rather than a policy the engine could be talked out of.

Production templates did carry connection strings — with passwords, in plaintext, in files that
get deployed. Honouring them is opt-in: read the attribute in your own factory.

### One data block per file

A second `<h-embedded-data>` is an error. The legacy engine silently ignored it while still
rendering its markup from the first block's rows — confusing and undiagnosable. Because a block
repeats the whole file, composing several queries means one file each, which is also how a
section is scoped and how it collapses on zero rows.

## Evidence from production usage

Surveyed `NDReportingEngine` 2019 and 2022 — both deployed, serving hundreds of reports daily —
and one live trial in an HTML-email project. Findings that shaped the above:

- **Both apps hand-rolled the provider**, identically, and both routed `PreRender` through
  `DataExtensions.Fill`: textual substitution into SQL, with a live template interpolating
  `{{name}}` *inside a quoted SQL literal*.
- **Neither used the legacy engine's default provider.** Its
  `Assembly.Load("Com.H.EF.Relational")` reflection targets a class that no longer exists, so it
  always throws.
- **`connection-string` is a tag attribute**, set per block, credentials included.
- **Markers may be asymmetric** — `open-marker="{v1{"` with the close left at `}}`.
- **The trial project hit the model-shadowing bug**, worked around it by selecting a caller value
  into the query, and reported it. That workaround is no longer needed.
- **The trial project also had no HTML escaping available** and was about to add a SQL
  `fn_HtmlEncode`. That produced `{html{…}}` and `{url{…}}`.

## Deliberate divergences from the legacy engine

Both replace behaviour that was a defect rather than a contract, and both are documented in
`LegacyParityTests`:

1. **Per-key model resolution** instead of whole-model shadowing.
2. **Unresolved markers collapse to empty** instead of emitting the word `null`.

Everything else — tags, marker syntax, date placeholders, repeat-per-row, zero-rows-collapses,
`{uri{.}}` resolution, case-insensitive names, current-culture formatting — is unchanged, with
expected values captured by running the original engine.

## Open items

- `DbTemplateDataProvider` opens a connection it was handed if it is closed, but only disposes
  what a factory marked `Owned`. Worth confirming that suits pooled connections under load.
- No provider ships for CSV or XML payloads. `JsonTemplateDataProvider` is the shape to copy.
- Rendering is async throughout, but providers materialise. A very large result set is held
  entirely in memory; that is inherent to repeat-per-row rendering rather than a defect, but it
  bounds the sensible result size.
