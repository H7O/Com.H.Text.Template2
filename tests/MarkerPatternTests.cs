using System.Text.RegularExpressions;
using Com.H.Data.Common;
using Com.H.Text.Template2;
using Microsoft.Data.Sqlite;

namespace Com.H.Text.Template2.Tests;

/// <summary>
/// Marker patterns, and the generic-versus-dedicated resolution rule.
/// </summary>
/// <remarks>
/// A pattern is the same named-group shape as <c>DbQueryParams.QueryParamsRegex</c>, so the
/// engine carries data models as <c>DbQueryParams</c> and a template's markers address query
/// parameters with no translation between them. There used to be a mapping layer here; unifying
/// the two types deleted it.
/// </remarks>
public class MarkerPatternTests : IDisposable
{
    private readonly SqliteConnection _conn;

    public MarkerPatternTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            create table t (name text);
            insert into t values ('ROW');
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();

    // ------------------------------------------------------------------ pattern building

    [Fact]
    public void PatternFor_Null_IsTheGenericPattern()
    {
        Assert.Equal(TemplateMarkers.DefaultPattern, TemplateMarkers.PatternFor(null));
        Assert.Equal(TemplateMarkers.DefaultPattern, TemplateMarkers.PatternFor(""));
        Assert.Equal(TemplateMarkers.DefaultPattern, TemplateMarkers.PatternFor("{{"));
    }

    [Fact]
    public void PatternFor_AcceptsBothTheGenericAndTheDedicatedForm()
    {
        var pattern = TemplateMarkers.PatternFor("{inv{");

        Assert.Matches(pattern, "a = {{name}}");
        Assert.Matches(pattern, "a = {inv{name}}");
    }

    [Fact]
    public void PatternFor_EscapesRegexMetacharacters()
    {
        // '[' and '$' would otherwise change the pattern's meaning
        var pattern = TemplateMarkers.PatternFor("$[");
        var match = Regex.Match("a = $[total}}", pattern);

        Assert.True(match.Success);
        Assert.Equal("total", match.Groups["param"].Value);
    }

    [Fact]
    public void PatternFor_CapturesWhichMarkerFired()
    {
        var pattern = TemplateMarkers.PatternFor("{inv{");

        Assert.Equal("{{", Regex.Match("{{name}}", pattern).Groups["open_marker"].Value);
        Assert.Equal("{inv{", Regex.Match("{inv{name}}", pattern).Groups["open_marker"].Value);
    }

    [Fact]
    public void PatternFor_SymmetricPair_IsSupported()
    {
        var pattern = TemplateMarkers.PatternFor("[[", "]]");

        Assert.Matches(pattern, "a = [[name]]");
        Assert.Matches(pattern, "a = {{name}}");   // generic still accepted
    }

    [Fact]
    public void PatternFor_MismatchedPair_DoesNotMatch()
    {
        // marker sets alternate as complete PAIRS; alternating each side independently
        // would accept {{name]] and [[name}}, which is a silent way to get it wrong
        var pattern = TemplateMarkers.PatternFor("[[", "]]");

        Assert.DoesNotMatch(pattern, "a = {{name]]");
        Assert.DoesNotMatch(pattern, "a = [[name}}");
    }

    // ------------------------------------------------------------------ validation

    [Theory]
    [InlineData(@"\{x\{.*?\}\}", "open_marker")]
    [InlineData(@"(?<open_marker>\{x\{).*?(?<close_marker>\}\})", "param")]
    [InlineData(@"(?<open_marker>\{x\{)(?<param>.*?)?\}\}", "close_marker")]
    public void MarkerPatternAttribute_MissingAGroup_IsReported(string pattern, string missing)
    {
        // silently matching nothing was the old failure mode; it must be loud
        var template = $"<h-embedded-data marker-pattern=\"{pattern}\"><![CDATA[select name from t]]>"
                     + "</h-embedded-data>[{x{name}}]";

        var ex = Assert.ThrowsAny<Exception>(() => template.RenderContent(_conn));

        Assert.Contains(missing, ex.GetBaseException().Message);
    }

    [Fact]
    public void MarkerPatternAttribute_NotAValidRegex_IsReported()
    {
        var template = "<h-embedded-data marker-pattern=\"(?&lt;unclosed\"><![CDATA[select name from t]]>"
                     + "</h-embedded-data>[{{name}}]";

        Assert.ThrowsAny<Exception>(() => template.RenderContent(_conn));
    }

    [Fact]
    public void MarkerPatternAttribute_Valid_IsHonoured()
    {
        var template =
            """
            <h-embedded-data marker-pattern="(?<open_marker>\{\{|\{inv\{)(?<param>.*?)?(?<close_marker>\}\})"><![CDATA[select name from t]]></h-embedded-data>[{{name}}|{inv{name}}]
            """;

        Assert.Equal("[ROW|ROW]", template.RenderContent(_conn));
    }

    // ------------------------------------------------- generic chains, dedicated scopes

    [Fact]
    public void GenericMarker_ResolvesThroughTheChain()
    {
        var template =
            "<h-embedded-data marker=\"{inv{\"><![CDATA[select name from t]]></h-embedded-data>"
            + "[{{only_on_caller}}]";

        Assert.Equal("[CALLER]", template.RenderContent(_conn, new { only_on_caller = "CALLER" }));
    }

    [Fact]
    public void DedicatedMarker_DoesNotFallBackToAnOuterModel()
    {
        // naming a model is a promise about which one answered; a fallback would break it.
        // The original engine behaved this way too — it is why giving an inner block its own
        // marker was the way to resolve collisions before per-key chaining existed.
        var template =
            "<h-embedded-data marker=\"{inv{\"><![CDATA[select name from t]]></h-embedded-data>"
            + "[{inv{only_on_caller}}]";

        Assert.Equal("[]", template.RenderContent(_conn, new { only_on_caller = "CALLER" }));
    }

    [Fact]
    public void DedicatedMarker_ReadsItsOwnModel()
    {
        var template =
            "<h-embedded-data marker=\"{inv{\"><![CDATA[select name from t]]></h-embedded-data>"
            + "[{inv{name}}]";

        Assert.Equal("[ROW]", template.RenderContent(_conn, new { name = "CALLER" }));
    }

    [Fact]
    public void DedicatedMarkerFromAParent_BeatsANearerRowWithTheSameColumnName()
    {
        // the whole point: an inner block that happens to have 'name' cannot hijack
        // {outer{name}}. Two queries means two files, so this is also the nesting case.
        var dir = Path.Combine(Path.GetTempPath(), "mk_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "child.txt"),
                "<h-embedded-data><![CDATA[select 'CHILD' as name]]></h-embedded-data>"
                + "[{outer{name}}|{{name}}]");
            var main = Path.Combine(dir, "main.txt");
            File.WriteAllText(main,
                "<h-embedded-data marker=\"{outer{\"><![CDATA[select name from t]]></h-embedded-data>"
                + "<h-embedded-template><![CDATA[{uri{.}}/child.txt]]></h-embedded-template>");

            Assert.Equal("[ROW|CHILD]", new Uri(main).RenderContent(_conn));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void EncodingMarkers_AreGeneric_EvenWhenABlockDeclaresItsOwn()
    {
        var template =
            "<h-embedded-data marker=\"{inv{\"><![CDATA[select name from t]]></h-embedded-data>"
            + "[{html{title}}]";

        Assert.Equal("[a &amp; b]", template.RenderContent(_conn, new { title = "a & b" }));
    }
}
