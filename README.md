# Com.H.Text.Template2

The current recommended way to run **database-backed templates** with
[Com.H](https://github.com/H7O/Com.H).

This is not a standalone templating engine. It is an extension to `Com.H`'s
`Com.H.Text.Template` namespace, connecting it to any ADO.NET database via
[Com.H.Data.Common](https://github.com/H7O/Com.H.Data.Common).

Queries embedded in templates are executed with **real SQL parameters**, never by substituting
values into the SQL as text.

## Why the `2`

`Com.H.Text.Template` (a namespace inside the `Com.H` package) is the original engine, from
2016. It still works and is still supported — deployed applications depend on it.

The number in this package's name says which generation is current. Starting something new?
Take the **highest-numbered** `Com.H.Text.Template*` package. That's the whole rule — no
descriptive name to remember, no need to track which approach superseded which.

| Generation | Where | Status |
|---|---|---|
| `Com.H.Text.Template` | namespace inside the `Com.H` package | original (2016), supported, used by deployed apps |
| `Com.H.Text.Template2` | **this package** | **current** — adds a parameterised database provider |

A future generation would arrive as `Com.H.Text.Template3`, as its own package — so you never
pay for dependencies belonging to a generation you don't use.

## Installation

```
dotnet add package Com.H.Text.Template2
```

## Usage

```csharp
using Com.H.Data.Common;          // CreateDbConnection
using Com.H.Text.Template2; // RenderContent

using var conn = connectionString.CreateDbConnection("Microsoft.Data.SqlClient");

var html = @"
    <h-embedded-data><![CDATA[
        select name, email from users where country = {{country}}
    ]]></h-embedded-data>
    <li>{{name}} - {{email}}</li>"
    .RenderContent(conn, new { country = ""JO"" });
```

The `<h-embedded-data>` block is the query. Everything after it is the body, rendered once per
row, with `{{column}}` markers filled from that row. `{{country}}` reaches the database as a
`DbParameter` — it is never concatenated into the query.

Templates can also be loaded from a URI:

```csharp
var html = new Uri("file:///c:/templates/users.html").RenderContent(conn, new { country = "JO" });
```

## Why this is a separate package

`Com.H` has the templating engine and no database code. `Com.H.Data.Common` has the database
code and no templating. **Neither depends on the other**, so nobody pays for what they don't use:

```
Com.H                          Com.H.Data.Common
(template rendering)           (DbConnection querying)
        \                              /
         \                            /
          Com.H.Text.Template2
```

Only this package depends on both. See [DESIGN.md](DESIGN.md) for the alternatives that were
considered and why they were rejected. [SUCCESSOR-NOTES.md](SUCCESSOR-NOTES.md) records how to approach a future generation.

## Safety defaults

| Behaviour | Default | Why |
|---|---|---|
| Parameter values | always bound as `DbParameter` | no textual substitution into SQL, ever |
| `pre-render="true"` | **rejected** | it substitutes values as text, reintroducing injection risk |
| Template's `connection-string` attribute | **ignored** | a template is data; data should not choose the database |

`pre-render` can be enabled with `allowPreRender: true` when a template must interpolate an
identifier — a table or column name — which cannot be parameterised. Use it only on templates
you control.

To honour a template's `connection-string`, supply a connection factory and read it yourself:

```csharp
var provider = new DbTemplateDataProvider(
    req => req.ConnectionString!.CreateDbConnection("Microsoft.Data.SqlClient"));

var html = template.RenderContent(provider, new { country = "JO" });
```

## Target frameworks

`netstandard2.0`, `net8.0`, `net9.0`, `net10.0` — matching both dependencies.

## Tests

25 tests, including adversarial ones that assert a classic `' OR '1'='1` payload and a
`'; delete from users; --` payload are both bound as data. Each carries a positive control, so
it cannot pass by rendering nothing.

```
dotnet test
```

## License

MIT
