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

Originally this package was ~300 lines wiring `Com.H`'s `Com.H.Text.Template` engine to
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

The original engine did not do this, and the failure was silent: a template mixing caller
values with query results rendered the caller's values as empty. Verified against
`Com.H` 10.2.0 — `Fill([outer, row])` returned `"name=John url="`, losing the URL entirely.

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
string even in strict mode, because a NULL is data, not a mistake. Strict mode run against real
data would otherwise fire on legitimate rows, and an error there would push the switch off in
development, which is the only place it earns its keep. (2026-09-05; pinned by the strict-mode
tests in `ModelChainTests`.)

### Rows are materialised, not streamed

`ITemplateDataProvider` returns `IReadOnlyList<dynamic>` — the type states the contract.

This is required, not merely convenient. In master-detail, a parent's rows repeat the template
while a *nested* template runs its own query on the same connection. A still-open parent reader
would throw *"There is already an open DataReader associated with this Connection."* Streaming
would break master-detail, the pattern the engine exists for. A template also builds its whole document in
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

Templates written for the original engine can carry connection strings — passwords included, in
plaintext, in files that get deployed. Honouring them is opt-in: read the attribute in your own
factory.

### One data block per file

A second `<h-embedded-data>` is an error. The legacy engine silently ignored it while still
rendering its markup from the first block's rows — confusing and undiagnosable. Because a block
repeats the whole file, composing several queries means one file each, which is also how a
section is scoped and how it collapses on zero rows.

### Rough edges of the original engine, and their answers here

- **The provider had to be hand-rolled**, and `pre-render` went through textual substitution
  into SQL — `{{name}}` interpolated *inside a quoted SQL literal*. Values now reach the database
  only as parameters; there is no textual route.
- **The default provider always threw.** Its `Assembly.Load("Com.H.EF.Relational")` reflection
  targets a class that no longer exists. This engine loads nothing by reflection.
- **`connection-string` was a per-block tag attribute**, credentials included. It is ignored now
  (see "Templates do not choose the database").
- **Markers may be asymmetric** — `open-marker="{v1{"` with the close left at `}}`. Still
  supported.
- **Model shadowing** forced the workaround of selecting a caller's value into the query so the
  row would carry it. Per-key resolution makes that unnecessary.
- **No HTML escaping existed**, which pushed encoding towards SQL functions. `{html{…}}` and
  `{url{…}}` put it where the output format is known.

## Deliberate divergences from the legacy engine

Each replaces behaviour that was a defect or a workaround rather than a contract, and each is
documented in `LegacyParityTests`:

1. **Per-key model resolution** instead of whole-model shadowing.
2. **Unresolved markers collapse to empty** instead of emitting the word `null`.
3. **Plain relative paths** instead of the `{uri{.}}` placeholder. The original engine passed an
   include's text straight to `new Uri(...)`, which only accepts absolute URIs, so `{uri{.}}` was
   the only way a template could name its own folder. This engine resolves a relative path against
   the including template, `~/` against `TemplateOptions.BasePath` or the application folder, and
   absolute paths and URLs as written. The placeholder is not recognised at all: it was dropped
   before the first release, and the one downstream application is migrated by replacing
   `{uri{.}}/` with nothing.

Everything else — tags, marker syntax, date placeholders, repeat-per-row, zero-rows-collapses,
case-insensitive names, current-culture formatting — is unchanged, with expected values captured
by running the original engine.

## Open items

- `DbTemplateDataProvider` opens a connection it was handed if it is closed, but only disposes
  what a factory marked `Owned`. Worth confirming that suits pooled connections under load.
- No provider ships for CSV or XML payloads. `JsonTemplateDataProvider` is the shape to copy.
- Rendering is async throughout, but providers materialise. A very large result set is held
  entirely in memory; that is inherent to repeat-per-row rendering rather than a defect, but it
  bounds the sensible result size.
