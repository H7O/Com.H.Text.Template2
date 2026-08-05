using System.Data.Common;
using Com.H.Text.Template2;
using Microsoft.Data.Sqlite;

namespace Com.H.Text.Template2.Tests;

/// <summary>
/// End-to-end tests: a real template, a real database, a real query.
/// Each test gets its own in-memory SQLite database.
/// </summary>
public class TemplateRenderingTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public TemplateRenderingTests()
    {
        // A shared-cache in-memory database lives as long as the connection is open.
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            create table users (name text, email text, country text);
            insert into users values ('Ali',  'ali@example.com',  'JO');
            insert into users values ('Sara', 'sara@example.com', 'JO');
            insert into users values ('Yuki', 'yuki@example.com', 'JP');
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();

    private static string Template(string query, string body) =>
        $"<h-embedded-data><![CDATA[{query}]]></h-embedded-data>{body}";

    [Fact]
    public void RenderContent_EmbeddedQuery_RendersOneBlockPerRow()
    {
        var template = Template(
            "select name, email from users where country = {{country}} order by name",
            "<li>{{name}}</li>");

        var result = template.RenderContent(_connection, new { country = "JO" });

        Assert.NotNull(result);
        Assert.Contains("Ali", result);
        Assert.Contains("Sara", result);
        Assert.DoesNotContain("Yuki", result);
    }

    [Fact]
    public void RenderContent_ProjectsMultipleColumns()
    {
        var template = Template(
            "select name, email from users where country = {{country}} order by name",
            "<li>{{name}}:{{email}}</li>");

        var result = template.RenderContent(_connection, new { country = "JP" });

        Assert.NotNull(result);
        Assert.Contains("Yuki:yuki@example.com", result);
    }

    [Fact]
    public void RenderContent_NoMatchingRows_RendersNothingFromTheBody()
    {
        var template = Template(
            "select name from users where country = {{country}}",
            "<li>{{name}}</li>");

        var result = template.RenderContent(_connection, new { country = "ZZ" });

        Assert.NotNull(result);
        Assert.DoesNotContain("Ali", result);
        Assert.DoesNotContain("Yuki", result);
    }

    // ---------------------------------------------------------------------
    // The reason this package exists.
    // ---------------------------------------------------------------------

    [Fact]
    public void RenderContent_ClassicInjectionPayload_IsBoundAsAParameterNotConcatenated()
    {
        var template = Template(
            "select name from users where country = {{country}} order by name",
            "<li>{{name}}</li>");

        // Positive control first: without it, the assertions below would also pass if
        // rendering silently produced nothing at all.
        var legitimate = template.RenderContent(_connection, new { country = "JO" });
        Assert.Equal("<li>Ali</li><li>Sara</li>", legitimate);

        // If the value were substituted into the SQL as text, this would close the string
        // literal and make the predicate always true, leaking every row.
        var attacked = template.RenderContent(_connection, new { country = "JO' OR '1'='1" });

        // Bound as a parameter, the payload is just a country nobody has: no rows, and
        // indistinguishable from any other non-matching value.
        Assert.Equal("", attacked);
    }

    [Fact]
    public void RenderContent_StatementTerminatorPayload_DoesNotExecuteASecondStatement()
    {
        var template = Template(
            "select name from users where country = {{country}}",
            "<li>{{name}}</li>");

        // Positive control: the same template does render rows for a real value.
        Assert.NotEmpty(template.RenderContent(_connection, new { country = "JO" })!);

        var attacked = template.RenderContent(
            _connection,
            new { country = "JO'; delete from users; --" });

        Assert.Equal("", attacked);

        // And the second statement never ran — the table is intact.
        using var check = _connection.CreateCommand();
        check.CommandText = "select count(*) from users";
        Assert.Equal(3L, Convert.ToInt64(check.ExecuteScalar()));
    }

    [Fact]
    public void RenderContent_ValueContainingMarkerSyntax_IsTreatedAsData()
    {
        var template = Template(
            "select name from users where country = {{country}}",
            "<li>{{name}}</li>");

        // A value that itself looks like a marker must not be re-interpreted.
        var result = template.RenderContent(_connection, new { country = "{{name}}" });

        Assert.NotNull(result);
        Assert.DoesNotContain("Ali", result);
    }

    [Fact]
    public void RenderContent_QuotedValueMatchingRealData_StillMatches()
    {
        using var insert = _connection.CreateCommand();
        insert.CommandText = "insert into users values ('O''Brien', 'ob@example.com', 'IE')";
        insert.ExecuteNonQuery();

        var template = Template(
            "select name from users where name = {{name}}",
            "<li>{{name}}</li>");

        // An apostrophe in legitimate data must round-trip, not break the query.
        var result = template.RenderContent(_connection, new { name = "O'Brien" });

        Assert.NotNull(result);
        Assert.Contains("O'Brien", result);
    }

    // ---------------------------------------------------------------------
    // pre-render policy
    // ---------------------------------------------------------------------

    [Fact]
    public void PreRenderAttribute_IsIgnored_NotHonoured()
    {
        // pre-render is gone: it substituted values into SQL as text, which is the injection
        // vector this package exists to remove. The attribute is now inert, and the marker is
        // bound as a parameter like any other -- so a template that relied on textual
        // interpolation gets a parameter, not a silent injection.
        var template =
            "<h-embedded-data pre-render=\"true\"><![CDATA["
            + "select name from users where country = {{country}}"
            + "]]></h-embedded-data><li>{{name}}</li>";

        var result = template.RenderContent(_connection, new { country = "JO" });

        Assert.Contains("Ali", result);
    }

    // ---------------------------------------------------------------------
    // plumbing
    // ---------------------------------------------------------------------

    [Fact]
    public void RenderContent_NoDataTag_RendersPlainTemplateUnchanged()
    {
        var result = "<p>no query here</p>".RenderContent(_connection);

        Assert.NotNull(result);
        Assert.Contains("no query here", result);
    }

    [Fact]
    public void RenderContent_NullConnection_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            "<p>x</p>".RenderContent((DbConnection)null!));
    }

    [Fact]
    public void RenderContent_ConnectionRemainsUsableAfterRendering()
    {
        var template = Template(
            "select name from users where country = {{country}}",
            "<li>{{name}}</li>");

        template.RenderContent(_connection, new { country = "JO" });

        // The reader must have been closed; a caller-supplied connection is not disposed.
        Assert.Equal(System.Data.ConnectionState.Open, _connection.State);

        var second = template.RenderContent(_connection, new { country = "JP" });
        Assert.NotNull(second);
        Assert.Contains("Yuki", second);
    }

    [Fact]
    public void RenderContent_ProviderOverload_UsesSuppliedProvider()
    {
        var provider = new DbTemplateDataProvider(_connection);
        var template = Template(
            "select name from users where country = {{country}}",
            "<li>{{name}}</li>");

        var result = template.RenderContent(provider, new { country = "JP" });

        Assert.NotNull(result);
        Assert.Contains("Yuki", result);
    }

    [Fact]
    public void ConnectionFactoryOverload_IsInvokedPerRequest()
    {
        var calls = 0;
        var provider = new DbTemplateDataProvider((attrs, ct) =>
        {
            calls++;
            return new ValueTask<TemplateConnection?>(TemplateConnection.Borrowed(_connection));
        });

        var template = Template(
            "select name from users where country = {{country}}",
            "<li>{{name}}</li>");

        var result = template.RenderContent(provider, new { country = "JO" });

        Assert.NotNull(result);
        Assert.Equal(1, calls);
    }
}
