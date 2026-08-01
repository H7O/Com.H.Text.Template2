using System.Data.Common;
using Com.H.Text.Template2;
using Microsoft.Data.Sqlite;

namespace Com.H.Text.Template2.Tests;

/// <summary>
/// Every example in README.md / nuget.md, executed. If an example here changes, the docs must
/// change with it — these exist so the documentation cannot quietly rot.
/// </summary>
public class DocumentationExamplesTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _dir;

    public DocumentationExamplesTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            create table products (name text, category text, price real);
            insert into products values ('Keyboard','Accessories', 25.0);
            insert into products values ('Monitor', 'Displays',   199.0);
            insert into products values ('Mouse',   'Accessories', 15.0);
            """;
        cmd.ExecuteNonQuery();

        _dir = Path.Combine(Path.GetTempPath(), "comh_docs_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // ---------------------------------------------------------------- Example 1
    [Fact]
    public void Example1_FillingInValues()
    {
        var output = "Hello {{name}}, you have {{count}} new messages."
            .RenderContent(new { name = "Ali", count = 3 });

        Assert.Equal("Hello Ali, you have 3 new messages.", output);
    }

    // ---------------------------------------------------------------- Example 2
    [Fact]
    public void Example2_TemplateFromAFile()
    {
        var path = Write("greeting.txt", "Hello {{name}}, welcome back.");

        var output = new Uri(path).RenderContent(new { name = "Ali" });

        Assert.Equal("Hello Ali, welcome back.", output);
    }

    // ---------------------------------------------------------------- Example 3
    [Fact]
    public void Example3_AddingADatabaseQuery()
    {
        var template =
            """
            <h-embedded-data><![CDATA[
                select name, price from products where category = {{category}} order by name
            ]]></h-embedded-data>
            <li>{{name}} - {{price}}</li>
            """;

        var output = template.RenderContent(_connection, new { category = "Accessories" });

        Assert.Contains("<li>Keyboard - 25</li>", output);
        Assert.Contains("<li>Mouse - 15</li>", output);
        Assert.DoesNotContain("Monitor", output);
    }

    // ---------------------------------------------------------------- Example 4
    [Fact]
    public void Example4_TemplateFileWithItsQueryInside()
    {
        var path = Write("products.html",
            """
            <h-embedded-data><![CDATA[
                select name from products where category = {{category}} order by name
            ]]></h-embedded-data>
            <li>{{name}}</li>
            """);

        var output = new Uri(path).RenderContent(_connection, new { category = "Displays" });

        Assert.Contains("<li>Monitor</li>", output);
        Assert.DoesNotContain("Keyboard", output);
    }

    // ---------------------------------------------------------------- Example 5
    [Fact]
    public void Example5_BuildingATableWithNestedTemplates()
    {
        Write("rows.html",
            "<h-embedded-data><![CDATA["
            + "select name, price from products order by name"
            + "]]></h-embedded-data>"
            + "<tr><td>{{name}}</td><td>{{price}}</td></tr>");

        var index = Write("index.html",
            "<table>"
            + "<tr><th>Product</th><th>Price</th></tr>"
            + "<h-embedded-template><![CDATA[{uri{.}}/rows.html]]></h-embedded-template>"
            + "</table>");

        var output = new Uri(index).RenderContent(_connection);

        // header appears exactly once; one row per record
        Assert.Equal("<table><tr><th>Product</th><th>Price</th></tr>"
            + "<tr><td>Keyboard</td><td>25</td></tr>"
            + "<tr><td>Monitor</td><td>199</td></tr>"
            + "<tr><td>Mouse</td><td>15</td></tr>"
            + "</table>", output);
    }

    // ---------------------------------------------------------------- Example 6
    [Fact]
    public void Example6_AlternatingRowColoursDecidedInSql()
    {
        Write("rows.html",
            "<h-embedded-data><![CDATA["
            + "select name, case when row_number() over (order by name) % 2 = 0 "
            + "then '#f0f0f0' else '#ffffff' end as row_colour "
            + "from products order by name"
            + "]]></h-embedded-data>"
            + "<tr bgcolor=\"{{row_colour}}\"><td>{{name}}</td></tr>");

        var index = Write("index.html",
            "<table><h-embedded-template><![CDATA[{uri{.}}/rows.html]]></h-embedded-template></table>");

        var output = new Uri(index).RenderContent(_connection);

        Assert.Equal("<table>"
            + "<tr bgcolor=\"#ffffff\"><td>Keyboard</td></tr>"
            + "<tr bgcolor=\"#f0f0f0\"><td>Monitor</td></tr>"
            + "<tr bgcolor=\"#ffffff\"><td>Mouse</td></tr>"
            + "</table>", output);
    }

    // ---------------------------------------------------------------- Example 7
    [Theory]
    [InlineData("Accessories", true)]
    [InlineData("Accessories' OR '1'='1", false)]
    [InlineData("x'; delete from products; --", false)]
    public void Example7_ParametersFromAWebRequestAreSafe(string category, bool expectRows)
    {
        var template =
            "<h-embedded-data><![CDATA["
            + "select name from products where category = {{category}} order by name"
            + "]]></h-embedded-data><li>{{name}}</li>";

        var output = template.RenderContent(_connection, new { category });

        if (expectRows) Assert.Contains("<li>Keyboard</li>", output);
        else Assert.Equal("", output);

        // the table is intact regardless of what was passed
        using var check = _connection.CreateCommand();
        check.CommandText = "select count(*) from products";
        Assert.Equal(3L, Convert.ToInt64(check.ExecuteScalar()));
    }

    // ---------------------------------------------------------------- Example 8
    [Fact]
    public void Example8_SectionDisappearsWhenThereIsNoData()
    {
        Write("offers.html",
            "<h-embedded-data><![CDATA["
            + "select name from products where category = {{category}}"
            + "]]></h-embedded-data>"
            + "<li>{{name}}</li>");

        var index = Write("index.html",
            "<h1>Catalogue</h1>"
            + "<ul><h-embedded-template><![CDATA[{uri{.}}/offers.html]]></h-embedded-template></ul>"
            + "<footer>end</footer>");

        var withRows = new Uri(index).RenderContent(_connection, new { category = "Displays" });
        var withoutRows = new Uri(index).RenderContent(_connection, new { category = "Nonexistent" });

        Assert.Equal("<h1>Catalogue</h1><ul><li>Monitor</li></ul><footer>end</footer>", withRows);
        Assert.Equal("<h1>Catalogue</h1><ul></ul><footer>end</footer>", withoutRows);
    }

    // ---------------------------------------------------------------- Example 9
    [Fact]
    public void Example9_ChangingTheMarkerSyntax()
    {
        var template =
            "<h-embedded-data open-marker=\"{v1{\"><![CDATA["
            + "select name from products where category = {{category}} order by name"
            + "]]></h-embedded-data>"
            + "<span style=\"color:{red}\">{v1{name}}</span>";

        var output = template.RenderContent(_connection, new { category = "Displays" });

        // {v1{name}} was substituted; the CSS braces were left alone
        Assert.Equal("<span style=\"color:{red}\">Monitor</span>", output);
    }

    // ------------------------------------------------- documented failure modes
    // ---------------------------------------------------------------- Example 10
    [Fact]
    public void Example10_OneTemplateWithAndWithoutADatabase()
    {
        var template =
            "<h-embedded-data><![CDATA["
            + "select name from products where category = {{category}} order by name"
            + "]]></h-embedded-data><li>{{name}}</li>";

        // with a connection: one <li> per row, filled from the query
        var withDb = template.RenderContent(_connection, new { category = "Accessories" });
        Assert.Equal("<li>Keyboard</li><li>Mouse</li>", withDb);

        // without one: the query is skipped and the template renders once,
        // with markers filled from the data model instead
        var withoutDb = template.RenderContent(new { name = "Placeholder" });
        Assert.Equal("<li>Placeholder</li>", withoutDb);
    }

    [Fact]
    public void DatabaseLessOverload_SkipsTheQueryRatherThanCollapsing()
    {
        // "no data source" (null) is distinct from "the query matched nothing" (empty):
        // the former still renders, the latter collapses. See Example 8 for the latter.
        var template =
            "HEADER<h-embedded-data><![CDATA[select name from products]]></h-embedded-data>"
            + "[{{name}}]FOOTER";

        Assert.Equal("HEADER[Ali]FOOTER", template.RenderContent(new { name = "Ali" }));
    }

    [Fact]
    public void StrictMode_QueryPresent_Throws()
    {
        var template =
            "<h-embedded-data><![CDATA[select name from products]]></h-embedded-data><li>{{name}}</li>";

        var ex = Assert.ThrowsAny<Exception>(() =>
            template.RenderContent(new { name = "x" }, throwIfQueryPresent: true));

        Assert.Contains("throwIfQueryPresent", ex.GetBaseException().Message);
    }

    [Fact]
    public void StrictMode_NoQuery_RendersNormally()
    {
        // the provider is only invoked when a data tag exists, so strict mode costs nothing
        // for templates that genuinely have no query
        var output = "Hello {{name}}.".RenderContent(new { name = "Ali" }, throwIfQueryPresent: true);

        Assert.Equal("Hello Ali.", output);
    }

    [Fact]
    public void StrictMode_NestedTemplateWithAQuery_AlsoThrows()
    {
        Write("rows.html",
            "<h-embedded-data><![CDATA[select name from products]]></h-embedded-data><li>{{name}}</li>");
        var index = Write("index.html",
            "<ul><h-embedded-template><![CDATA[{uri{.}}/rows.html]]></h-embedded-template></ul>");

        Assert.ThrowsAny<Exception>(() =>
            new Uri(index).RenderContent(new { name = "x" }, throwIfQueryPresent: true));
    }

    [Fact]
    public void DatabaseLessOverload_NestedTemplateWithAQuery_AlsoSkips()
    {
        Write("rows.html",
            "<h-embedded-data><![CDATA[select name from products]]></h-embedded-data><li>{{name}}</li>");
        var index = Write("index.html",
            "<ul><h-embedded-template><![CDATA[{uri{.}}/rows.html]]></h-embedded-template></ul>");

        Assert.Equal("<ul><li>Placeholder</li></ul>",
            new Uri(index).RenderContent(new { name = "Placeholder" }));
    }

    [Fact]
    public void AnyMarkerCharacters_Work()
    {
        // the legacy engine did not regex-escape markers, so '[[' silently failed and '<%'
        // threw an XmlException; the native engine escapes them, so any characters work
        var template =
            "<h-embedded-data open-marker=\"[[\" close-marker=\"]]\"><![CDATA["
            + "select name from products where category = {{category}} order by name"
            + "]]></h-embedded-data>[[name]] ";

        Assert.Equal("Monitor ", template.RenderContent(_connection, new { category = "Displays" }));

        var angled =
            "<h-embedded-data open-marker=\"<%\" close-marker=\"%>\"><![CDATA["
            + "select name from products where category = {{category}} order by name"
            + "]]></h-embedded-data><%name%> ";

        Assert.Equal("Monitor ", angled.RenderContent(_connection, new { category = "Displays" }));
    }

    [Fact]
    public void MultipleDataTagsInOneFile_ThrowWithGuidance()
    {
        // one query per template file, because its rows repeat the whole file; the legacy engine
        // silently ignored extra tags — this engine says so instead
        var q = "<h-embedded-data><![CDATA[select name from products]]></h-embedded-data>";
        var template = q + "<li>{{name}}</li>" + q + "<b>{{name}}</b>";

        var ex = Assert.ThrowsAny<Exception>(() =>
            template.RenderContent(_connection, new { c = "Accessories" }));

        Assert.Contains("h-embedded-template", ex.GetBaseException().Message);
    }

    // ---------------------------------------------------------------- async

    [Fact]
    public async Task Example3_Async_ProducesTheSameOutput()
    {
        var template =
            "<h-embedded-data><![CDATA["
            + "select name, price from products where category = {{category}} order by name"
            + "]]></h-embedded-data><li>{{name}} - {{price}}</li>";

        var output = await template.RenderContentAsync(_connection, new { category = "Accessories" });

        Assert.Equal("<li>Keyboard - 25</li><li>Mouse - 15</li>", output);
    }

    [Fact]
    public async Task Example5_Async_NestedTemplates()
    {
        Write("rows.html",
            "<h-embedded-data><![CDATA[select name from products order by name]]></h-embedded-data>"
            + "<tr><td>{{name}}</td></tr>");
        var index = Write("index.html",
            "<table><h-embedded-template><![CDATA[{uri{.}}/rows.html]]></h-embedded-template></table>");

        var output = await new Uri(index).RenderContentAsync(_connection);

        Assert.Equal(
            "<table><tr><td>Keyboard</td></tr><tr><td>Monitor</td></tr><tr><td>Mouse</td></tr></table>",
            output);
    }

    // ---------------------------------------------------------------- new syntax additions

    [Fact]
    public void RelativeIncludePaths_ResolveAgainstTheParentTemplate()
    {
        // {uri{.}} still works; plain relative paths are the new, simpler form
        Write("sub/part.html", "[part {{name}}]");
        var index = Write("index.html",
            "A<h-embedded-template><![CDATA[sub/part.html]]></h-embedded-template>B");

        Assert.Equal("A[part Ali]B", new Uri(index).RenderContent(new { name = "Ali" }));
    }

    [Fact]
    public void SqlCanLiveInItsOwnFile_ViaTheSrcAttribute()
    {
        Write("query.sql", "select name from products where category = {{category}} order by name");
        var index = Write("index.html",
            "<h-embedded-data src=\"query.sql\"></h-embedded-data><li>{{name}}</li>");

        Assert.Equal("<li>Keyboard</li><li>Mouse</li>",
            new Uri(index).RenderContent(_connection, new { category = "Accessories" }));
    }

    [Fact]
    public void JsonDataBlock_RendersRowsWithoutADatabase()
    {
        // content-type="json": the block's content IS the data — an embedded JSON array here,
        // or one fetched from a REST API via src="https://..."
        var template =
            "<h-embedded-data content-type=\"json\"><![CDATA["
            + "[ {\"name\":\"Ali\",\"city\":\"Amman\"}, {\"name\":\"Sara\",\"city\":\"Dubai\"} ]"
            + "]]></h-embedded-data><li>{{name}} ({{city}})</li>";

        Assert.Equal("<li>Ali (Amman)</li><li>Sara (Dubai)</li>",
            template.RenderContent(new { }));
    }

    [Fact]
    public void IncludeCycle_IsDetectedRatherThanHanging()
    {
        Write("a.html", "<h-embedded-template><![CDATA[b.html]]></h-embedded-template>");
        Write("b.html", "<h-embedded-template><![CDATA[a.html]]></h-embedded-template>");

        var ex = Assert.ThrowsAny<Exception>(() => new Uri(Path.Combine(_dir, "a.html")).RenderContent(new { }));

        Assert.Contains("cycle", ex.GetBaseException().Message);
    }
}
