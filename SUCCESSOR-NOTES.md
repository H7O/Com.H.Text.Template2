# Notes for whoever designs Com.H.Text.Template3

**Date:** 2026-08-04
**Status:** not needed, deliberately

This is **not** an architecture document. It is a note to future-us about how to approach one,
and — more usefully now — what building Template2 actually taught.

Nothing here is a commitment. Template2 works, and most of what this file originally proposed for
a successor was built into it instead.

---

## Read this first: the successor already happened

An earlier version of this file planned Template3 as "the radical departure from the 2016
engine." That departure is Template2. It has its own engine, no `Com.H` dependency, native async,
and pluggable data sources. `DESIGN.md` records what was decided and what was rejected.

So the bar for a Template3 is now much higher. It should exist only if something is wrong that
**cannot be fixed inside Template2** — and given the engine is now ours, very little qualifies.
Ask hard whether a new package is really the answer before starting one.

## Naming is already decided

- Package **and** namespace: `Com.H.Text.Template3`. Identical, always.
- Its own package, not a namespace inside Template2. Each generation carries its own
  dependencies; bundling forces consumers to take the union.
- The rule for users stays one sentence: **take the highest-numbered `Com.H.Text.Template*`
  package.**

Rejected: a package named `Com.H.Text.Template` holding namespace `Com.H.Text.Template2` — a
package that doesn't contain the namespace it is named after is actively misleading.

Rejected: a long descriptive name. Descriptive names get forgotten, and an old name often fits
better than the "better" one meant to replace it. A number is inelegant and unambiguous;
unambiguous wins.

---

## The one principle not up for debate

**SQL is a first-class citizen, not an implementation detail to abstract away.**

The evidence is not theoretical. A **DBA with no software-development background** built massive
critical automation on the 2016 engine — scheduled calculations, CSV to SFTP for downstream
systems, PDF reports to management. When he left, his successor picked it up easily. When the
supporting DevOps engineer left, so did his. Both because they knew SQL.

That is a decade of operational continuity bought by not inventing a syntax. It also shortens
reasoning time for AI agents, which already know SQL and would have to be taught a DSL.

**SQL for logic, the output format for presentation.** Do not fuse them.

## The question Template2 answered — and the answer

*Does the engine get any logic constructs?* **No**, and it has now been used in anger without
them. `case when` covers conditional formatting, `coalesce` covers placeholder text, an empty
result set covers "hide this section", and nesting covers scoping. Nothing in the package's first
consumer or in either production reporting engine needed a template-level `if`.

Treat that as settled unless a concrete case appears that SQL genuinely cannot express — and
write the case down before designing for it.

---

## What Template2 learned that a successor should not relearn

These cost real debugging. Each is pinned by a test.

- **Never re-scan a substituted value.** Filling markers and then searching the result for more
  markers, or for include tags, is template injection and SSRF. It was also quadratic: 120 KB of
  marker-shaped data took 63 seconds.
- **Resolve markers per key, innermost first.** Whole-model shadowing silently drops caller
  values in the ordinary "some values from code, the rest from a query" case.
- **A named marker must not fall back.** Otherwise naming it buys nothing.
- **Alternate marker sets as complete pairs.** Alternating open-against-open and
  close-against-close accepts `{{name]]`.
- **Escape markers before building a regex.** The legacy engine didn't: `[[` silently matched
  nothing and `<%` threw an `XmlException`.
- **Materialise rows before returning them.** Master-detail runs a nested query on the same
  connection; a still-open reader breaks it.
- **State who disposes a connection.** The engine cannot infer whether a factory handed back a
  shared connection or a fresh one.
- **A malformed tag must be an error, not silently rendered.** A tag that fails to parse would
  otherwise publish its query — and any plaintext `connection-string` — into the output.
- **Formatting follows the current culture.** Getting this wrong silently anglicises dates and
  decimal separators in localised templates.

## Where to look

### This repository — the current engine

Start here, not from first principles. `DESIGN.md` lists every decision with its rejected
alternatives, and the test suite is the specification: `LegacyParityTests` (the compatibility
contract), `SecurityTests` (injection properties), `ModelChainTests` and `MarkerPatternTests`
(resolution rules), `DocumentationExamplesTests` (every README example, executed).

### The newest application in the family — how we build applications now

Worth noting it does **not** use the templating engine at all. When built from scratch with
current practices, the engine wasn't reached for. That is evidence about fit. What it does have,
and what a successor should copy:

| Pattern | What to take |
|---|---|
| a connection factory | `Create(connectionStringName)` — connections resolved **by name**, plus a tracking wrapper |
| an encrypting configuration | `IConfiguration` with a decrypting `GetConnectionString(name)` |
| a marker-convention file | the convention Template2 adopted: one close marker, the open marker carries the meaning, `{{…}}` always accepted |
| a numbered middleware pipeline | an explicitly ordered, numbered pipeline |
| an HTTP executor module | module shape: public interface + `Options` + `Models/` + `Internal/` + DI registration |

### The deployed legacy reporting engines — what production needs

The requirements list, not a design to copy. Their busiest templates and their master-detail
specimens (a main template including a rows template) show what real reports need.

### `Com.H.Threading.Scheduler` — what *not* to repeat

`VP.Sql` textually substitutes variables into SQL and then executes with **no parameters at
all**. There is no safe path in that processor. Whatever a successor does, values must reach a
database as parameters, with no textual route available even as an option.

---

## Constraints that still hold

- **`TemplateMultiDataRequest`'s `ConnectionString` / `ContentType` / `PreRender` cannot be
  removed from `Com.H`** — deployed reporting engines read all three. See the NOTE at the top of
  `Com.H/src/Text/Template/TemplateExtensions.cs`. Template2 sidesteps this by not depending on
  `Com.H` at all.
- **The 2016 security posture was of its era.** Corporate sites were plain HTTP; plaintext
  connection strings in a backend config were not an obvious concern. Context, not a lapse to
  litigate.
- **Marker syntax is permanent.** `open-marker="{v1{"` has been carried for a decade. Whatever a
  successor chooses, it will live just as long.

## If the exercise ever runs

1. Read `DESIGN.md` and the test suite. Most questions are already answered there.
2. Establish what Template2 cannot do that the new thing must. If the list is short, fix
   Template2 instead.
3. Only then design, and design it DI-first: an interface in the container, an options type,
   sources resolved by name, nothing opinionated in the core.
4. Write it as `ARCHITECTURE.md` in a new `Com.H.Text.Template3` repository.

Step 2 is the one that matters. A new generation is expensive; Template2 cost a full redesign
cycle and three restructurings of its public API before it settled.
