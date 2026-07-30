# Com.H.Text.Template2 — design

**Date:** 2026-07-29
**Status:** initial implementation

## What this package is

A thin **glue** package. It contains no templating engine and no database layer of its own.
It wires the two together:

```
Com.H                          Com.H.Data.Common
(template rendering engine)    (DbConnection querying, parameterised)
        \                              /
         \                            /
          Com.H.Text.Template2
             (this package — the adapter)
```

Neither base package depends on the other. Only the glue depends on both. A consumer who
wants templating without a database references `Com.H` alone and pays nothing for this.

## Why a separate package rather than merging

Three alternatives were considered and rejected:

1. **Make `Com.H.Data.Common` depend on `Com.H`** and move `DbQueryParams` / `DbQueryResult`
   into `Com.H`. Rejected: it puts ADO.NET code into the base package that every downstream
   library inherits, and `Com.H.Data.Common`'s selling point is having no dependencies.
2. **Put a `Template2` namespace inside `Com.H.Data.Common`.** Rejected: it would then need
   the rendering engine, i.e. a dependency on `Com.H` — the same problem as (1).
3. **Fold everything into `Com.H` and deprecate `Com.H.Data.Common`.** Rejected: at least
   `Com.H.EF.Relational` pins `Com.H.Data.Common [10.1.0.4, 11.0.0)`, and other siblings
   depend on it too. Deprecating it is a migration cost across several repos for no gain.

The glue inverts the dependency: neither base moves, and consumers opt in.

## The seam already existed

`Com.H.Text.Template.TemplateExtensions.RenderContent` already accepts:

```csharp
Func<TemplateMultiDataRequest, IEnumerable<dynamic>?>? dataProviders = null
```

That delegate **is** the extension point. The engine parses the `<h-embedded-data>` tag,
hands the provider the raw query text plus the caller's data models, and renders whatever
rows come back. It has no idea a database exists.

Its own source comments confirm the intent:

> *"no pre-render data model before calling data providers (unless pre-render tag = true) as
> data model is submitted to data providers to allow data providers implement their own sql
> injection protection if needed"*

So the engine deliberately passes the query **un-substituted**, expecting the provider to
parameterise. Nothing ever implemented such a provider — that is what this package is.

### The pre-existing default provider is broken

`TemplateExtensions.GetDefaultDataProcessors` uses `Assembly.Load("Com.H.EF.Relational")`
and reflects for `Com.H.EF.Relational.QueryExtensions.GetDefaultDataProcessors`. That class
no longer exists — `Com.H.EF.Relational` was rewritten as a thin wrapper over
`Com.H.Data.Common` (commit `ebb2c72`) and now only contains `DbContextExtensions`. The
reflection path therefore always throws `NotSupportedException`.

This package sidesteps it entirely by passing an explicit delegate, so **no change to
`Com.H` is required**. Deleting that dead reflection code in `Com.H` is worthwhile
housekeeping but is not a prerequisite.

## SQL injection: the point of the exercise

The old templating story substituted parameter values into SQL **as text**
(`QueryParams.NullReplacement = "null"`, `ReplaceQueryParameterMarkers` doing plain
`string.Replace`). That is injection-prone by construction.

This package never does textual substitution into SQL. Every query goes through
`Com.H.Data.Common`'s `ExecuteQuery`, which turns `{{marker}}` occurrences into real
`DbParameter` objects. Safety is structural, not a matter of remembering to escape.

Consequently `NullReplacement` is intentionally **not** mapped — a null becomes a genuine
`DBNull` parameter rather than the literal text `null`.

### `pre-render="true"`

A template can set `pre-render="true"` on its data tag, which asks the provider to
substitute values into the query text before execution — reintroducing exactly the injection
risk this package exists to remove.

**Default: rejected with a clear exception.** It can be enabled explicitly
(`allowPreRender: true`) because substituting an identifier — a table or column name — cannot
be done with parameters and is a legitimate if sharp-edged need.

### Templates do not choose the database

