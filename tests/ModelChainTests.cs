using Com.H.Text.Template2;
using Microsoft.Data.Sqlite;

namespace Com.H.Text.Template2.Tests;

/// <summary>
/// Marker resolution walks the data-model chain innermost-first, per key — the same semantic
/// <c>Com.H.Data.Common</c> applies to query parameters via <c>ReduceToUnique</c>.
/// </summary>
/// <remarks>
/// The original engine did not do this for the template body: it filled markers model-by-model
/// with a global string replace, so the first model consulted overwrote every marker it lacked
/// with its own null text, hiding all outer values. Verified against Com.H 10.2.0 — rendering
/// "name={{english_name}} url={{record_url}}" with [outer, row] produced "name=Ali url=", losing
/// the caller's URL entirely. These tests pin the corrected behaviour.
/// </remarks>
public class ModelChainTests : IDisposable
{
    private readonly SqliteConnection _conn;

    public ModelChainTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            create table insider (id integer, english_name text, entity_name text);
            insert into insider values (7, 'Ali', 'Acme Holdings');
            insert into insider values (8, 'Sara', null);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();

    private const string Template =
        "<h-embedded-data><![CDATA["
        + "select english_name, entity_name from insider where id = {{ref_id}}"
        + "]]></h-embedded-data>"
        + "<b>{{english_name}}</b><a href=\"{{record_url}}\">Review</a>";

    [Fact]
    public void CallerValue_SurvivesInsideADataBlock()
    {
        // the reported failure: record_url came from the caller, english_name from the row
        var output = Template.RenderContent(
            _conn, new { ref_id = 7, record_url = "https://app/record/7" });

        Assert.Equal("<b>Ali</b><a href=\"https://app/record/7\">Review</a>", output);
    }

    [Fact]
    public void RowValue_StillWinsOnAKeyBothModelsHave()
    {
        var output = Template.RenderContent(
            _conn,
            new { ref_id = 7, record_url = "u", english_name = "SHOULD-NOT-WIN" });

        Assert.Contains("<b>Ali</b>", output);
        Assert.DoesNotContain("SHOULD-NOT-WIN", output);
    }

    [Fact]
    public void NullColumn_FallsBackToTheCallerValueOfTheSameName()
    {
        // row 8 has a null entity_name; the caller supplies one
        var template =
            "<h-embedded-data><![CDATA["
            + "select english_name, entity_name from insider where id = {{ref_id}}"
            + "]]></h-embedded-data>[{{entity_name}}]";

        Assert.Equal("[Fallback Ltd]",
            template.RenderContent(_conn, new { ref_id = 8, entity_name = "Fallback Ltd" }));
    }

    [Fact]
    public void KeyMissingEverywhere_CollapsesToEmpty()
    {
        var template =
            "<h-embedded-data><![CDATA["
            + "select english_name from insider where id = {{ref_id}}"
            + "]]></h-embedded-data>[{{nowhere}}]";

        Assert.Equal("[]", template.RenderContent(_conn, new { ref_id = 7 }));
    }

    [Fact]
    public void ChainWorksThroughNestedTemplatesAndCustomMarkers()
    {
        var template =
            "<h-embedded-data marker=\"{v1{\"><![CDATA["
            + "select english_name from insider where id = {{ref_id}}"
            + "]]></h-embedded-data>[{v1{english_name}}|{{record_url}}]";

        Assert.Equal("[Ali|https://app/record/7]",
            template.RenderContent(_conn, new { ref_id = 7, record_url = "https://app/record/7" }));
    }

    [Fact]
    public void EncodedMarkers_ResolveThroughTheChainToo()
    {
        var template =
            "<h-embedded-data><![CDATA["
            + "select english_name from insider where id = {{ref_id}}"
            + "]]></h-embedded-data>[{html{title}}]";

        Assert.Equal("[Smith &amp; Sons]",
            template.RenderContent(_conn, new { ref_id = 7, title = "Smith & Sons" }));
    }

    // ---------------------------------------------------------------- strict mode

    // ThrowOnUnresolvedMarker is a typo detector, so it fires only for a name NO model in scope
    // declares. A name a model declares with a null value is data, not a mistake: a LEFT JOIN
    // with no match must not turn a development rehearsal of a real e-mail into an error, or the
    // check gets switched off exactly where it earns its keep.

    private static readonly TemplateOptions Strict = new() { ThrowOnUnresolvedMarker = true };

    [Fact]
    public void StrictMode_NullColumn_IsDataNotATypo()
    {
        // row 8's entity_name is NULL and nothing outer supplies one: empty, not an exception
        var template =
            "<h-embedded-data><![CDATA["
            + "select english_name, entity_name from insider where id = {{ref_id}}"
            + "]]></h-embedded-data>[{{english_name}}:{{entity_name}}]";

        Assert.Equal("[Sara:]", template.RenderContent(_conn, new { ref_id = 8 }, Strict));
    }

    [Fact]
    public void StrictMode_NullColumn_StillFallsBackToTheCallerValue()
    {
        var template =
            "<h-embedded-data><![CDATA["
            + "select english_name, entity_name from insider where id = {{ref_id}}"
            + "]]></h-embedded-data>[{{entity_name}}]";

        Assert.Equal("[Fallback Ltd]",
            template.RenderContent(_conn, new { ref_id = 8, entity_name = "Fallback Ltd" }, Strict));
    }

    [Fact]
    public void StrictMode_NullCallerValue_IsDataToo()
    {
        Assert.Equal("[]", "[{{name}}]".RenderContent(new { name = (string?)null }, Strict));
    }

    [Fact]
    public void StrictMode_JsonNull_IsDataToo()
    {
        var template =
            "<h-embedded-data content-type=\"json\"><![CDATA["
            + "[{\"name\":\"Ali\",\"email\":null}]"
            + "]]></h-embedded-data>[{{name}}:{{email}}]";

        Assert.Equal("[Ali:]", template.RenderContent(new { }, Strict));
    }

    [Fact]
    public void StrictMode_NameNoModelDeclares_StillThrows()
    {
        var template =
            "<h-embedded-data><![CDATA["
            + "select english_name from insider where id = {{ref_id}}"
            + "]]></h-embedded-data>[{{nowhere}}]";

        var ex = Assert.ThrowsAny<Exception>(() =>
            template.RenderContent(_conn, new { ref_id = 7 }, Strict));

        Assert.Contains("nowhere", ex.GetBaseException().Message);
    }

    [Fact]
    public void StrictMode_DedicatedMarker_ReportsANameItsBlockLacks_EvenIfAnOuterModelHasIt()
    {
        // {v1{record_url}} names the block, and only the caller has record_url. Quietly that
        // renders empty, because a dedicated marker never falls back; strictly it is reported,
        // since addressing the wrong model is exactly the kind of mistake the check exists for.
        var template =
            "<h-embedded-data marker=\"{v1{\"><![CDATA["
            + "select english_name from insider where id = {{ref_id}}"
            + "]]></h-embedded-data>[{v1{record_url}}]";
        var model = new { ref_id = 7, record_url = "https://app/record/7" };

        Assert.Equal("[]", template.RenderContent(_conn, model));

        var ex = Assert.ThrowsAny<Exception>(() => template.RenderContent(_conn, model, Strict));
        Assert.Contains("record_url", ex.GetBaseException().Message);
    }
}
