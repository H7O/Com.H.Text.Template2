using System.Text.RegularExpressions;

using Com.H.Text.Template2;

namespace Com.H.Text.Template2.Tests;

/// <summary>
/// Unit tests for the translation between the templating engine's parameter model
/// (<see cref="TemplateDataModel"/>) and the data layer's (<c>DbQueryParams</c>).
/// </summary>
public class MappingTests
{
    [Fact]
    public void MarkerPattern_Defaults_MatchDoubleBraces()
    {
        var pattern = TemplateDataModel.PatternFor(null, null);
        var match = Regex.Match("where a = {{name}}", pattern);

        Assert.True(match.Success);
        Assert.Equal("name", match.Groups["param"].Value);
        Assert.Equal("{{", match.Groups["open_marker"].Value);
        Assert.Equal("}}", match.Groups["close_marker"].Value);
    }

    [Fact]
    public void MarkerPattern_CustomMarkers_AreHonoured()
    {
        var pattern = TemplateDataModel.PatternFor("[[", "]]");
        var match = Regex.Match("where a = [[city]]", pattern);

        Assert.True(match.Success);
        Assert.Equal("city", match.Groups["param"].Value);
    }

    [Fact]
    public void MarkerPattern_RegexMetacharactersInMarkers_AreEscaped()
    {
        // '|' and '$' are regex metacharacters; unescaped they would change the pattern's meaning.
        var pattern = TemplateDataModel.PatternFor("$(", ")$");
        var match = Regex.Match("where a = $(total)$", pattern);

        Assert.True(match.Success);
        Assert.Equal("total", match.Groups["param"].Value);
    }

    [Fact]
    public void MarkerPattern_EmptyMarkers_FallBackToDefaults()
    {
        var pattern = TemplateDataModel.PatternFor("", "");
        Assert.Matches(pattern, "where a = {{x}}");
    }

    [Fact]
    public void MarkerPattern_AsymmetricMarkers_AreSupported()
    {
        // Real production templates set only open-marker and let the close default, e.g.
        //   <h-embedded-data open-marker="{v1{"> ... {v1{name}}
        // so the two markers must be escaped and defaulted independently.
        var pattern = TemplateDataModel.PatternFor("{v1{", null);
        var match = Regex.Match("temp 1 name = {v1{name}}", pattern);

        Assert.True(match.Success);
        Assert.Equal("name", match.Groups["param"].Value);
    }

    [Fact]
    public void MapQueryParams_AsymmetricMarkers_SurviveTheMapping()
    {
        var input = new List<TemplateDataModel>
        {
            // open marker overridden, close marker left at its default.
            new() { Model = new { name = "x" }, MarkerPattern = TemplateDataModel.PatternFor("{v1{") }
        };

        var mapped = DbTemplateDataProvider.MapQueryParams(input);

        Assert.NotNull(mapped);
        Assert.Matches(Assert.Single(mapped!).QueryParamsRegex, "a = {v1{name}}");
    }

    [Fact]
    public void MapQueryParams_Null_ReturnsNull()
    {
        Assert.Null(DbTemplateDataProvider.MapQueryParams(null));
    }

    [Fact]
    public void MapQueryParams_EmptySequence_ReturnsNull()
    {
        Assert.Null(DbTemplateDataProvider.MapQueryParams(new List<TemplateDataModel>()));
    }

    [Fact]
    public void MapQueryParams_EntriesWithoutDataModel_AreSkipped()
    {
        var input = new List<TemplateDataModel>
        {
            new() { Model = null },
            new() { Model = new { a = 1 } }
        };

        var mapped = DbTemplateDataProvider.MapQueryParams(input);

        Assert.NotNull(mapped);
        Assert.Single(mapped!);
    }

    [Fact]
    public void MapQueryParams_CarriesDataModelAndMarkersAcross()
    {
        var model = new { country = "JO" };
        var input = new List<TemplateDataModel>
        {
            new() { Model = model, MarkerPattern = TemplateDataModel.PatternFor("<%", "%>") }
        };

        var mapped = DbTemplateDataProvider.MapQueryParams(input);

        Assert.NotNull(mapped);
        var single = Assert.Single(mapped!);
        Assert.Same(model, single.DataModel);
        Assert.Matches(single.QueryParamsRegex, "a = <%country%>");
    }

    [Fact]
    public void MapQueryParams_PreservesOrderOfMultipleModels()
    {
        var input = new List<TemplateDataModel>
        {
            new() { Model = new { first = 1 } },
            new() { Model = new { second = 2 }, MarkerPattern = TemplateDataModel.PatternFor("[[", "]]") }
        };

        var mapped = DbTemplateDataProvider.MapQueryParams(input);

        Assert.NotNull(mapped);
        Assert.Equal(2, mapped!.Count);
        Assert.Matches(mapped[0].QueryParamsRegex, "a = {{first}}");
        Assert.Matches(mapped[1].QueryParamsRegex, "a = [[second]]");
    }
}
