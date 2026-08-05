using Com.H.Text.Template2;
using Microsoft.Data.Sqlite;

namespace Com.H.Text.Template2.Tests;

/// <summary>
/// Encoding markers. The engine writes values verbatim by default because it does not know the
/// output format; <c>{html{…}}</c> and <c>{url{…}}</c> let a template say so at the point of use.
/// </summary>
public class EncodingTests : IDisposable
{
    private readonly SqliteConnection _conn;

    public EncodingTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            create table issuers (name text, note text);
            insert into issuers values ('Smith & Sons <Holdings>', null);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();

    [Fact]
    public void HtmlMarker_EncodesTheValue()
    {
        var output = "<td>{html{issuer}}</td>"
            .RenderContent(new { issuer = "Smith & Sons <Holdings>" });

        Assert.Equal("<td>Smith &amp; Sons &lt;Holdings&gt;</td>", output);
    }

    [Fact]
    public void PlainMarker_StillWritesVerbatim()
    {
        // the default is unchanged: the engine does not assume HTML
        var output = "<td>{{issuer}}</td>"
            .RenderContent(new { issuer = "Smith & Sons <Holdings>" });

        Assert.Equal("<td>Smith & Sons <Holdings></td>", output);
    }

    [Fact]
    public void HtmlMarker_EscapesQuotes_SoQuotedAttributesAreSafe()
    {
        var output = "<td title=\"{html{v}}\">x</td>"
            .RenderContent(new { v = "a\"b'c" });

        Assert.DoesNotContain("title=\"a\"b", output);
        Assert.Contains("&quot;", output);
        Assert.Contains("&#39;", output);
    }

    [Fact]
    public void HtmlMarker_NeutralisesAScriptPayload()
    {
        var output = "<p>{html{comment}}</p>"
            .RenderContent(new { comment = "<script>alert(1)</script>" });

        Assert.DoesNotContain("<script>", output);
        Assert.Equal("<p>&lt;script&gt;alert(1)&lt;/script&gt;</p>", output);
    }

    [Fact]
    public void HtmlMarker_WorksOnDatabaseRows()
    {
        var template =
            "<h-embedded-data><![CDATA[select name from issuers]]></h-embedded-data>"
            + "<td>{html{name}}</td>";

        Assert.Equal("<td>Smith &amp; Sons &lt;Holdings&gt;</td>", template.RenderContent(_conn));
    }

    [Fact]
    public void UrlMarker_PercentEncodes()
    {
        var output = "/search?q={url{term}}".RenderContent(new { term = "a b&c=d" });

        Assert.Equal("/search?q=a+b%26c%3Dd", output);
    }

    [Fact]
    public void EncodingMarkers_WorkAlongsideACustomOpenMarker()
    {
        // encoders address the models directly, so a block's own marker syntax is irrelevant
        var template =
            "<h-embedded-data marker=\"{v1{\"><![CDATA[select name from issuers]]>"
            + "</h-embedded-data>[{v1{name}}|{html{name}}]";

        Assert.Equal("[Smith & Sons <Holdings>|Smith &amp; Sons &lt;Holdings&gt;]",
            template.RenderContent(_conn));
    }

    [Fact]
    public void NullValue_CollapsesToEmpty_AndEncodingDoesNotChangeThat()
    {
        var template =
            "<h-embedded-data><![CDATA[select name, note from issuers]]>"
            + "</h-embedded-data>[{html{note}}]";

        Assert.Equal("[]", template.RenderContent(_conn));
    }

    [Fact]
    public void PlaceholderFromTheQuery_IsEncodedLikeAnyOtherValue()
    {
        // placeholder text now comes from the query, so it is data and gets encoded — which is
        // right: whatever coalesce returns is a value, not markup the template author wrote
        var template =
            "<h-embedded-data><![CDATA["
            + "select coalesce(note, '<em>n/a</em>') as note from issuers"
            + "]]></h-embedded-data>[{html{note}}]";

        Assert.Equal("[&lt;em&gt;n/a&lt;/em&gt;]", template.RenderContent(_conn));
    }

    [Fact]
    public void EncodedValue_IsStillNeverRescanned()
    {
        // the security property holds for encoded markers too
        var output = "[{html{v}}]".RenderContent(new { v = "{{secret}}", secret = "LEAKED" });

        Assert.Equal("[{{secret}}]", output);
    }
}
