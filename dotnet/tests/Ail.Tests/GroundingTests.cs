using System.Text.Json;
using Ail.Investigation;
using Ail.Schemas;

namespace Ail.Tests;

public class GroundingTests
{
    private static readonly List<EvidenceItem> Evidence = new()
    {
        new() { Id = "E1", Kind = "observation", Statement = "Failure rate rose from 0.40% to 10.67%." },
        new() { Id = "C1", Kind = "correlation", Statement = "Atlas 2.1 was deployed 3 minutes before onset." },
    };
    private static readonly string[] Shas = { "3f28a91c07b5e4d2a9f68b1e0c5d7a3948bf12ce" };

    private static List<string> Check(string json) => Grounding.Validate(JsonDocument.Parse(json).RootElement, Evidence, Shas);

    private static string Output(string summary = "Failure rate rose after the Atlas rollout.", string rationale = "The rise follows the deployment.",
        string refs = "\"E1\",\"C1\"", string confidence = "medium") =>
        $$"""{"summary":"{{summary}}","hypotheses":[{"title":"Deployment regression","rationale":"{{rationale}}","confidence":"{{confidence}}","supporting_evidence":[{{refs}}]}],"recommended_steps":[{"step":"Review the rollout","evidence":["E1"]}]}""";

    [Fact] public void GroundedOutputPasses() => Assert.Empty(Check(Output()));

    [Fact] public void NumbersMustComeFromEvidence() =>
        Assert.Contains(Check(Output(summary: "Failure rate rose to 99.9 percent.")), p => p.Contains("numbers not present"));

    [Fact] public void EvidenceNumbersAreAllowed() => Assert.Empty(Check(Output(summary: "It rose from 0.40 to 10.67.")));

    [Fact] public void UnknownEvidenceIdsAreRejected() =>
        Assert.Contains(Check(Output(refs: "\"E9\"")), p => p.Contains("unknown evidence"));

    [Fact] public void HypothesesNeedEvidence() =>
        Assert.Contains(Check(Output(refs: "")), p => p.Contains("no supporting evidence"));

    [Fact] public void InventedCommitHashesAreRejected() =>
        Assert.Contains(Check(Output(rationale: "Likely commit deadbeef1234 caused it.")), p => p.Contains("unknown commit hash"));

    [Fact] public void KnownCommitHashesAreAccepted() =>
        Assert.Empty(Check(Output(rationale: "Likely commit 3f28a91 caused it.")));

    [Fact] public void CertaintyWordsAreRejected() =>
        Assert.Contains(Check(Output(rationale: "This definitely caused the rise.")), p => p.Contains("certainty"));

    [Fact] public void MalformedOutputIsRejected() => Assert.NotEmpty(Check("""{"summary":"ok"}"""));
}
