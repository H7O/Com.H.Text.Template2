# Notes for whoever designs Com.H.Text.Template3

**Date:** 2026-07-29
**Status:** not started, deliberately

This is **not** an architecture document. It is a note to future-us about *how to approach*
writing one — where to look, what is already decided, and what the real questions are. Written
now, while the context is fresh, so that starting the actual design exercise later doesn't begin
with a week of rediscovery.

Nothing here is a commitment. `Com.H.Text.Template2` works and is enough for the foreseeable
future.

---

## Naming is already decided

- Package **and** namespace: `Com.H.Text.Template3`. Identical, always.
- Its own package, not a namespace inside `Template2`. Each generation carries its own
  dependencies; bundling them would force consumers to take the union.
- The rule for users stays one sentence: **take the highest-numbered
  `Com.H.Text.Template*` package.**

Rejected alternative: package named `Com.H.Text.Template` holding namespace
`Com.H.Text.Template2`. A package that doesn't contain the namespace it is named after is
actively misleading — `Com.H.Text.Template` already exists inside `Com.H`.

Rejected alternative: a long descriptive name. Descriptive names get forgotten, and an old name
often fits better than the "better" name meant to replace it. A number is inelegant and
unambiguous; unambiguous wins.

---

## The one principle not up for debate

**SQL is a first-class citizen, not an implementation detail to abstract away.**

This is the single decision that has repeatedly paid off across this ecosystem, and the design
should treat it as a constraint rather than an option.

The evidence is not theoretical:

- A **DBA with no software development background** built massive critical automation on the
  2016 reporting engine — scheduled calculations, CSV files pushed to SFTP for downstream
  systems, PDF reports to management and vendors.
- When he left, **his successor picked it up easily** — because the successor also knew SQL.
- When the DevOps engineer supporting it left, **his replacement picked it up easily** too, for
  the same reason.

That is a decade of operational continuity bought by not inventing a syntax. A bespoke
templating DSL — Angular/Vue-style directives, opinionated tag names, a custom expression
language for `if`/`for` — would have cost all of it. It also shortens the reasoning time for
people *and* for AI agents, because SQL is already known to both.

The division of labour to preserve: **SQL for logic, the output format (HTML/XML/CSV) for
presentation.** Do not fuse them.

---

## The biggest open design question

**Does the new engine get any logic constructs at all?**

The 2016 engine has none — no conditionals, no loops. Everything is pushed into SQL:

```sql
CASE WHEN percentage_change > 0 THEN 'forestgreen'
     WHEN percentage_change < 0 THEN 'red'
     ELSE 'blue' END as color
```

The template then just emits `{{color}}`. That is not an oversight; it is the principle above,
applied consistently. Iteration is the only implicit construct — the body repeats per row.

This is the fork that determines nearly everything downstream. Resist adding logic constructs
because they seem convenient; each one is a step toward the DSL that the evidence above argues
against. If some case genuinely cannot be expressed in SQL, write that case down and design for
it specifically, rather than adding general-purpose logic.

---

## Where to look for inspiration

### `C:\code\H7O\DBToRestAPI` — how we build things now

The most modern application in the ecosystem (~22k lines, net10.0). Worth noting up front:
**it does not use the templating engine at all.** When built from scratch with current
practices, the engine wasn't reached for. That is evidence about fit, and worth understanding
before designing a replacement.

Patterns to carry over:

| Where | What to take |
|---|---|
| `Services/DbConnectionFactory.cs` | `Create(string connectionStringName = "default")` — connections resolved **by name**. Also `TrackedDbConnection`, a wrapper adding lifetime tracking. |
| `Services/IEncryptedConfiguration.cs` | `IEncryptedConfiguration : IConfiguration` with a decrypting `GetConnectionString(name)`. |
| `Middlewares/Step1…Step8` | an explicitly ordered, numbered pipeline — trivially readable, trivially reorderable. |
| `Cache/` | `CacheService` plus typed cache containers; caching as a first-class concern, not an afterthought. |
| `Services/HttpExecutor/` | module shape: public interface + `Options` + `Models/` + `Internal/` + a `ServiceCollectionExtensions` registration. Good template for how a pluggable data source should be packaged. |
| `csproj` | `Condition="'$(DbProviders)' != 'lite'"` on provider packages — build-time trimming of optional dependencies. |

