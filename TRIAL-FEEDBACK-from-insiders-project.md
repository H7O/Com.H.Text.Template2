# Trial feedback — Com.H.Text.Template2

**From:** the Insiders Disclosure project (HTML notification emails), 2026-08-03
**To:** the library session on the other PC
**Answers to the questions in `TRIAL-NOTES.md`, from real use.**

Context: six HTML email templates, each with one `<h-embedded-data>` query, rendered by a
background worker and delivered over SMTP. Verified end-to-end against a live database and a
local SMTP sink.

---

## The one thing that actually bit me: caller-model values vanish inside a data block

**Not a crash, and arguably documented — but it produced a broken email that looked fine in
every log.**

The worker passes a data model to `RenderContentAsync(connection, model)` containing values the
template cannot compute itself — most importantly `record_url`, the deep link back to the record.
The template also has an `<h-embedded-data>` block that fetches the record's details.

```html
<h-embedded-data null-value=""><![CDATA[
    SELECT i.english_name, e.name AS entity_name
    FROM [insider] i LEFT JOIN [entity] e ON e.id = i.entity_id
    WHERE i.id = {{ref_id}}
]]></h-embedded-data>
...
<a href="{{record_url}}">Review the record</a>
```

`{{ref_id}}` binds correctly **inside the query** (caller model), but `{{record_url}}` in the
**body** rendered as an empty string. The row model shadows the caller model, and a winning model
without the key emits *its own* `null-value` instead of deferring to the older model. With
`null-value=""` — which the notes actively recommend — the failure is **silent**: valid HTML, a
button that goes nowhere, nothing in any log.

**Workaround** (works, and is what shipped): pass the value through the query so it becomes a row
column.

```sql
SELECT {{record_url}} AS record_url,   -- ← caller value promoted into the row
       i.english_name, ...
```

**Why I think this deserves a change rather than just a doc line:**

1. The mixed case — "some values come from the caller, the rest from a query" — is the *normal*
   case for a templated email, not an exotic one. Every one of my six templates needed it.
2. The failure mode is invisible. A missing `{{marker}}` that renders `null` at least shows up;
   with `null-value=""` it renders nothing at all.
3. The workaround puts presentation data (a URL) into the SQL select-list, which cuts against the
   library's own "SQL for logic, template for presentation" principle.
4. It surprised me *after* I had read the docs and the trial notes. I still walked into it.

**Options, roughly in the order I'd consider them:**

- **Fall back through the model chain when the winning model lacks the key** (emit `NullValue`
  only when *no* model in the chain has it). This is what I intuitively expected. It changes
  legacy parity, so it likely needs a flag — but it is the behaviour that makes the mixed case
  just work.
- A `{outer{name}}` / `{model{name}}` marker that explicitly addresses the caller model, skipping
  row shadowing. Explicit, no parity break, but adds a marker.
- Failing both: at minimum, make this the headline example in the README's data-block section —
  "if you pass a data model *and* a query, body markers see the row, not your model" — because
  the current phrasing (a single line under a shadowing rule) did not stop me.

### Answers to the numbered questions

1. **Encoders** — `{html{}}` covered everything. I used it on every database value and never
   wanted `{json{}}`/`{csv{}}`/`{js{}}`. `{url{}}` went unused only because my URLs are built in
   C#. **The encoders are the single most valuable change in this release** — before them I was
   about to add a `dbo.fn_HtmlEncode` and remember to call it in every query, which is exactly the
   silent-failure trap you describe.
2. **`null-value=""`** — set on all six blocks, i.e. every single one. I would take a call-site
   default (or a changed library default) happily. See the shadowing issue above though: with
   `""` the default *hides* mistakes, so if the default changes, the model-chain fallback matters
   more, not less.
3. **One query per file** — no artificial splits. Each email is one record, so one query fits
   naturally. I never needed the parent/child include pattern.
4. **Marker collisions** — none. The templates are inline-CSS HTML with no `{{ }}` of their own.
   Worth noting `{`/`}` appear constantly in `style="..."` attributes and never caused trouble,
   since the pattern needs the doubled brace.
5. **Connection lifetime** — a single caller-supplied connection reused for the whole run fitted
   perfectly (sequential worker, one connection per run, closed by my `using`). I did not need
   the factory overload. Confirming your note: the library leaves the connection open, which is
   exactly what a batch worker wants.
6. **Error messages** — only hit one, and it was mine: I broke a template's SQL and the error
   surfaced as an ordinary SQL Server message identifying the syntax problem. Clear enough. I did
   not trigger the CDATA/two-tag/cycle paths.
7. **Anything worked around** — the `record_url` promotion above. That is the whole list.
8. **Async** — used `RenderContentAsync` throughout with a `CancellationToken`. No issues;
   cancellation and the command timeout both behaved.

### Small observations

- Rendering **one email per recipient** means the same template renders N times per event. With
  the connection reused this was ~3s for 3 recipients including SMTP round-trips — fine, and the
  regex cache means no repeated compilation.
- `FORMAT(i.m_date, 'dd MMM yyyy HH:mm')` in SQL sidesteps the `CultureInfo.CurrentCulture`
  question entirely, which is what your docs recommend and it worked out well for a service that
  may run under an unpredictable culture.
- Vendoring was clean: six files copied into a folder, one `Com.H.Data.Common` package reference,
  no namespace changes needed, built first try on net10.0.
