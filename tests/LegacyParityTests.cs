using Com.H.Text.Template2;
using Microsoft.Data.Sqlite;

namespace Com.H.Text.Template2.Tests;

/// <summary>
/// Pins the semantics inherited from the original Com.H.Text.Template engine. Every expected
/// value here was captured by running the ORIGINAL engine before this package's native engine
/// replaced it — these tests are the compatibility contract for existing template files.
/// </summary>
/// <remarks>
/// <para>
/// Two <b>deliberate</b> divergences, both replacing behaviour that was a defect rather than a
/// contract:
/// </para>
/// <para>
/// <b>1. Model-chain resolution</b> (see <c>ModelChainTests</c>). Markers now resolve per key,
/// innermost model first, so a caller value the current row lacks stays reachable. The original
/// overwrote it with the row's null text — verified against Com.H 10.2.0, where rendering with
/// [outer, row] silently dropped the caller's value. Collision priority is unchanged: the row
/// still wins a key both models have.
/// </para>
/// <para>
/// <b>2. Unresolved markers collapse to empty</b>. The original emitted the literal word
/// <c>null</c>, which reached readers of production emails. A template wanting placeholder text
/// says so in its query (<c>coalesce</c>), where the meaning is known — so the
/// <c>null-value</c> attribute is gone with it. Set
/// <c>TemplateOptions.ThrowOnUnresolvedMarker</c> in development to make the silence loud.
/// </para>
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

    // ---- divergence 2: unresolved markers collapse instead of emitting "null" ----

    [Fact]
    public void UnmatchedMarker_CollapsesToEmpty()
    {
        // was "a=Ali b=null" on the original engine
        Assert.Equal("a=Ali b=",
            "a={{name}} b={{missing}}".RenderContent(new { name = "Ali" }));
    }

    [Fact]
    public void NullColumnValue_CollapsesToEmpty()
    {
        var t = "<h-embedded-data><![CDATA[select name, email from users order by name]]></h-embedded-data>[{{name}}:{{email}}]";
        Assert.Equal("[Ali:][Sara:sara@x.com]", t.RenderContent(_conn));
    }

    [Fact]
    public void PlaceholderText_ComesFromTheQuery_NotAnAttribute()
    {
        // replaces null-value="(none)": the query says what a missing value means
        var t = "<h-embedded-data><![CDATA["
              + "select name, coalesce(email, '(none)') as email from users order by name"
              + "]]></h-embedded-data>[{{name}}:{{email}}]";
        Assert.Equal("[Ali:(none)][Sara:sara@x.com]", t.RenderContent(_conn));
    }

    [Fact]
    public void UnresolvedMarker_CanBeMadeLoud()
    {
        var ex = Assert.ThrowsAny<Exception>(() =>
            "a={{missing}}".RenderContent(
                new { other = 1 },
                new TemplateOptions { ThrowOnUnresolvedMarker = true }));

        Assert.Contains("missing", ex.GetBaseException().Message);
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
        var t = "<h-embedded-data marker=\"{v1{\"><![CDATA[select name from users order by name]]></h-embedded-data>name={v1{name}} ";
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
        // legacy accepted content_type / connection_string alongside the dash forms, so
        // attribute names still normalise '_' to '-'
        var t = "<h-embedded-data content_type=\"sql\"><![CDATA[select name from users order by name]]>"
              + "</h-embedded-data>[{{name}}]";
        Assert.Equal("[Ali][Sara]", t.RenderContent(_conn));
    }

    [Fact]
    public void NullDataModel_DoesNotLeakRawMarkerSyntax()
    {
        // a chain always exists, so an unfillable marker collapses rather than surviving as
        // literal "{{name}}" text in the output
        Assert.Equal("Hello .", "Hello {{name}}.".RenderContent((object?)null));
        Assert.Equal("Hello .", "Hello {{name}}.".RenderContent(_conn));
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
