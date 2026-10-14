using System.Net;
using System.Text;
using System.Text.Json;
using Ail.Api;
using Ail.Detection;
using Ail.Ingestion;
using Ail.Investigation;
using Ail.Simulator;

namespace Ail.Tests;

public class HistoryTests
{
    private static List<Ail.Schemas.Incident> Analyze(int seed, IncidentArchive? archive)
    {
        using var store = new TelemetryStore();
        FleetSimulator.SimulateToStore(ScenarioLoader.Load("scenario-003"), store, 10_000, seed);
        return Workflow.RunAnalysis(store, archive: archive);
    }

    [Fact]
    public void SecondRunOfTheSameRegressionIsFlaggedAsRecurring()
    {
        using var archive = new IncidentArchive();
        Assert.Empty(Assert.Single(Analyze(3, archive)).SimilarIncidents);

        var again = Assert.Single(Analyze(4, archive));
        var match = Assert.Single(again.SimilarIncidents);
        Assert.True((bool)match["recurring"]!);
        Assert.Contains(again.Investigation!.ObservedFacts, e => e.Kind == "history");
        Assert.Contains(again.Investigation.Hypotheses, h => h.Title.Contains("recurrence"));
        Assert.True((bool)again.Investigation.Guardrail["provider_output_valid"]!);
    }

    [Fact]
    public void ARunIsNeverComparedWithItself()
    {
        using var archive = new IncidentArchive();
        Analyze(3, archive);
        Assert.Empty(Assert.Single(Analyze(3, archive)).SimilarIncidents);
    }

    [Fact]
    public void UnrelatedIncidentsScoreLow()
    {
        var a = Assert.Single(Analyze(3, null));
        var b = Assert.Single(Analyze(3, null));
        b.Metric = "latency_ms";
        b.Cohorts[0].Dimensions = new() { ["os_version"] = "13.0" };
        b.Signatures.Clear();
        b.Correlations.Clear();
        Assert.True(Similarity.Compare(a, b).Score < 0.5);
    }

    [Fact]
    public void SignatureTokensSplitOnPunctuation() =>
        Assert.Equal(new[] { "allocate", "kill", "memorycache", "oom" }, Similarity.SignatureTokens("OOM_KILL|MemoryCache.Allocate").OrderBy(x => x));
}

public class NlqTests
{
    private static readonly Dictionary<string, List<string>> Vocab = new()
    {
        ["architecture"] = new() { "arm64", "x64", "x86" }, ["device_model"] = new() { "Nova-A1" },
        ["application_version"] = new() { "2.0", "2.1" }, ["driver_version"] = new() { "7.13", "7.14" }, ["os_version"] = new() { "12.4" },
    };

    [Fact]
    public void ParsesCohortsExclusionsMetricAndWindow()
    {
        var q = Nlq.Parse("Show latency regressions in the last two hours affecting ARM64 but not x64", Vocab);
        Assert.Equal("latency_ms", q.Metric);
        Assert.Equal(120, q.WindowMinutes);
        Assert.Equal("arm64", q.Include["architecture"]);
        Assert.Equal("x64", Assert.Single(q.Exclude)["architecture"]);
        Assert.Empty(q.Notes);
    }

    [Fact]
    public void ReadsVersionsAndSaysSoWhenItUnderstandsNothing()
    {
        Assert.Equal("2.1", Nlq.Parse("failures on app 2.1 with driver 7.14", Vocab).Include["application_version"]);
        Assert.Contains(Nlq.Parse("what is going on", Vocab).Notes, n => n.Contains("no cohort recognised"));
        Assert.Contains(Nlq.Parse("arm64 but not penguins", Vocab).Notes, n => n.Contains("excluded cohort"));
    }

