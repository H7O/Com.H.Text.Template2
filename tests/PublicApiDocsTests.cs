using System.Reflection;
using System.Text.RegularExpressions;
using Com.H.Text.Template2;

namespace Com.H.Text.Template2.Tests;

/// <summary>
/// The shipped XML documentation should describe the public API and nothing else.
/// </summary>
/// <remarks>
/// The compiler emits a doc entry for every member with a <c>///</c> comment regardless of
/// accessibility, so without filtering the file describes internal methods and private fields.
/// The csproj strips a configured list of internal types after build; this test fails if a new
/// internal type is documented and not added to that list, so the list cannot silently rot.
/// </remarks>
public class PublicApiDocsTests
{
    private static readonly Regex MemberName = new(@"<member name=""(?<kind>[A-Z]):(?<target>[^""]+)""");

    [Fact]
    public void ShippedXmlDocuments_OnlyPublicTypes()
    {
        var assembly = typeof(TemplateExtensions).Assembly;
        var xmlPath = Path.ChangeExtension(assembly.Location, ".xml");

        Assert.True(File.Exists(xmlPath), $"No XML documentation beside the assembly: {xmlPath}");

        var publicTypes = assembly.GetExportedTypes()
            .Select(t => t.FullName!.Replace('+', '.'))
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(publicTypes);

        var offenders = new List<string>();
        foreach (Match match in MemberName.Matches(File.ReadAllText(xmlPath)))
        {
            var target = match.Groups["target"].Value;

            // "M:Ns.Type.Method(args)" / "F:Ns.Type.Field" -> the declaring type
            var withoutArgs = target.Split('(')[0];
            var declaringType = match.Groups["kind"].Value == "T"
                ? target
                : withoutArgs.Substring(0, withoutArgs.LastIndexOf('.'));

            // a member of a public type, or a nested public type, is fine
            if (publicTypes.Contains(declaringType)) continue;
            if (publicTypes.Any(p => declaringType.StartsWith(p + ".", StringComparison.Ordinal))) continue;

            offenders.Add(target);
        }

        Assert.True(offenders.Count == 0,
            "The shipped XML documents non-public members. Add their type to "
            + "<DocFilterInternalType> in Com.H.Text.Template2.csproj:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void PublicApi_IsTheExpectedSurface()
    {
        // a deliberate inventory: adding or removing a public type should be a conscious act,
        // not something noticed after publishing
        var actual = typeof(TemplateExtensions).Assembly.GetExportedTypes()
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[]
        {
            "DbTemplateDataProvider",
            "ITemplateDataProvider",
            "JsonTemplateDataProvider",
            "TemplateConnection",
            "TemplateConnectionFactory",
            "TemplateContent",
            "TemplateContentResolver",
            "TemplateDataProviders",
            "TemplateDataRequest",
            "TemplateExtensions",
            "TemplateMarkers",
            "TemplateOptions",
        }, actual);
    }
}
