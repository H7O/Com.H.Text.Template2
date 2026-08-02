# Com.H.Text.Template2

A small, natively async templating engine for .NET that can run **SQL queries embedded in the template itself** — or fetch its rows from a REST API.

Output any text format — HTML, XML, CSV, JSON, plain text. There is no template language to
learn: values are `{{markers}}`, and anything that needs *logic* is written in SQL, where you
already know how to express it.

```csharp
using Com.H.Text.Template2;

var output = "Hello {{name}}, you have {{count}} new messages."
    .RenderContent(new { name = "Ali", count = 3 });

// Hello Ali, you have 3 new messages.
```

Add a query and the template repeats once per row:

```csharp
var template = """
    <h-embedded-data><![CDATA[
        select name, price from products where category = {{category}} order by name
    ]]></h-embedded-data>
    <li>{{name}} - {{price}}</li>
    """;

var output = template.RenderContent(connection, new { category = "Accessories" });

// <li>Keyboard - 25</li>
// <li>Mouse - 15</li>
```

`{{category}}` reaches the database as a **real SQL parameter**. It is never concatenated into
the query, so a hostile value cannot change what the query does.

## Installation

```
dotnet add package Com.H.Text.Template2
```

## Design in one line

**SQL for logic, the template for formatting.** There are no `if` or `for` constructs to learn —
sorting, filtering, conditional colours, running totals and formatting are all things SQL already
does well, so the engine doesn't reinvent them. That also means anyone on your team who knows SQL
can maintain the templates.

---

## Example 1 — Filling in values

The simplest use needs no database at all.

```csharp
using Com.H.Text.Template2;

var output = "Hello {{name}}, you have {{count}} new messages."
    .RenderContent(new { name = "Ali", count = 3 });
```

```
Hello Ali, you have 3 new messages.
```

The data model can be an anonymous object, a `Dictionary<string, object>`, a JSON string, a
`System.Text.Json.JsonElement`, or any object with matching property names.

There are also built-in date markers, which need no data model:

```csharp
"Report generated {now{yyyy-MM-dd}}".RenderContent(new { });
// Report generated 2026-08-01
```

`{now{…}}`, `{tomorrow{…}}` and `{yesterday{…}}` all accept standard .NET date format strings.

## Example 2 — A template from a file

```csharp
// greeting.txt:  Hello {{name}}, welcome back.

var output = new Uri(@"C:\templates\greeting.txt").RenderContent(new { name = "Ali" });
```

```
Hello Ali, welcome back.
```

`http://` and `https://` URLs work too, with optional `referrer` and `userAgent` arguments.

## Example 3 — Adding a database query

Wrap a query in `<h-embedded-data>`. Everything else in the template becomes the **body**, which
is rendered once per returned row, with `{{column}}` markers filled from that row.

```csharp
using Com.H.Data.Common;    // CreateDbConnection
using Com.H.Text.Template2; // RenderContent

using var connection = connectionString.CreateDbConnection("Microsoft.Data.SqlClient");

var template = """
    <h-embedded-data><![CDATA[
        select name, price from products where category = {{category}} order by name
    ]]></h-embedded-data>
    <li>{{name}} - {{price}}</li>
    """;

var output = template.RenderContent(connection, new { category = "Accessories" });
```

```html
<li>Keyboard - 25</li>
<li>Mouse - 15</li>
```

`{{category}}` in the *query* is filled from the data model you passed, as a SQL parameter.
`{{name}}` and `{{price}}` in the *body* are filled from each row.

Any ADO.NET database works — SQL Server, PostgreSQL, MySQL, SQLite, Oracle, ODBC, OleDb. The
connection is yours to open and dispose; this package never closes a connection you supplied.

## Example 4 — Keeping the query in the template file

The query does not have to live in your C# source. Put it in the template file, and the file
becomes self-contained — editable by whoever maintains the report, without a rebuild.