The engine surfaces a `connection-string` attribute from the template. By default this
package **ignores** it: a template is data, and data should not be able to point the
application at an arbitrary database. Callers who want that behaviour can opt in by
supplying a connection factory and reading the attribute themselves.

## Mapping between the two parameter models

| `Com.H.Data.QueryParams` | `Com.H.Data.Common.DbQueryParams` |
|---|---|
| `DataModel` | `DataModel` — passed through unchanged |
| `OpenMarker` / `CloseMarker` | folded into `QueryParamsRegex` via `Regex.Escape` |
| `NullReplacement` | **deliberately dropped** — see above |

## Result materialisation

`ExecuteQuery` returns a lazy `DbQueryResult<dynamic>` holding an open `DbDataReader`. The
engine's delegate signature returns `IEnumerable<dynamic>?` and gives the provider no
opportunity to dispose afterwards, so rows are materialised before returning and the reader
is closed deterministically.

This costs streaming, which a template engine cannot exploit anyway — it builds the whole
document in memory. The alternative leaks readers.

## Evidence from production usage

Surveyed `NDReportingEngine` 2019 (net5.0) and 2022 (net7.0) — both in production, serving
hundreds of reports daily. They are the only known real-world consumers of the templating
engine, and they confirm the design decisions above rather than contradicting them.

**`connection-string` is a tag attribute, set per data block inside the template file:**

```html
<h-embedded-data content-type="sql"
                 connection-string="Data Source=...;Initial Catalog=...;User ID=...;Password=..."
                 open-marker="{v1{"
                 null-value="null-v1"
                 pre-render="true">
  <![CDATA[ declare @name nvarchar(50) = '{{name}}'; select ... ]]>
</h-embedded-data>
```

Each block therefore names its own database, credentials included, in plaintext, in a file
that is deployed. That is a further argument for this package ignoring the attribute by
default rather than merely a stylistic one.

**Both apps implement the provider delegate themselves,** and identically:

```csharp
public IEnumerable<dynamic>? GetDataProviders(TemplateMultiDataRequest req)
{
    var dc = new DbContext(new DbContextOptionsBuilder<DbContext>()
        .UseSqlServer(req.ConnectionString).Options);

    if (req.PreRender) req.Request = req.Request.Fill(req.QueryParamsList);   // <-- textual
    return dc.ExecuteQuery(req.Request, req.QueryParamsList);                 // <-- parameterised
}
```

This settles the SQL injection question. `PreRender` routes through `DataExtensions.Fill`,
which is regex text replacement — and the specimen template above interpolates `{{name}}`
*inside a quoted SQL literal*. Note the non-pre-render path was already correct; only
`pre-render="true"` is unsafe. Rejecting it by default is therefore both the safe choice and
a narrow one.

Scope of that risk in those apps: parameter values come from scheduler task vars, i.e.
operator-authored config (and, via the SQL value provider, from database content) — not from
end-user input. Bounded, but the mechanism is unsafe the moment any parameter becomes
externally influenced.

**Neither app relies on `GetDefaultDataProcessors`.** Every `RenderContent` call site passes
an explicit provider, so the `Assembly.Load("Com.H.EF.Relational")` reflection is never
reached. It is dead for these two consumers — though a 2016-era deployment was not available
to check, which is the reason to leave it in place.

**Asymmetric markers are real.** Production templates set `open-marker="{v1{"` and leave the
close marker at its default, yielding `{v1{name}}`. `BuildMarkerRegex` escapes and defaults
each marker independently, which handles this; there are now tests pinning that behaviour.

## Open items

- Delete the dead `GetDefaultDataProcessors` reflection in `Com.H` (housekeeping; not blocking).
- `TemplateMultiDataRequest` still carries `ConnectionString` / `ContentType` / `PreRender`,
  which the engine's own `// todo` marks for removal. Doing so is a breaking change to
  `Com.H` and is deliberately deferred.
- Async rendering: the engine's `RenderContent` is synchronous, so the provider must be too.
  A genuinely async path needs an engine-side change.
