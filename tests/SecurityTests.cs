using System.Data.Common;
using Com.H.Text.Template2;
using Microsoft.Data.Sqlite;

namespace Com.H.Text.Template2.Tests;

/// <summary>
/// Regression tests for defects found by an adversarial review of the engine. The unifying
/// principle: <b>substituted values are data, never template syntax</b>. A value arriving from a
/// database row, a REST payload or a caller's model is emitted verbatim and never re-examined.
/// </summary>
public class SecurityTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly string _dir;

    public SecurityTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            create table notes (body text);
            insert into notes values ('see {{apiToken}} here');
            """;
        cmd.ExecuteNonQuery();
        _dir = Path.Combine(Path.GetTempPath(), "sec_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { _conn.Dispose(); try { Directory.Delete(_dir, true); } catch { } }

    private string Write(string name, string content)
    {
        var p = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    // ---------------------------------------------------------------- template injection

    [Fact]
    public void RowValue_ContainingAMarker_DoesNotLeakTheCallerModel()
    {
        // a DB value that looks like a marker must NOT be resolved against the caller's model
        var template =
            "<h-embedded-data><![CDATA[select body from notes]]></h-embedded-data>[{{body}}]";

        var output = template.RenderContent(_conn, new { apiToken = "SECRET-123" });

        Assert.Equal("[see {{apiToken}} here]", output);
        Assert.DoesNotContain("SECRET-123", output);
    }

    [Fact]
    public void JsonRowValue_ContainingAMarker_IsEmittedVerbatim()
    {
        var template =
            "<h-embedded-data content-type=\"json\"><![CDATA["
            + "[ {\"note\":\"x{{apiToken}}x\"} ]"
            + "]]></h-embedded-data>[{{note}}]";

        var output = template.RenderContent(new { apiToken = "SECRET-123" });

        Assert.Equal("[x{{apiToken}}x]", output);
    }

    [Fact]
    public void ValueContainingAnIncludeTag_IsNotFetched()
    {
        // otherwise any untrusted value is an arbitrary file read / SSRF primitive
        var secret = Write("secret.txt", "TOP-SECRET");
        var payload = "<h-embedded-template><![CDATA[" + new Uri(secret).AbsoluteUri
                      + "]]></h-embedded-template>";

        var output = "[{{note}}]".RenderContent(new { note = payload });

        Assert.Contains("h-embedded-template", output);
        Assert.DoesNotContain("TOP-SECRET", output);
    }

    [Fact]
    public void ValueContainingADataTag_IsNotExecuted()
    {
        var payload = "<h-embedded-data><![CDATA[select body from notes]]></h-embedded-data>";

        var output = "[{{note}}]".RenderContent(_conn, new { note = payload });

        Assert.Equal("[" + payload + "]", output);
    }

    // ---------------------------------------------------------------- header injection

    [Fact]
    public void HeaderValue_WithALineBreak_IsRejected()
    {
        var template =
            "<h-embedded-template header-X-Trace=\"{{trace}}\"><![CDATA[https://example.invalid/x]]>"
            + "</h-embedded-template>";

        var ex = Assert.ThrowsAny<Exception>(() =>
            template.RenderContent(new { trace = "ok\r\nX-Injected: evil" }));

        Assert.Contains("line break", ex.GetBaseException().Message);
    }

    [Fact]
    public async Task HeaderAttributes_ActuallyReachTheWire_IncludingContentHeaders()
    {
        // HttpRequestHeaders rejects content-class names; they must not be silently dropped
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        var captured = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var buffer = new byte[8192];
            var read = await stream.ReadAsync(buffer, 0, buffer.Length);
            var request = System.Text.Encoding.ASCII.GetString(buffer, 0, read);

            var body = "OK"u8.ToArray();
            var response = System.Text.Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\nOK");
            await stream.WriteAsync(response, 0, response.Length);
            return request;
        });

        var template =
            $"<h-embedded-template header-Content-Type=\"application/json\" "
            + $"header-Authorization=\"Bearer {{{{token}}}}\">"
            + $"<![CDATA[http://127.0.0.1:{port}/tpl]]></h-embedded-template>";

        await template.RenderContentAsync(new { token = "secret123" });

        var wire = await captured;
        listener.Stop();

        Assert.Contains("Authorization: Bearer secret123", wire);
        Assert.Contains("Content-Type: application/json", wire);
    }

    // ---------------------------------------------------------------- malformed input

    [Fact]
    public void UnparseableDataTag_IsReported_NotEchoedIntoOutput()
    {
        // a forgotten CDATA must not publish the query or a connection-string attribute
        var template =
            "<h-embedded-data connection-string=\"Server=x;Password=hunter2\">"
            + "select * from secrets</h-embedded-data>";

        var ex = Assert.ThrowsAny<Exception>(() => template.RenderContent(_conn, new { }));

        Assert.Contains("could not be parsed", ex.GetBaseException().Message);
        Assert.DoesNotContain("hunter2", ex.GetBaseException().Message);
    }

    [Fact]
    public void SlippedCdataTerminator_DoesNotMergeTwoDataTags()
    {
        var template =
            "<h-embedded-data><![CDATA[select 1]]</h-embedded-data><li>{{a}}</li>"
            + "<h-embedded-data><![CDATA[select 2]]></h-embedded-data><b>{{b}}</b>";

        Assert.ThrowsAny<Exception>(() => template.RenderContent(_conn, new { }));
    }

    [Fact]
    public void SimilarlyNamedElement_IsNotTreatedAsADataTag()
    {
        var output = "<h-embedded-data-summary>kept</h-embedded-data-summary>"
            .RenderContent(new { });

        Assert.Equal("<h-embedded-data-summary>kept</h-embedded-data-summary>", output);
    }

    // ---------------------------------------------------------------- performance

    [Fact]
    public void LargeValuesContainingMarkerSyntax_RenderInLinearTime()
    {
        // the previous implementation re-scanned substituted text, making this quadratic:
        // 120 KB of marker-shaped row data took over a minute
        var big = string.Concat(Enumerable.Repeat("{{x", 40_000)); // ~120 KB
        var template = "[{{note}}]";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var output = template.RenderContent(new { note = big });
        sw.Stop();

        Assert.Equal("[" + big + "]", output);
        Assert.True(sw.ElapsedMilliseconds < 5_000,
            $"took {sw.ElapsedMilliseconds} ms — marker filling should be a single pass");
    }
}
