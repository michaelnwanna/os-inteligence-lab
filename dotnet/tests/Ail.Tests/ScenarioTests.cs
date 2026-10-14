using Ail.Investigation;
using Ail.Simulator;

namespace Ail.Tests;

/// <summary>End-to-end acceptance: every scenario is scored against its ground truth.</summary>
public class ScenarioTests
{
    public static IEnumerable<object[]> ScenarioIds() => ScenarioLoader.LoadAll().Select(s => new object[] { s.Id });

    [Fact]
    public void AllTenScenariosAreAvailable() => Assert.Equal(10, ScenarioLoader.LoadAll().Count);

    [Theory]
    [MemberData(nameof(ScenarioIds))]
    public void ScenarioMeetsGroundTruth(string id)
    {
        var sc = ScenarioLoader.Load(id);
        var r = Evaluation.Run(sc, sc.Seed, 30_000);

        Assert.Equal(0, r.Fp);
        Assert.Equal(0, r.Fn);
        Assert.Equal(r.Expected, r.Incidents);
        Assert.All(r.CohortOk, ok => Assert.True(ok, "affected cohort must match the injected one exactly"));
        Assert.All(r.TriggerRank, rank => Assert.Equal(1, rank));
        Assert.All(r.AiValid, ok => Assert.True(ok, "rules report must pass the grounding validator"));
        Assert.All(r.CulpritTop1, ok => Assert.True(ok, "injected commit must be ranked first"));
        Assert.All(r.Latencies, l => Assert.InRange(l, 0, 30));
    }

    [Fact]
    public void AnalysisIsReproducible()
    {
        var sc = ScenarioLoader.Load("scenario-003");
        var a = Evaluation.Run(sc, sc.Seed, 10_000);
        var b = Evaluation.Run(sc, sc.Seed, 10_000);
        Assert.Equal(a.Latencies, b.Latencies);
        Assert.Equal(a.Incidents, b.Incidents);
    }
}
