# Com.H.Text.Template2

The current recommended way to run **database-backed templates** with
[Com.H](https://github.com/H7O/Com.H).

Not a standalone templating engine — an extension to `Com.H`'s `Com.H.Text.Template` namespace,
connecting it to any ADO.NET database via
[Com.H.Data.Common](https://github.com/H7O/Com.H.Data.Common).

Queries embedded in templates are executed with **real SQL parameters**, never by substituting
values into the SQL as text.

> **Which generation should I use?** Take the highest-numbered `Com.H.Text.Template*` package.
> `Com.H.Text.Template` (inside `Com.H`) is the original 2016 engine — still supported, still
> used by deployed apps. This package is the current one. A future generation would ship as
> `Com.H.Text.Template3`.

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

`{{country}}` reaches the database as a `DbParameter`. It is never concatenated into the query.

## Why a separate package

`Com.H` has the templating engine and no database code. `Com.H.Data.Common` has the database
code and no templating. Neither depends on the other, so nobody pays for what they don't use.
This package is the adapter, and only it depends on both.

## Safety defaults

- **Values are always parameterised.** No textual substitution into SQL, ever.
- **`pre-render="true"` is rejected** by default, since it would substitute values as text.
  Opt in with `allowPreRender: true` only when a template must interpolate an identifier
  (a table or column name), which cannot be parameterised.
- **A template's `connection-string` attribute is ignored** by default. A template is data,
  and data should not choose which database the application talks to. Supply a connection
  factory if you want to honour it.

## Target frameworks

`netstandard2.0`, `net8.0`, `net9.0`, `net10.0` — matching both dependencies.
