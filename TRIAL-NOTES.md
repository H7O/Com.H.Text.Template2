# Trial notes — Com.H.Text.Template2

**For:** whoever is using this library in a real project before it goes to NuGet.
**From:** the session that maintains the library.
**Date:** 2026-08-03 · package still **unpublished** (version 10.0.0), so breaking changes are free.

This is a transient document. Delete it once the trial concludes.

---

## Which version do you have?

```
grep -c "{html{" src/TemplateEngine.cs       # 1 or more → has encoders (this note applies)
dotnet test                                  # 85 tests → current
```

If `{html{` is absent you have the pre-encoder snapshot; everything below marked **NEW** is what
you're missing.

---

## What changed, and why

### NEW — HTML escaping, because you were right that there wasn't any

You observed: *"The templating library does no HTML escaping — at all... for HTML email every
`{{value}}` is an injection point. An issuer named `Smith & Sons <Holdings>` would break the
markup."*

Correct, and verified — there was no encoder anywhere in the source. There are now two markers:

```html
<td>{{issuer}}</td>       <!-- verbatim: Smith & Sons <Holdings>   (unchanged default) -->
<td>{html{issuer}}</td>   <!-- encoded:  Smith &amp; Sons &lt;Holdings&gt; -->
<a href="?q={url{term}}"> <!-- percent-encoded -->
```

- `{html{…}}` is `WebUtility.HtmlEncode`, so it escapes `& < > " '` — safe in text **and** in a
  **quoted** attribute.
- Named `{html{` not `{h{` because `{h{` already means *HTTP header* in DBToRestAPI's `regex.xml`.
- No `{attr{`: it would be a pure synonym, since HtmlEncode already handles quotes.
- `null-value` text is **not** encoded — the template author wrote it, so
  `null-value="<em>n/a</em>"` stays markup.
- Encoders address whatever data is in scope, so they work regardless of a block's own
  `open-marker`.

**Your `dbo.fn_HtmlEncode` workaround still works** and nothing forces a change. But `{html{}}`
is preferable going forward: escaping is a property of the output format, not logic, so a query
that forgets it fails silently whereas the template shows you at the point of use.

### On the `null` observation

You noted: *"a missing placeholder renders the literal word `null`, so template queries use
`ISNULL(...)` throughout."*

Half right, and the distinction matters — I tested it rather than reasoning about it:

| Where the marker sits | A null becomes |
|---|---|
| Inside the **query** | a real SQL `NULL` (a bound `DBNull` parameter) |
| Inside the **body** | the literal four characters `null` |

So `declare @name nvarchar(50) = {{name}};` gets a genuine `NULL` — **`ISNULL` is not needed in
queries.** Only the body side needs it, and there `null-value=""` on the data block is usually
cleaner than wrapping every column:

```html
<h-embedded-data null-value=""><![CDATA[ … ]]></h-embedded-data>
```

An absent key binds as `NULL` too, so a mistyped parameter name fails as a null comparison
rather than a syntax error.

---

## Marker cheat sheet

| Marker | Meaning |
|---|---|
| `{{name}}` | value, verbatim |
| `{html{name}}` | value, HTML/XML encoded |
| `{url{name}}` | value, percent-encoded |
| `{now{fmt}}` `{tomorrow{fmt}}` `{yesterday{fmt}}` | date, **current culture** |
| `{uri{.}}` `{uri{./}}` | the including template's folder |

One close marker (`}}`) for everything; the open marker carries the meaning. Setting
`open-marker="{v1{"` on a block makes it accept **both** `{v1{name}}` and `{{name}}`.

## Behaviours that surprise people

- A `<h-embedded-data>` block repeats the **whole file** per row. Scope it by putting the query
  in its own file and including it — that is also how a section collapses to nothing on zero rows.
- **One query per file.** A second `<h-embedded-data>` throws (it used to be silently ignored).
- **No database supplied** ≠ **query returned nothing**: the first renders once from your data
  model with the query skipped; the second renders nothing.
- Values are **never re-scanned**. A row containing `{{x}}` or `<h-embedded-template>` is emitted
  verbatim — that closes template injection and SSRF, and it is why HTML escaping had to be
  explicit.
- `pre-render="true"` genuinely substitutes text into SQL now (it used to be a no-op gate). It is
  rejected unless `allowPreRender: true`, and a template using it must quote its own values.

## Async

Every `RenderContent` has a `RenderContentAsync` twin; the engine is natively async end to end.
Prefer the async form in a web app — the sync overloads block.

---

## Questions I'd like answered from real use

Paste answers back to the library session; each one is a decision I'd rather make from evidence
than from taste. **Nothing here is committed to — say if a question is irrelevant to your project.**

1. **Encoders** — did `{html{}}` cover everything, or did you reach for something it doesn't do
   (`{json{}}` for values inside a JSON template, `{csv{}}` for quote-doubling, `{js{}}` for a
   script block)? I deliberately shipped only two rather than guessing.

2. **The `null` body default** — is `null-value=""` per block enough, or do you find yourself
   setting it on every block? If the latter, it should probably be settable once at the call site,
   or the default should change. Changing the default breaks legacy parity, so I want a real
   reason.

3. **One query per file** — did that force file splits that felt artificial? If a real report
   wanted two independent queries in one document, say so; the restriction exists because rows
   repeat the whole file, but it is not the only possible design.

4. **Marker collisions** — did `{{ }}` clash with anything in your output (CSS blocks, JS
   template literals, Handlebars, Vue)? If yes, was `open-marker` enough to resolve it?

5. **Connection lifetime** — the connection you pass is used for every query including nested
   templates, and never closed by the library. Did that fit, or did you want per-query
   connections (the `DbTemplateDataProvider(factory)` overload)?

6. **Error messages** — when something was wrong (bad CDATA, two data tags, include cycle,
   missing connection), did the exception tell you enough to fix it without reading the source?
   These were written blind and are the easiest thing to improve.

7. **Anything you had to work around.** The most useful answer. If you wrote a helper, a wrapper,
   or a SQL function to compensate for something the library should do, that is the strongest
   signal available — `dbo.fn_HtmlEncode` is exactly what produced the encoders above.

8. **Async** — did you use `RenderContentAsync`, and did anything about it feel wrong under a
   web request (cancellation, connection pooling, timeouts)?

---

## State

- **85 tests**, all green. Every README example is executed by `DocumentationExamplesTests.cs`,
  so the docs cannot drift from behaviour.
- `LegacyParityTests.cs` pins compatibility with the original `Com.H.Text.Template`; its expected
  values were captured by running the **original** engine.
- `SecurityTests.cs` pins the injection properties.
- Targets `netstandard2.0`, `net8.0`, `net9.0`, `net10.0`. Only dependency: `Com.H.Data.Common`.
- Full docs in `README.md`; architecture and rejected alternatives in `DESIGN.md`.