    [Fact]
    public void AnswersFromTheSameStatisticsAsTheRestOfThePlatform()
    {
        using var store = new TelemetryStore();
        FleetSimulator.SimulateToStore(ScenarioLoader.Load("scenario-003"), store, 20_000);
        var incidents = Ail.Investigation.Workflow.RunAnalysis(store);
        var ws = new Workspace(store, new DetectionConfig());

        var yes = Nlq.Answer(ws, Nlq.Parse("regressions affecting ARM64 but not x64", Nlq.VocabOf(ws)), incidents);
        Assert.Equal("regression", yes["verdict"]);
        Assert.Single((List<Dictionary<string, object?>>)yes["incidents"]!);

        var no = Nlq.Answer(ws, Nlq.Parse("failures on x64", Nlq.VocabOf(ws)), incidents);
        Assert.Equal("no_regression", no["verdict"]);
        Assert.Empty((List<Dictionary<string, object?>>)no["incidents"]!);
    }

    [Fact]
    public async Task ApiExposesTheQueryEndpoint()
    {
        await using var api = await ApiFixture.StartAsync();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await api.Http.GetAsync("/api/v1/query?q=hi")).StatusCode);
    }
}

public class GitHubRepoTests
{
    private sealed class Fake : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Seen = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request);
            var path = request.RequestUri!.AbsolutePath;
            var json = path switch
            {
                "/repos/acme/atlas/compare/v2.0...v2.1" => """{"commits":[{"sha":"abc1234def","commit":{"message":"Cache buffers\n\nbody","author":{"date":"2026-03-01T10:00:00Z"}},"author":{"login":"dev1"}}]}""",
                "/repos/acme/atlas/commits/abc1234def" => """{"files":[{"filename":"src/MemoryCache.cs","status":"modified","additions":12,"deletions":1,"patch":"+retain"}]}""",
                "/repos/acme/atlas/issues" => """{"html_url":"https://github.com/acme/atlas/issues/7"}""",
                _ => null,
            };
            return Task.FromResult(json is null ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private static (GitHubRepo Repo, Fake Handler) Make(string token = "t0ken")
    {
        var fake = new Fake();
        return (new GitHubRepo("acme/atlas", token, new HttpClient(fake) { BaseAddress = new Uri("https://api.github.com") }), fake);
    }

    [Fact]
    public void ComparesTwoRefsIntoCommitsWithFiles()
    {
        var (repo, fake) = Make();
        var commit = Assert.Single(repo.Compare("v2.0", "v2.1"));
        Assert.Equal("abc1234def", commit.Sha);
        Assert.Equal("Cache buffers", commit.Message);
        Assert.Equal("dev1", commit.Author);
        Assert.Equal("src/MemoryCache.cs", Assert.Single(commit.Files).Path);
        Assert.All(fake.Seen, r => Assert.Equal("Bearer t0ken", r.Headers.Authorization!.ToString()));
    }

    [Fact]
    public void ErrorsFromGitHubSurface() =>
        Assert.Throws<HttpRequestException>(() => Make().Repo.Compare("v1", "v9"));

    [Theory]
    [InlineData("not a repo")]
    [InlineData("../etc/passwd")]
    [InlineData("a/b/c")]
    public void RejectsMalformedRepositoryNames(string name)
    {
        Assert.Throws<ArgumentException>(() => new GitHubRepo(name));
        Assert.Null(RepoProviders.Get(name, "github"));
    }

    [Fact]
    public void FixtureNamesStayOffline() => Assert.IsType<FixtureRepo>(RepoProviders.Get("fixture/atlas", "auto"));

    [Fact]
    public void CreatesAnIssueAndReturnsItsUrl()
    {
        var (repo, _) = Make();
        Assert.Equal("https://github.com/acme/atlas/issues/7", repo.CreateIssue("title", "body", new[] { "x" }));
    }

    [Fact]
    public void IssueDraftSeparatesFactsFromHypotheses()
    {
        using var store = new TelemetryStore();
        FleetSimulator.SimulateToStore(ScenarioLoader.Load("scenario-003"), store, 10_000);
        var inc = Assert.Single(Ail.Investigation.Workflow.RunAnalysis(store));
        var (title, body) = Ail.Investigation.Report.RenderIssue(inc);
        Assert.StartsWith("[AIL-1001]", title);
        Assert.Contains("## Observed facts", body);
        Assert.Contains("## AI-generated hypotheses (unverified)", body);
        Assert.True(body.IndexOf("## Observed facts") < body.IndexOf("## AI-generated hypotheses"));
    }
}
