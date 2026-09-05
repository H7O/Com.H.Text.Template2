# Com.H.Text.Template2

A small, natively async templating engine for .NET that can run **SQL queries embedded in the
template itself**.

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

var output = await template.RenderContentAsync(connection, new { category = "Accessories" });

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
```

`{now{…}}`, `{tomorrow{…}}` and `{yesterday{…}}` accept standard .NET date format strings and
render in the current culture.

## Example 2 — A template from a file

```csharp
// greeting.txt:  Hello {{name}}, welcome back.

var output = new Uri(@"C:\templates\greeting.txt").RenderContent(new { name = "Ali" });
```

```
Hello Ali, welcome back.
```

`http://` and `https://` URLs work too — and anything else you like, via
[a content resolver](#loading-templates-from-anywhere).

## Example 3 — Adding a database query

Wrap a query in `<h-embedded-data>`. Everything else in the template becomes the **body**, which
renders once per returned row, with `{{column}}` markers filled from that row.

```csharp
using Com.H.Data.Common;    // CreateDbConnection
using Com.H.Text.Template2; // RenderContentAsync

using var connection = connectionString.CreateDbConnection("Microsoft.Data.SqlClient");

var template = """
    <h-embedded-data><![CDATA[
        select name, price from products where category = {{category}} order by name
    ]]></h-embedded-data>
    <li>{{name}} - {{price}}</li>
    """;

var output = await template.RenderContentAsync(connection, new { category = "Accessories" });
```

```html
<li>Keyboard - 25</li>
<li>Mouse - 15</li>
```

`{{category}}` in the *query* is filled from the data model you passed, as a SQL parameter.
`{{name}}` and `{{price}}` in the *body* are filled from each row.

Any ADO.NET database works — SQL Server, PostgreSQL, MySQL, SQLite, Oracle, ODBC, OleDb. The
connection is yours: the engine opens it if needed and never disposes it.

Every method has a synchronous twin (`RenderContent`), but the engine is async end to end —
prefer `RenderContentAsync` in a web application.

### Mixing caller values with query results

A very common case: some values come from your code, the rest from the query. Both work in the
same template, and **a caller value stays reachable inside the data block**:

```csharp
var template = """
    <h-embedded-data><![CDATA[
        select price from products where name = {{product}}
    ]]></h-embedded-data>
    <b>{{product}}</b> costs {{price}} <a href="{{details_url}}">Details</a>
    """;

await template.RenderContentAsync(connection, new { product = "Monitor", details_url = "https://shop.example/monitor" });
// <b>Monitor</b> costs 199 <a href="https://shop.example/monitor">Details</a>
```

Markers resolve **per key, innermost first**:

1. the current row, if it has that column
2. then the enclosing template's row, and so on outward
3. then the data model you passed
4. and if *nothing* has the key, the marker renders as an empty string

So a row value wins a name both have, and a caller value the row doesn't have is still visible.
This is the same rule `Com.H.Data.Common` applies to query parameters.

> Set `TemplateOptions.ThrowOnUnresolvedMarker` in development to make step 4 loud instead of
> silent. Leave it off in production, where an empty string beats a placeholder word in front of
> a reader. It fires only for a name *nothing* declares — a column that exists but is NULL still
> renders as an empty string, because that is data, not a typo.

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
var output = await new Uri(@"C:\templates\products.html")
    .RenderContentAsync(connection, new { category = "Displays" });
```

A block can also point at its query with `src`, which is resolved the same way templates are:

```html
<h-embedded-data src="{uri{.}}/queries/products.sql"></h-embedded-data>
<li>{{name}}</li>
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

```html
<table><tr><th>Product</th><th>Price</th></tr>
<tr><td>Keyboard</td><td>25</td></tr>
<tr><td>Monitor</td><td>199</td></tr>
<tr><td>Mouse</td><td>15</td></tr></table>
```

`{uri{.}}` resolves to the folder of the template doing the including, so nested templates move
with their parent. Nesting can go as deep as you like, and a nested template's query can bind
values from its parent's current row.

## Example 6 — Logic belongs in the query

Alternating row colours, conditional formatting, running totals, placeholder text for nulls —
decide them in SQL and let the template just place the value:

```html
<h-embedded-data><![CDATA[
    select coalesce(name, '(unnamed)') as name,
           case when row_number() over (order by name) % 2 = 0
                then '#f0f0f0' else '#ffffff' end as row_colour
    from products order by name
]]></h-embedded-data>
<tr bgcolor="{{row_colour}}"><td>{{name}}</td></tr>
```

No template-level `if`, because SQL's `case when` is better at it and more people already read it.

## Example 7 — Parameters straight from a web request

Values from a request can be passed directly. They are bound as SQL parameters, so there is
nothing to escape and no injection risk:

```csharp
[HttpGet("catalogue")]
public async Task<IActionResult> Catalogue([FromQuery] string category, CancellationToken ct)
{
    using var connection = _connectionString.CreateDbConnection("Microsoft.Data.SqlClient");

    var html = await _template.RenderContentAsync(connection, new { category }, cancellationToken: ct);

    return Content(html ?? "", "text/html");
}
```

| `category` value | Result |
|---|---|
| `Accessories` | the matching rows |
| `Accessories' OR '1'='1` | **no rows** — treated as a category name nobody has |
| `x'; delete from products; --` | **no rows**, and the table is untouched |

The payloads are indistinguishable from any other value that matches nothing, which is exactly
what a bound parameter does. There is no way to ask for textual substitution instead.

## Example 8 — Sections that disappear when there is no data

Because a template with a query repeats per row, **zero rows means it renders as nothing**. Put
the query in a nested template and the surrounding page survives while that section collapses:

```csharp
await new Uri(index).RenderContentAsync(connection, new { category = "Displays" });
// <h1>Catalogue</h1><ul><li>Monitor</li></ul><footer>end</footer>

await new Uri(index).RenderContentAsync(connection, new { category = "Nonexistent" });
// <h1>Catalogue</h1><ul></ul><footer>end</footer>
```

No conditional syntax needed — an empty result set is its own "hide this".

## Example 9 — Escaping values for HTML

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
| `{{name}}` | none — written exactly as it is |
| `{html{name}}` | HTML/XML text and quoted attribute values |
| `{url{name}}` | percent-encoding, for URLs and query strings |

`{html{…}}` also escapes `"` and `'`, so a **quoted** attribute is safe. An encoded value is
still never re-scanned as template syntax.

## Example 10 — Generic and dedicated markers

`{{name}}` searches the whole model chain. When you need certainty about *which* model answered,
give a block its own marker:

```html
<h-embedded-data marker="{invoice{"><![CDATA[
    select id, total from invoice where id = {{invoice_id}}
]]></h-embedded-data>

<h-embedded-template><![CDATA[{uri{.}}/lines.html]]></h-embedded-template>
```

```html
<!-- lines.html — its own rows also have a 'total' column -->
<h-embedded-data><![CDATA[select description, total from invoice_line where invoice_id = {{id}}]]>
</h-embedded-data>
<tr><td>{{description}}</td><td>{{total}}</td><td>{invoice{total}}</td></tr>
```

- `{{total}}` — the nearest model with a `total`, i.e. the line
- `{invoice{total}}` — **only** the block that declared `{invoice{`, whatever else is in scope

A dedicated marker never falls back, which is precisely the guarantee it exists to give. A block
declaring one still accepts `{{name}}` too, so you only reach for it where it matters.

`close-marker` is optional and defaults to `}}`; supply it when a symmetric pair reads better:

```html
<h-embedded-data marker="[[" close-marker="]]">
```

For full control, `marker-pattern` takes a regex with named groups `open_marker`, `param` and
`close_marker` — deliberately the same shape as `Com.H.Data.Common`'s
`DbQueryParams.QueryParamsRegex`, so a template's markers address query parameters with no
translation in between. It is validated on use, so a mistake is reported rather than silently
matching nothing.

## Example 11 — One template, with or without a database

A template containing a query can still render without one. The query is skipped and the template
renders **once**, from the data model you passed:

```csharp
await template.RenderContentAsync(connection, new { category = "Accessories" });
// <li>Keyboard</li><li>Mouse</li>

template.RenderContent(new { name = "Placeholder" });
// <li>Placeholder</li>
```

Useful for previewing a layout or a design-time placeholder. Note the difference from Example 8:

| Situation | Result |
|---|---|
| No data source supplied | query skipped; template renders **once** from your model |
| Query ran and matched nothing | template renders as **nothing** |

Set `TemplateOptions.ThrowIfQueryPresent` for templates that must never render without their data.

---

## Choosing where data comes from

The engine knows about templates, markers and rows. It knows nothing about SQL, JSON, HTTP or
anything else — every source is an `ITemplateDataProvider`, and two ship in the box.

| You have | Call |
|---|---|
| Values only | `content.RenderContentAsync(dataModel)` |
| One connection for everything | `content.RenderContentAsync(connection, dataModel)` |
| A connection per block | `content.RenderContentAsync(connectionFactory, dataModel)` |
| Anything else | `content.RenderContentAsync(provider, dataModel)` |

### A connection per block

The factory receives the block's attributes and decides everything — which database, on what
terms, and who disposes the connection:

```csharp
var html = await template.RenderContentAsync(
    (attrs, ct) =>
    {
        var name = attrs.TryGetValue("database", out var v) ? v : "default";
        return new ValueTask<TemplateConnection?>(
            TemplateConnection.Owned(_factory.Create(name)));
    },
    new { country = "JO" });
```

```html
<h-embedded-data database="reporting"><![CDATA[ … ]]></h-embedded-data>
```

`database` means nothing to the engine — invent whatever vocabulary your templates need
(`tenant`, `region`, `retries`) and interpret it in the factory. `TemplateConnection.Owned`
lets the engine dispose the connection after the block; `Borrowed` keeps it yours.

> A template's own `connection-string` attribute is **not** honoured. A template is data, and
> data should not choose which database the application talks to. Read it in your factory if you
> really want that.

### Data that isn't SQL

A block whose `content-type` is `json` carries its own rows — written inline, or fetched by `src`:

```html
<h-embedded-data content-type="json" src="https://api.example.com/users"
                 header-Authorization="Bearer {{token}}"></h-embedded-data>
<li>{{name}}</li>
```

Mix sources in one document by composing providers:

```csharp
var provider = TemplateDataProviders.Compose(
    new JsonTemplateDataProvider(),
    new DbTemplateDataProvider(connectionFactory));
```

Each provider inspects the block and declines what isn't its; the first real answer wins. Serving
rows from a cache, a queue, or SFTP means writing one `ITemplateDataProvider` — no fork required.

### Loading templates from anywhere

`TemplateOptions.ContentResolver` decides what a template URI means, for the root template, every
include, and a block's `src`:

```csharp
options.ContentResolver = async (uri, attrs, ct) =>
    cache.TryGetValue(uri, out var hit)
        ? hit
        : cache[uri] = await TemplateContent.FetchAsync(uri, attrs, ct);
```

The default reads local files and http(s), honouring `referrer`, `user-agent` and any `header-*`
attributes. Call it from your own resolver rather than reimplementing it — this is also why the
package contains no HTTP client of its own: a REST-backed data block is just `src` plus whatever
resolver you supply.

---

## Reference

### Tags

| Tag | Purpose |
|---|---|
| `<h-embedded-data>` | A data block. CDATA content is the query (or `src` names it); the rest of the file is the body, repeated per row. |
| `<h-embedded-template>` | Includes another template. CDATA content is its URI; `{uri{.}}` is the including file's folder. |

### Attributes on `<h-embedded-data>`

| Attribute | Meaning |
|---|---|
| `marker` / `close-marker` | Dedicated marker for this block's rows. Default `{{` / `}}`. |
| `marker-pattern` | A regex with `open_marker` / `param` / `close_marker` groups, instead of the pair above. |
| `content-type` | Which provider claims the block. `json` is built in; anything else goes to your provider. |
| `src` | Resolve the query (or payload) from this URI. |
| Anything else | Yours. Passed to the provider and the connection factory untouched. |

Underscores and dashes are interchangeable — `content_type` and `content-type` are one attribute.

### Markers

| Marker | Meaning |
|---|---|
| `{{name}}` | a value, searched innermost-first through the model chain |
| `{block{name}}` | a value from **only** the block that declared `{block{` |
| `{html{name}}` / `{url{name}}` | a value, encoded |
| `{now{fmt}}` `{tomorrow{fmt}}` `{yesterday{fmt}}` | a date, in the current culture |
| `{uri{.}}` / `{uri{./}}` | the including template's folder |

One close marker (`}}`) for everything; the open marker carries the meaning.

### Things worth knowing

- **One data block per template file.** A second `<h-embedded-data>` is an error — put each query
  in its own file and include them.
- **A data block repeats the whole file.** Use nesting to control what repeats.
- **Zero rows renders nothing**; *no data source* renders once (Example 11).
- **Values are never re-scanned.** A row containing `{{x}}` or `<h-embedded-template>` is emitted
  verbatim, so untrusted data cannot become template syntax.
- **Include cycles** are detected at depth 32 and reported.

## Safety

| Behaviour | Why |
|---|---|
| Parameter values are always bound as `DbParameter` | no textual substitution into SQL, ever — there is no option to turn this off |
| A template's `connection-string` is ignored | a template is data; data should not choose the database |
| Substituted values are never re-parsed | untrusted data cannot inject markers, includes, or queries |
| Header values containing CR/LF are rejected | a marker-filled header cannot split an HTTP request |

## Target frameworks

`netstandard2.0`, `net8.0`, `net9.0`, `net10.0` — .NET Framework 4.6.1+, .NET Core 2.0+, Mono,
Xamarin and Unity alongside modern .NET. The only dependency is
[Com.H.Data.Common](https://github.com/H7O/Com.H.Data.Common).

## Tests

Every example on this page is executed by
the `DocumentationExamplesTests` suite in the repository, so the documentation
cannot drift from the behaviour. `LegacyParityTests.cs` pins compatibility with the original
`Com.H.Text.Template` engine, and `SecurityTests.cs` pins the injection properties.

```
dotnet test
```

## How this relates to Com.H

The `2` marks the generation. `Com.H.Text.Template` (a namespace inside the `Com.H` package) is
the original 2016 engine — still supported, still used by deployed applications. **This package is
the current one**, with its own engine; it no longer depends on `Com.H` at all. Starting
something new? Take the highest-numbered `Com.H.Text.Template*` package.

Existing template files keep working: same tags, markers, date placeholders and repeat-per-row
semantics. [DESIGN.md](https://github.com/H7O/Com.H.Text.Template2/blob/master/DESIGN.md) covers the architecture and the deliberate divergences;
[SUCCESSOR-NOTES.md](https://github.com/H7O/Com.H.Text.Template2/blob/master/SUCCESSOR-NOTES.md) records how a future generation should be approached.

## License

MIT