**The connection-string problem is already solved there.** A connection factory plus named,
encrypted configuration means the new engine should accept a factory and resolve by name, and
never see a connection string at all. The plaintext-credentials problem then disappears by
construction rather than by policy.

### `C:\code\legacy_reporting_engine\NDReportingEngine2019` / `2022` — what production actually needs

The requirements list, not a design to copy. Read the templates under
`config/Templates/`, especially:

- `debug1/sub/temp1.txt` — the busiest specimen: custom markers, null value, pre-render,
  nested sub-template include.
- `e08/sample01/tables/table1-details.html` — a real report: per-row body, derived columns
  computed in SQL.

Capabilities they exercise, which a successor must cover:

- Nested templates by URI (`<h-embedded-template>`), resolved relative to the parent (`{uri{.}}`).
- Row iteration — body repeated per result row.
- **One** data block per file — verified 2026-08-01: if a file contains more than one
  `<h-embedded-data>`, only the first executes; the rest are stripped and their markup is filled
  from the first query's rows. Composing several queries into one document therefore requires one
  nested template per query. A successor should decide deliberately whether to keep that
  restriction or support multiple blocks properly.
- Custom markers per block, because `{{ }}` collides with CSS and JSON in HTML output.
  Note markers may be **asymmetric** — `open-marker="{v1{"` with the close left at `}}`.
  Markers are currently interpolated into a regex **without escaping**, so `open-marker="[["`
  silently fails to substitute and `<` throws an `XmlException` (the tag is parsed as XML).
  A successor should escape them, or pick a syntax that cannot collide.
- Date placeholders (`{now{yyyy-MM-dd}}`, `tomorrow`, `yesterday`).
- Output formats beyond HTML: CSV, PSV, XML, plain text.

**Start the design document from this list**, not from first principles. Design docs for generic
concepts sprawl without bound; a concrete list of what production already does is the thing that
bounds it. Most people designing a templating engine don't have one.

### `Com.H.Threading.Scheduler` — what *not* to repeat

`VP.Sql` textually substitutes variables into SQL (`CustomVarsProcessor` → `Fill`) and then
executes with **no parameters at all** — `db.ExecuteQuery(text)`. There is no safe path in that
processor.

Bounded in practice (operator-authored config, not user input), but it is the same class of
mistake as `pre-render="true"`, and it shows how easily "just substitute the text" becomes the
only mechanism. Whatever the new engine does, values must reach a database as parameters, with
no textual path available even as an option.

---

## Already-learned constraints worth writing down

- **`TemplateMultiDataRequest`'s `ConnectionString` / `ContentType` / `PreRender` cannot be
  removed** from `Com.H.Text.Template` — deployed reporting engines read all three. See the NOTE
  at the top of `Com.H/src/Text/Template/TemplateExtensions.cs`. This is why a new generation is
  a new package rather than a refactor.
- **The 2016 security posture was of its era.** Corporate sites were plain HTTP; plaintext
  connection strings at rest in a backend config were not an obvious concern. Not a lapse in
  judgement to litigate — just context for why the old shape looks the way it does.
- **Rendering is currently synchronous.** `RenderContent` is sync, so `Template2`'s provider is
  too, and it blocks on the async query path internally. A genuinely async pipeline is a reason
  for a new generation, not something to retrofit.
- **The `Assembly.Load("Com.H.EF.Relational")` reflection in the old engine is dead** — the class
  it looks for no longer exists in that package. Kept deliberately: a 2016-era deployment with an
  old copy of that DLL might still rely on the drop-in-a-DLL mechanism it was built for. Do not
  reproduce that mechanism; DI solves it properly now.

---

## How to run the design exercise, when the time comes

1. Re-read the production templates above. Write the capability list first.
2. Decide the logic question (the fork above). Everything else follows from it.
3. Decide the marker syntax. **This is permanent** — get it wrong and it is carried for a
   decade, as `open-marker="{v1{"` demonstrates. Note `DBToRestAPI` already has a sharper, more
   customisable variable syntax than the reporting engine; look there first.
4. Only then design the object model, and design it DI-first: an interface in the container, an
   `Options` type, a connection factory resolved by name, pluggable data sources.
5. Write it as `ARCHITECTURE.md` in a new `Com.H.Text.Template3` repository.

Do not rush step 3. It is the one that cannot be revised later.