```html
<!-- products.html -->
<h-embedded-data><![CDATA[
    select name from products where category = {{category}} order by name
]]></h-embedded-data>
<li>{{name}}</li>
```

```csharp
var output = new Uri(@"C:\templates\products.html")
    .RenderContent(connection, new { category = "Displays" });
```

```html
<li>Monitor</li>
```

## Example 5 — Building a table with nested templates

A template containing a query repeats **in its entirety** once per row. So to keep a table header
out of the repetition, put the rows in their own file and pull it in with
`<h-embedded-template>`:

```html
<!-- index.html -->
<table>
    <tr><th>Product</th><th>Price</th></tr>
    <h-embedded-template><![CDATA[{uri{.}}/rows.html]]></h-embedded-template>
</table>
```

```html
<!-- rows.html -->
<h-embedded-data><![CDATA[
    select name, price from products order by name
]]></h-embedded-data>
<tr><td>{{name}}</td><td>{{price}}</td></tr>
```

```csharp
var output = new Uri(@"C:\templates\index.html").RenderContent(connection);
```

```html
<table><tr><th>Product</th><th>Price</th></tr>
<tr><td>Keyboard</td><td>25</td></tr>
<tr><td>Monitor</td><td>199</td></tr>
<tr><td>Mouse</td><td>15</td></tr></table>
```

`{uri{.}}` resolves to the folder of the template doing the including, so nested templates move
with their parent. Nesting can go as deep as you like, and nested templates work without a
database too.

## Example 6 — Logic belongs in the query

Alternating row colours, conditional formatting, running totals — decide them in SQL and let the
template just place the value:

```html
<!-- rows.html -->
<h-embedded-data><![CDATA[
    select name,
           case when row_number() over (order by name) % 2 = 0
                then '#f0f0f0' else '#ffffff' end as row_colour
    from products order by name
]]></h-embedded-data>
<tr bgcolor="{{row_colour}}"><td>{{name}}</td></tr>
```

```html
<tr bgcolor="#ffffff"><td>Keyboard</td></tr>
<tr bgcolor="#f0f0f0"><td>Monitor</td></tr>
<tr bgcolor="#ffffff"><td>Mouse</td></tr>
```

This is the whole philosophy: no template-level `if`, because SQL's `case when` is better at it
and more people already read it.

## Example 7 — Parameters straight from a web request

Values from a request can be passed directly. They are bound as SQL parameters, so there is
nothing to escape and no injection risk:

```csharp
[HttpGet("catalogue")]
public IActionResult Catalogue([FromQuery] string category)
{
    using var connection = _connectionString.CreateDbConnection("Microsoft.Data.SqlClient");

    var html = _template.RenderContent(connection, new { category });

    return Content(html ?? "", "text/html");
}
```

| `category` value | Result |
|---|---|
| `Accessories` | the two matching rows |
| `Accessories' OR '1'='1` | **no rows** — treated as a category name nobody has |
| `x'; delete from products; --` | **no rows**, and the table is untouched |

The payloads are indistinguishable from any other value that happens to match nothing, which is
exactly what a bound parameter does.

