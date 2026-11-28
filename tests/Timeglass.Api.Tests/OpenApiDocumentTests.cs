using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using static Timeglass.Api.Tests.ApiHost;

namespace Timeglass.Api.Tests;

/// <summary>
/// Keeps <c>src/Timeglass.Api/openapi.json</c>, which the frontend's typed client is built from,
/// in step with the endpoints. CI runs this test, so a change to the API that isn't reflected in
/// the committed document fails the build.
/// </summary>
public sealed class OpenApiDocumentTests
{
    /// <summary>Set to <c>1</c> to write the generated document over the committed one.</summary>
    public const string UpdateVariable = "TIMEGLASS_UPDATE_OPENAPI";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string CommittedPath
    {
        get
        {
            var folder = new DirectoryInfo(AppContext.BaseDirectory);
            while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Timeglass.slnx")))
            {
                folder = folder.Parent;
            }

            return Path.Combine(
                folder?.FullName ?? throw new InvalidOperationException("Could not find the repository root (Timeglass.slnx)."),
                "src",
                "Timeglass.Api",
                "openapi.json");
        }
    }

    private static async Task<string> GenerateAsync()
    {
        await using var host = await StartAsync(Utc("2026-11-05T09:00Z"), [], Ct);
        var json = await host.Client.GetStringAsync(new Uri(TimeglassApi.OpenApiPath, UriKind.Relative), Ct);

        // Re-indent so the committed file has one stable layout, with a final newline.
        return JsonNode.Parse(json)!.ToJsonString(new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).ReplaceLineEndings("\n") + "\n";
    }

    [Fact]
    public async Task Committed_document_matches_the_endpoints()
    {
        var generated = await GenerateAsync();
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            await File.WriteAllTextAsync(CommittedPath, generated, Ct);
        }

        File.Exists(CommittedPath).ShouldBeTrue($"{CommittedPath} is missing. Run the tests with {UpdateVariable}=1 to create it.");
        var committed = (await File.ReadAllTextAsync(CommittedPath, Ct)).ReplaceLineEndings("\n");
        committed.ShouldBe(generated, $"The API changed but {CommittedPath} did not. Run the tests with {UpdateVariable}=1 and commit the result.");
    }

    [Fact]
    public async Task Document_describes_every_endpoint()
    {
        var document = JsonNode.Parse(await GenerateAsync())!;

        document["info"]!["title"]!.GetValue<string>().ShouldBe("Timeglass");
        document["servers"].ShouldBeNull();
        document["paths"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal).ShouldBe(
        [
            "/api/days/{date}/apps/{appKey}",
            "/api/days/{date}/summary",
            "/api/days/{date}/timeline",
            "/api/export",
            "/api/intervals",
            "/api/pause",
            "/api/range/categories",
            "/api/range/category-application-titles",
            "/api/range/category-applications",
            "/api/range/summary",
            "/api/resume",
            "/api/settings",
            "/api/status",
        ]);
        document["paths"]!["/api/days/{date}/summary"]!["get"]!["operationId"]!.GetValue<string>().ShouldBe("GetDaySummary");
        document["paths"]!["/api/days/{date}/summary"]!["get"]!["responses"]!["400"].ShouldNotBeNull();
        document["paths"]!["/api/range/summary"]!["get"]!["parameters"]!.AsArray()
            .Select(p => (p!["name"]!.GetValue<string>(), p["schema"]!["format"]!.GetValue<string>()))
            .ShouldBe([("from", "date"), ("to", "date")]);
        document["paths"]!["/api/settings"]!.AsObject().Select(o => o.Key).ShouldBe(["get", "put"], ignoreOrder: true);
        document["paths"]!["/api/intervals"]!["delete"]!["requestBody"]!["required"]!.GetValue<bool>().ShouldBeTrue();
        document["paths"]!["/api/intervals"]!["delete"]!["parameters"]!.AsArray()
            .Select(p => (p!["name"]!.GetValue<string>(), p["schema"]!["format"]!.GetValue<string>()))
            .ShouldBe([("from", "date"), ("to", "date")]);
        document["components"]!["schemas"]!["DaySummaryDto"]!["properties"]!["activeMs"]!["type"]!.GetValue<string>().ShouldBe("integer");
    }
}
