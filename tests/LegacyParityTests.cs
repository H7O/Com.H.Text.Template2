using Com.H.Text.Template2;
using Microsoft.Data.Sqlite;

namespace Com.H.Text.Template2.Tests;

/// <summary>
/// Pins the semantics inherited from the original Com.H.Text.Template engine. Every expected
/// value here was captured by running the ORIGINAL engine before this package's native engine
/// replaced it — these tests are the compatibility contract for existing template files.
/// </summary>
/// <remarks>
/// There is exactly one <b>deliberate</b> divergence, covered by <c>ModelChainTests</c>: marker
/// resolution now walks the model chain per key, so a caller value the current row lacks stays
/// reachable. The original overwrote it with the row's null text. That was a defect — verified
/// against Com.H 10.2.0, where rendering with [outer, row] silently dropped the caller's value —
/// and it made the ordinary "some values from the caller, the rest from a query" case fail
/// invisibly. Collision priority is unchanged: the row still wins a key both models have.
/// </remarks>
public class LegacyParityTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly string _dir;

    public LegacyParityTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            create table users (code text, name text, email text);
            insert into users values ('A1','Ali', null);
            insert into users values ('B2','Sara','sara@x.com');
            """;
        cmd.ExecuteNonQuery();
        _dir = Path.Combine(Path.GetTempPath(), "parity_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { _conn.Dispose(); try { Directory.Delete(_dir, true); } catch { } }

    private string Write(string name, string content)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void UnmatchedMarker_BecomesTheNullValueText()
    {
        // legacy: a marker with no matching value renders as the model's null-value ("null")
        Assert.Equal("a=Ali b=null",
            "a={{name}} b={{missing}}".RenderContent(new { name = "Ali" }));
    }

    [Fact]
    public void NullColumnValue_DefaultNullValueText()
    {
        var t = "<h-embedded-data><![CDATA[select name, email from users order by name]]></h-embedded-data>[{{name}}:{{email}}]";
        Assert.Equal("[Ali:null][Sara:sara@x.com]", t.RenderContent(_conn));
    }

    [Fact]
    public void NullColumnValue_CustomNullValueText()
    {
        var t = "<h-embedded-data null-value=\"(none)\"><![CDATA[select name, email from users order by name]]></h-embedded-data>[{{name}}:{{email}}]";
        Assert.Equal("[Ali:(none)][Sara:sara@x.com]", t.RenderContent(_conn));
    }

    [Fact]
    public void RowValue_WinsOverOuterModel_OnSameKey()
    {
        var t = "<h-embedded-data><![CDATA[select name from users where code = {{code}}]]></h-embedded-data>[{{name}}]";
        Assert.Equal("[Ali]", t.RenderContent(_conn, new { code = "A1", name = "Outer" }));
    }

    [Fact]
    public void MarkerNames_AreCaseInsensitive()
    {
        Assert.Equal("x=Ali", "x={{NAME}}".RenderContent(new { name = "Ali" }));
    }

    [Fact]
    public void Marker_InsideNestedTemplateUri_IsFilled()
    {
        Write("part_X.txt", "CHILD-X");
        var main = Write("m.txt",
            "<h-embedded-template><![CDATA[{uri{.}}/part_{{suffix}}.txt]]></h-embedded-template>");

        Assert.Equal("CHILD-X", new Uri(main).RenderContent(new { suffix = "X" }));
    }

    [Fact]
    public void MasterDetail_ParentRowValueBindsInChildQuery()
    {
        // the defining reporting-engine pattern: the child template's query binds {{code}} from
        // the PARENT's current row, and a child with zero rows collapses (Ali has no email)
        Write("detail.txt",
            "<h-embedded-data><![CDATA[select email from users where code = {{code}} and email is not null]]></h-embedded-data><e>{{email}}</e>");
        var main = Write("main.txt",
            "<h-embedded-data><![CDATA[select code, name from users order by name]]></h-embedded-data>"
            + "<row>{{name}}:<h-embedded-template><![CDATA[{uri{.}}/detail.txt]]></h-embedded-template></row>");

        Assert.Equal("<row>Ali:</row><row>Sara:<e>sara@x.com</e></row>",
            new Uri(main).RenderContent(_conn));
    }

    [Fact]
    public void AsymmetricMarkers_OpenOverriddenCloseDefaulted()
    {
        // production templates set open-marker="{v1{" and leave close at "}}"
        var t = "<h-embedded-data open-marker=\"{v1{\"><![CDATA[select name from users order by name]]></h-embedded-data>name={v1{name}} ";
        Assert.Equal("name=Ali name=Sara ", t.RenderContent(_conn));
    }

    [Fact]
    public void DatePlaceholders_NowTomorrowYesterday()
    {
        var output = "t={now{yyyy-MM-dd}} tm={tomorrow{yyyy-MM-dd}} y={yesterday{yyyy-MM-dd}}"
            .RenderContent(new { });

        Assert.Equal(
            $"t={DateTime.Now:yyyy-MM-dd} tm={DateTime.Today.AddDays(1):yyyy-MM-dd} y={DateTime.Today.AddDays(-1):yyyy-MM-dd}",
            output);
    }

    [Fact]
    public void ZeroRows_RendersTheFileAsNothing()
    {
        var t = "BEFORE<h-embedded-data><![CDATA[select name from users where code = {{code}}]]></h-embedded-data>[{{name}}]AFTER";
        Assert.Equal("", t.RenderContent(_conn, new { code = "ZZ" }));
    }

    [Fact]
    public void EachRow_RepeatsTheWholeFile()
    {
        var t = "BEFORE<h-embedded-data><![CDATA[select name from users order by name]]></h-embedded-data>[{{name}}]AFTER";
        Assert.Equal("BEFORE[Ali]AFTERBEFORE[Sara]AFTER", t.RenderContent(_conn));
    }

    [Fact]
    public void UnderscoreAttributeVariants_AreAccepted()
    {
        // legacy accepted pre_render / connection_string alongside the dash forms
        var t = "<h-embedded-data pre_render=\"true\"><![CDATA[select name from users]]></h-embedded-data>{{name}}";
        var ex = Assert.ThrowsAny<Exception>(() => t.RenderContent(_conn));
        Assert.Contains("pre-render", ex.GetBaseException().Message);
    }


    [Fact]
    public void NullDataModel_StillReplacesMarkersWithTheNullValueText()
    {
        // legacy always constructed one QueryParams even for a null model, so markers became
        // "null" rather than leaking raw {{marker}} syntax into the output
        Assert.Equal("Hello null.", "Hello {{name}}.".RenderContent((object?)null));
        Assert.Equal("Hello null.", "Hello {{name}}.".RenderContent(_conn));
    }

    [Fact]
    public void MarkerValues_FormatWithTheCurrentCulture()
    {
        // legacy used value.ToString() with no provider; a localised template must keep
        // rendering as it did
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                new System.Globalization.CultureInfo("de-DE");

            // de-DE uses a comma as the decimal separator
            Assert.Equal("v=1,5", "v={{amount}}".RenderContent(new { amount = 1.5m }));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void DateMarkers_FormatWithTheCurrentCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            var culture = new System.Globalization.CultureInfo("fr-FR");
            System.Globalization.CultureInfo.CurrentCulture = culture;

            Assert.Equal($"m={DateTime.Now.ToString("MMMM", culture)}",
                "m={now{MMMM}}".RenderContent(new { }));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }
}
