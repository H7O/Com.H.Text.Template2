using System.Collections.Generic;
using Com.H.Text.Template2;
using Microsoft.Data.Sqlite;

namespace Com.H.Text.Template2.Tests;

/// <summary>
/// The engine knows about templates, markers and rows — and nothing about SQL, JSON, HTTP or any
/// other source. Each of those is a substitutable <see cref="ITemplateDataProvider"/>; these
/// tests pin that separation.
/// </summary>
public class ProviderCompositionTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly string _dir;

    public ProviderCompositionTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "create table t (name text); insert into t values ('FROM-SQL');";
        cmd.ExecuteNonQuery();
        _dir = Path.Combine(Path.GetTempPath(), "pc_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { _conn.Dispose(); try { Directory.Delete(_dir, true); } catch { } }

    private const string SqlBlock =
        "<h-embedded-data><![CDATA[select name from t]]></h-embedded-data>[{{name}}]";

    private const string JsonBlock =
        "<h-embedded-data content-type=\"json\"><![CDATA[[{\"name\":\"FROM-JSON\"}]]]>"
        + "</h-embedded-data>[{{name}}]";

    // ---------------------------------------------------------------- composition

    [Fact]
    public void Compose_RoutesEachBlockToWhicheverProviderClaimsIt()
    {
        var provider = TemplateDataProviders.Compose(
            new JsonTemplateDataProvider(),
            new DbTemplateDataProvider(_conn));

        Assert.Equal("[FROM-SQL]", SqlBlock.RenderContent(provider));
        Assert.Equal("[FROM-JSON]", JsonBlock.RenderContent(provider));
    }

    [Fact]
    public void Compose_TakesTheFirstProviderThatAnswers()
    {
        var first = new StubProvider("FIRST", handles: true);
        var second = new StubProvider("SECOND", handles: true);

        var provider = TemplateDataProviders.Compose(first, second);

        Assert.Equal("[FIRST]", SqlBlock.RenderContent(provider));
        Assert.False(second.WasAsked);
    }

    [Fact]
    public void Compose_FallsThroughAProviderThatDeclines()
    {
        var declines = new StubProvider("NEVER", handles: false);
        var answers = new StubProvider("ANSWERED", handles: true);

        var provider = TemplateDataProviders.Compose(declines, answers);

        Assert.Equal("[ANSWERED]", SqlBlock.RenderContent(provider));
        Assert.True(declines.WasAsked);
    }

    [Fact]
    public void Compose_EveryProviderDeclines_MeansNoDataSource()
    {
        // which renders the template once from the models in scope, rather than failing
        var provider = TemplateDataProviders.Compose(new StubProvider("x", handles: false));

        Assert.Equal("[CALLER]", SqlBlock.RenderContent(provider, new { name = "CALLER" }));
    }

    // ---------------------------------------------------------------- self-contained blocks

    [Fact]
    public void JsonBlock_NeedsNoProvider_BecauseItCarriesItsOwnData()
    {
        Assert.Equal("[FROM-JSON]", JsonBlock.RenderContent(new { }));
    }

    [Fact]
    public void SqlBlock_WithNoProvider_RendersOnceFromTheCallerModel()
    {
        Assert.Equal("[CALLER]", SqlBlock.RenderContent(new { name = "CALLER" }));
    }

    [Fact]
    public void SqlBlock_WithNoProvider_AndStrictMode_Throws()
    {
        Assert.ThrowsAny<Exception>(() =>
            SqlBlock.RenderContent(new { }, new TemplateOptions { ThrowIfQueryPresent = true }));
    }

    [Fact]
    public void JsonBlock_InStrictMode_StillRenders_BecauseItIsSelfContained()
    {
        Assert.Equal("[FROM-JSON]",
            JsonBlock.RenderContent(new { }, new TemplateOptions { ThrowIfQueryPresent = true }));
    }

    [Fact]
    public void RestBackedBlock_NeedsNoHttpCodeInAnyProvider()
    {
        // A block with src has its URI resolved by the content resolver BEFORE any provider is
        // asked, so the JSON provider only ever parses text. That is why this package contains no
        // HTTP client: swapping the resolver changes how a REST payload is obtained — caching,
        // auth, retries — without touching a provider.
        var options = new TemplateOptions
        {
            ContentResolver = (uri, attrs, ct) =>
                new ValueTask<string?>("""[ {"name":"Ali"}, {"name":"Sara"} ]""")
        };

        var template =
            "<h-embedded-data content-type=\"json\" src=\"https://api.example/users\">"
            + "</h-embedded-data>[{{name}}]";

        Assert.Equal("[Ali][Sara]", template.RenderContent(new { }, options));
    }

    // ---------------------------------------------------------------- content resolver

    [Fact]
    public void ContentResolver_ReplacesTheBuiltInReader()
    {
        var asked = new List<string>();
        var options = new TemplateOptions
        {
            ContentResolver = (uri, attrs, ct) =>
            {
                asked.Add(Path.GetFileName(uri.LocalPath));
                return new ValueTask<string?>(
                    uri.LocalPath.EndsWith("child.txt", StringComparison.OrdinalIgnoreCase)
                        ? "[child:{{name}}]"
                        : "<h-embedded-template><![CDATA[{uri{.}}/child.txt]]></h-embedded-template>");
            }
        };

        // nothing exists on disk; the resolver supplies both the root and the include
        var main = Path.Combine(_dir, "nonexistent-main.txt");
        var output = new Uri(main).RenderContent(new { name = "X" }, options);

        Assert.Equal("[child:X]", output);
        Assert.Equal(new[] { "nonexistent-main.txt", "child.txt" }, asked);
    }

    [Fact]
    public void ContentResolver_CanDelegateToTheBuiltInOne()
    {
        var served = 0;
        var child = Path.Combine(_dir, "child.txt");
        File.WriteAllText(child, "REAL-CHILD");
        var main = Path.Combine(_dir, "main.txt");
        File.WriteAllText(main,
            "<h-embedded-template><![CDATA[{uri{.}}/child.txt]]></h-embedded-template>");

        var options = new TemplateOptions
        {
            ContentResolver = async (uri, attrs, ct) =>
            {
                served++;
                return await TemplateContent.FetchAsync(uri, attrs, ct);
            }
        };

        Assert.Equal("REAL-CHILD", new Uri(main).RenderContent(new { }, options));
        Assert.Equal(2, served);
    }

    [Fact]
    public void ContentResolver_SeesTheIncludeTagsAttributes()
    {
        Dictionary<string, string?>? seen = null;
        var options = new TemplateOptions
        {
            ContentResolver = (uri, attrs, ct) =>
            {
                if (attrs.Count > 0) seen = new Dictionary<string, string?>(attrs!);
                return new ValueTask<string?>("ok");
            }
        };

        var template =
            "<h-embedded-template tenant=\"acme\" header-X-Trace=\"{{trace}}\">"
            + "<![CDATA[https://example.invalid/x]]></h-embedded-template>";

        template.RenderContent(new { trace = "T-1" }, options);

        Assert.NotNull(seen);
        Assert.Equal("acme", seen!["tenant"]);
        Assert.Equal("T-1", seen["header-X-Trace"]);   // marker-filled before the resolver sees it
    }

    private sealed class StubProvider : ITemplateDataProvider
    {
        private readonly string _value;
        private readonly bool _handles;
        public bool WasAsked { get; private set; }

        public StubProvider(string value, bool handles) { _value = value; _handles = handles; }

        public ValueTask<IReadOnlyList<dynamic>?> GetDataAsync(
            TemplateDataRequest request, CancellationToken cancellationToken = default)
        {
            WasAsked = true;
            IReadOnlyList<dynamic>? rows = _handles
                ? new List<dynamic> { new Dictionary<string, object> { ["name"] = _value } }
                : null;
            return new ValueTask<IReadOnlyList<dynamic>?>(rows);
        }
    }
}
