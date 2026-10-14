using System.Net;
using System.Text;
using System.Text.Json;
using Ail.Api;
using Ail.Ingestion;
using Ail.Simulator;
using Ail.Investigation;
using Microsoft.AspNetCore.Builder;

namespace Ail.Tests;

public sealed class ApiFixture : IAsyncDisposable
{
    public WebApplication App { get; }
    public HttpClient Http { get; }

    private ApiFixture(WebApplication app, string baseUrl)
    {
        App = app;
        Http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public static async Task<ApiFixture> StartAsync(string? apiToken = null, string? readToken = null, double? rate = null)
    {
        var app = ApiHost.Create(new ApiOptions
        {
            DbPath = ":memory:", ArchivePath = ":memory:", Inline = true, ApiToken = apiToken, ReadToken = readToken, RateLimit = rate,
            Urls = new[] { "http://127.0.0.1:0" },
        });
        await app.StartAsync();
        return new ApiFixture(app, app.Urls.First());
    }

    public Task<HttpResponseMessage> Post(string path, string json, string? token = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (token is not null) req.Headers.Add("Authorization", $"Bearer {token}");
        return Http.SendAsync(req);
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

public class IngestionAndApiTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public async Task AcceptsValidEventsAndDropsDuplicates()
    {
        await using var api = await ApiFixture.StartAsync();
        var body = $$"""{"events":[{{SchemaTests.ValidEvent}}]}""";
        var first = await api.Post("/api/v1/events", body);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(1, Json(await first.Content.ReadAsStringAsync()).GetProperty("accepted").GetInt32());
        await api.Post("/api/v1/events", body);
        var meta = Json(await api.Http.GetStringAsync("/api/v1/meta"));
        Assert.Equal(1, meta.GetProperty("counts").GetProperty("raw_events").GetInt32());
    }

    [Fact]
    public async Task RejectsInvalidItemsIndividually()
    {
        await using var api = await ApiFixture.StartAsync();
        var pii = SchemaTests.ValidEvent.Replace("\"d1\"", "\"d2\"").TrimEnd('}') + ",\"dimensions\":{\"email\":\"a@b.c\"}}";
        var body = "[" + SchemaTests.ValidEvent + ",{\"timestamp\":\"2026-03-10T12:00:00Z\"}," + pii + "]";
        var res = Json(await (await api.Post("/api/v1/events", body)).Content.ReadAsStringAsync());
        Assert.Equal(1, res.GetProperty("accepted").GetInt32());
        Assert.Equal(new[] { 1, 2 }, res.GetProperty("rejected").EnumerateArray().Select(r => r.GetProperty("index").GetInt32()).ToArray());
    }

    [Fact]
    public async Task MalformedBodyIsA400()
    {
        await using var api = await ApiFixture.StartAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post("/api/v1/events", "{not json")).StatusCode);
    }

    [Fact]
    public async Task WritesRequireTheApiTokenWhenConfigured()
    {
        await using var api = await ApiFixture.StartAsync(apiToken: "s3cret");
        var body = $$"""[{{SchemaTests.ValidEvent}}]""";
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Post("/api/v1/events", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Post("/api/v1/events", body, "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await api.Post("/api/v1/events", body, "s3cret")).StatusCode);
    }

    [Fact]
    public async Task ReadsRequireTheReadTokenWhenConfigured()
    {
        await using var api = await ApiFixture.StartAsync(readToken: "reader");
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Http.GetAsync("/api/v1/incidents")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Http.GetAsync("/health")).StatusCode);
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/incidents");
        req.Headers.Add("Authorization", "Bearer reader");
        Assert.Equal(HttpStatusCode.OK, (await api.Http.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task WritesAreRateLimited()
    {
        await using var api = await ApiFixture.StartAsync(rate: 1);
        var body = $$"""[{{SchemaTests.ValidEvent}}]""";
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) codes.Add((await api.Post("/api/v1/events", body)).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
    }

    [Fact]
    public async Task UnknownRoutesAndEmptyStoreAreHandled()
    {
        await using var api = await ApiFixture.StartAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await api.Http.GetAsync("/api/v1/incidents/AIL-9999")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await api.Http.GetAsync("/api/v1/series?metric=failure_rate")).StatusCode);
        Assert.Equal("[]", await api.Http.GetStringAsync("/api/v1/incidents"));
    }

    [Fact]
    public async Task ServesPortalAndMetrics()
    {
        await using var api = await ApiFixture.StartAsync();
        var home = await api.Http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Contains("OS Intelligence Lab", await home.Content.ReadAsStringAsync());
        Assert.Contains("ail_", await api.Http.GetStringAsync("/metrics"));
    }

    [Fact]
    public async Task DeploymentsAreStoredAndListed()
    {
        await using var api = await ApiFixture.StartAsync();
        var change = """{"deployment_id":"d-1","timestamp":"2026-03-10T12:00:00Z","kind":"app_deployment","component":"Atlas","dimension":"application_version","to_version":"2.1"}""";
        var posted = await api.Post("/api/v1/deployments", change);
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        Assert.Equal(1, Json(await posted.Content.ReadAsStringAsync()).GetProperty("accepted").GetInt32());
        var list = Json(await api.Http.GetStringAsync("/api/v1/changes"));
        Assert.Equal("d-1", list[0].GetProperty("deployment_id").GetString());
    }

    [Fact]
    public async Task SimulatedDataIsServedWithIncidentDetail()
    {
        using var store = new TelemetryStore();
        var sc = ScenarioLoader.Load("scenario-003");
        FleetSimulator.SimulateToStore(sc, store, 10_000);
        Workflow.RunAnalysis(store);
        var inc = Assert.Single(store.ListIncidents());
        Assert.Equal("arm64", inc.Cohorts[0].Dimensions["architecture"]);
        Assert.Equal("3f28a91c07b5e4d2a9f68b1e0c5d7a3948bf12ce", inc.Investigation!.CandidateCommits[0].Sha);
        Assert.NotEmpty(Ail.Investigation.Report.RenderMarkdown(inc));
    }
}

public class RateLimiterTests
{
    [Fact]
    public void BurstThenRefill()
    {
        var limiter = new RateLimiter(2);
        long t = 0, second = System.Diagnostics.Stopwatch.Frequency;
        var results = Enumerable.Range(0, 5).Select(_ => limiter.Allow("a", t).Allowed).ToList();
        Assert.Equal(new[] { true, true, true, true, false }, results);
        Assert.True(limiter.Allow("a", t + second).Allowed);
        Assert.True(limiter.Allow("b", t).Allowed);
    }
}