> The one exception is `pre-render="true"`, which asks for values to be pasted into the query as
> text. It is **rejected by default** for this reason — see [Safety](#safety).

## Example 8 — Sections that disappear when there is no data

Because a template with a query repeats per row, **zero rows means it renders as nothing**. Put
the query in a nested template and the surrounding page survives while that section collapses:

```html
<!-- index.html -->
<h1>Catalogue</h1>
<ul><h-embedded-template><![CDATA[{uri{.}}/offers.html]]></h-embedded-template></ul>
<footer>end</footer>
```

```html
<!-- offers.html -->
<h-embedded-data><![CDATA[
    select name from products where category = {{category}}
]]></h-embedded-data>
<li>{{name}}</li>
```

```csharp
new Uri(index).RenderContent(connection, new { category = "Displays" });
// <h1>Catalogue</h1><ul><li>Monitor</li></ul><footer>end</footer>

new Uri(index).RenderContent(connection, new { category = "Nonexistent" });
// <h1>Catalogue</h1><ul></ul><footer>end</footer>
```

No conditional syntax needed — an empty result set is its own "hide this".

## Example 9 — Changing the marker syntax

`{{ }}` can collide with the output format — CSS blocks, JSON, Handlebars-style content. Set a
different marker on the data tag:

```html
<h-embedded-data open-marker="{v1{"><![CDATA[
    select name from products where category = {{category}} order by name
]]></h-embedded-data>
<span style="color:{red}">{v1{name}}</span>
```

```html
<span style="color:{red}">Monitor</span>
```

The body now uses `{v1{name}}`, so the CSS braces are left alone. Note the **query** still uses
`{{ }}` — the marker attributes apply to the body only.

Attributes: `open-marker`, `close-marker` (defaults to `}}`), `null-value`.

The convention across Com.H libraries is **one close marker (`}}`) for everything, with the open
marker carrying the meaning**, and `{{name}}` always accepted as the generic form. So setting
`open-marker="{v1{"` above means the body accepts *both* `{v1{name}}` and `{{name}}`.

Any characters work — they are escaped before use — and `close-marker` is only needed if you
want something other than `}}`.

For full control, `marker-pattern` takes a regex directly:

```html
<h-embedded-data marker-pattern="(?&lt;open_marker&gt;\{\{|\{row\{)(?&lt;param&gt;.*?)?(?&lt;close_marker&gt;\}\})">
```

It must define the named groups `open_marker`, `param` and `close_marker` — deliberately the
same shape as `Com.H.Data.Common`'s `DbQueryParams.QueryParamsRegex`, so a template's markers
address query parameters with no translation in between.

## Example 10 — Escaping values for HTML

The engine writes values **verbatim**, because it does not know whether you are producing HTML,
CSV, JSON or plain text. For HTML that matters: a company called `Smith & Sons <Holdings>` would
break your markup, and a hostile value could inject a `<script>` tag.

Say what you mean at the point of use:

```html
<td>{{issuer}}</td>       <!-- verbatim: Smith & Sons <Holdings> -->
<td>{html{issuer}}</td>   <!-- encoded:  Smith &amp; Sons &lt;Holdings&gt; -->
```

| Marker | Encoding |
|---|---|
| `{{name}}` | none — the value is written exactly as it is |
| `{html{name}}` | HTML/XML text and quoted attribute values |
| `{url{name}}` | percent-encoding, for URLs and query strings |

`{html{…}}` also escapes `"` and `'`, so a **quoted** attribute is safe:

```html
<td title="{html{note}}">…</td>
```

Encoding markers address whatever data is in scope, so they work regardless of a block's own
`open-marker` — and, like every marker, an encoded value is still never re-scanned as template
syntax.

The `null-value` text is **not** encoded: you wrote it, so `null-value="<em>n/a</em>"` stays
markup rather than becoming visible tags.

## Example 11 — One template, with or without a database

A template that contains a query can still be rendered without one. The query is skipped and the
template renders **once**, with `{{markers}}` filled from the data model you passed:

```csharp
var template = """
    <h-embedded-data><![CDATA[
        select name from products where category = {{category}} order by name
    ]]></h-embedded-data>
    <li>{{name}}</li>
    """;

template.RenderContent(connection, new { category = "Accessories" });
// <li>Keyboard</li><li>Mouse</li>

template.RenderContent(new { name = "Placeholder" });
// <li>Placeholder</li>
```

This is useful for previewing a layout, rendering a design-time placeholder, or reusing one
template in a context that has no database.

Note the difference from Example 8:

| Situation | Result |
|---|---|
| No database supplied | Query skipped; template renders **once** from your data model |
| Query ran and matched nothing | Template renders as **nothing** (the section collapses) |

"There is no data source" and "the query found nothing" are deliberately different answers.

For templates that must **never** render without their data, opt into strict mode — it throws
instead of skipping:

```csharp
template.RenderContent(dataModel, throwIfQueryPresent: true);
```

## Example 12 — Data from a REST API, no database at all

With `content-type="json"`, the block's content **is** the data — a JSON array whose elements
become the rows. Point `src` at an API and the template becomes HTTP-driven:

```html
<h-embedded-data content-type="json"
                 src="https://api.example.com/products?category={{category}}"
                 header-Authorization="Bearer {{token}}">
</h-embedded-data>
<li>{{name}} ({{city}})</li>
```

`header-*` attributes become HTTP request headers; `referrer` and `user-agent` are also
available. The JSON can equally be embedded directly:

```csharp
var template = """
    <h-embedded-data content-type="json"><![CDATA[
        [ {"name":"Ali","city":"Amman"}, {"name":"Sara","city":"Dubai"} ]
    ]]></h-embedded-data>
    <li>{{name}} ({{city}})</li>
    """;

template.RenderContent(new { });
// <li>Ali (Amman)</li><li>Sara (Dubai)</li>
```

A single JSON object renders as one row. Nested objects and arrays arrive as raw JSON text.

## Example 13 — SQL in its own file, includes by relative path

A query can live next to the template instead of inside it, via `src`:

```html
<!-- index.html -->
<h-embedded-data src="query.sql"></h-embedded-data>
<li>{{name}}</li>

<!-- query.sql -->
select name from products where category = {{category}} order by name
```

Relative URIs — in `src` and in `<h-embedded-template>` — resolve against the including
template's own location, so a template folder can be moved or deployed anywhere as a unit.
The classic `{uri{.}}` placeholder still works and remains useful when you want to be explicit.

## Async

Every `RenderContent` overload has a `RenderContentAsync` twin. The engine is natively
asynchronous — database queries, file reads and HTTP fetches all await — and the synchronous
overloads are thin wrappers for callers that don't need it:

```csharp
var html = await new Uri(path).RenderContentAsync(connection, new { category = "Displays" });
```

---

## Reference

### Tags

| Tag | Purpose |
|---|---|
| `<h-embedded-data>` | A query. Its CDATA content is the SQL; the rest of the file is the body, repeated per row. |
| `<h-embedded-template>` | Includes another template. CDATA content is its URI; `{uri{.}}` is the including file's folder. |

### Attributes on `<h-embedded-data>`

| Attribute | Meaning |
|---|---|
| `open-marker` / `close-marker` | Marker syntax for the **body** (default `{{` / `}}`). Any characters; escaped before use. A custom open marker also keeps accepting `{{name}}`. |
| `marker-pattern` | A regex with named groups `open_marker` / `param` / `close_marker`, used instead of the pair above. |
| `null-value` | Text substituted for a null column value (default `null`). |
| `src` | Load the query (or, with `content-type="json"`, the data) from a URI instead of CDATA. Relative URIs resolve against the template. |
| `content-type` | `json` makes the block self-contained (Example 12); anything else is carried through to the provider. |
| `header-*`, `referrer`, `user-agent` | HTTP headers used when `src` is fetched. |
| `connection-string` | **Ignored by default** — see [Safety](#safety). |
| `pre-render` | **Rejected by default** — see [Safety](#safety). |

Underscore forms (`connection_string`, `pre_render`, …) are accepted everywhere the dash forms are.

`<h-embedded-template>` accepts `header-*`, `referrer` and `user-agent` too, applied when its
URI is fetched.

### Markers

| Marker | Meaning |
|---|---|
| `{{name}}` | a value, written verbatim |
| `{html{name}}` | a value, HTML/XML encoded (Example 10) |
| `{url{name}}` | a value, percent-encoded |
| `{now{fmt}}`, `{tomorrow{fmt}}`, `{yesterday{fmt}}` | a date, in the current culture |
| `{uri{.}}` / `{uri{./}}` | the including template's folder |

One close marker (`}}`) for everything; the open marker carries the meaning. That is the same
convention `Com.H.Data.Common` and the DBToRestAPI configuration use.

### Things worth knowing

- **One query per template file** — its rows repeat the whole file, so a second
  `<h-embedded-data>` is ambiguous and throws, with a message pointing at nested templates.
- **A query makes the whole file repeat.** Use nesting (Examples 5 and 8) to control what repeats
  and what doesn't.
- **Zero rows renders nothing** for that file. This is distinct from *no database at all* — see
  Example 11.
- **Include cycles are detected** — templates that include each other fail with a clear error
  instead of hanging.

### Choosing an overload

| You have | Call |
|---|---|
| Values only, no query | `content.RenderContent(dataModel)` |
| A template file, no query | `new Uri(path).RenderContent(dataModel)` |
| A query, one connection | `content.RenderContent(connection, dataModel)` |
| A template file with a query | `new Uri(path).RenderContent(connection, dataModel)` |
| A connection per query, or custom rules | `content.RenderContent(provider, dataModel)` |

A database-less overload used on a template that *does* contain a query doesn't fail — the query
is skipped and the template renders once from your data model. See Example 11.

## Safety

| Behaviour | Default | Why |
|---|---|---|
| Parameter values | always bound as `DbParameter` | no textual substitution into SQL, ever |
| `pre-render="true"` | **rejected** | it pastes values into the SQL as text, reintroducing injection risk |
| `connection-string` attribute | **ignored** | a template is data; data should not choose which database the application talks to |

`pre-render` can be enabled with `allowPreRender: true` when a template must interpolate an
*identifier* — a table or column name — which cannot be parameterised. Use it only on templates
you control.

To honour a template's `connection-string`, opt in explicitly with a connection factory:

```csharp
var provider = new DbTemplateDataProvider(
    req => req.ConnectionString!.CreateDbConnection("Microsoft.Data.SqlClient"));

var html = template.RenderContent(provider, new { category = "Displays" });
```

## Target frameworks

`netstandard2.0`, `net8.0`, `net9.0`, `net10.0` — so .NET Framework 4.6.1+, .NET Core 2.0+, Mono,
Xamarin and Unity are all covered alongside modern .NET.

## Tests

Every example on this page is executed by
the `DocumentationExamplesTests` suite in the repository, including the injection
payloads in Example 7 — so the documentation cannot quietly drift from the behaviour. A separate
legacy-parity suite, whose expected values were captured by running
the **original** engine, pins template-file compatibility with `Com.H.Text.Template`.

```
dotnet test
```

## How this relates to Com.H

Two things worth knowing, neither of which you need in order to use this package:

- The `2` marks the generation. `Com.H.Text.Template` (inside the
  [Com.H](https://github.com/H7O/Com.H) package) is the original 2016 engine — still supported,
  still used by deployed applications. **This package is the current one**: a native
  reimplementation, template-file compatible with the original (same tags, markers and
  semantics, pinned by a parity test suite) but natively async and with the original's sharp
  edges removed. If you're starting something new, take the highest-numbered
  `Com.H.Text.Template*` package; that's the whole rule.
- Its only dependency is [Com.H.Data.Common](https://github.com/H7O/Com.H.Data.Common), which
  supplies the parameterised database access. It does not depend on `Com.H` at all — templating
  without a database costs you nothing else.

[DESIGN.md](https://github.com/H7O/Com.H.Text.Template2/blob/master/DESIGN.md) covers the architecture and the alternatives that were rejected.
[SUCCESSOR-NOTES.md](https://github.com/H7O/Com.H.Text.Template2/blob/master/SUCCESSOR-NOTES.md) records how a future generation should be approached.

## License

MIT
