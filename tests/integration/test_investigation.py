from __future__ import annotations

import json

import httpx
import pytest
from ail_investigation import (
    InvestigationAgent,
    OpenAICompatibleProvider,
    RulesProvider,
    create_provider,
)
from ail_investigation.codeintel import rank_commits
from ail_investigation.grounding import validate_output
from ail_investigation.llm import LLMResult
from ail_investigation.report import render_issue, render_markdown
from ail_investigation.repos import FixtureRepo, GitHubRepo
from ail_schemas import EvidenceItem
from ail_simulator import repo_root

CULPRIT = "3f28a91c07b5e4d2a9f68b1e0c5d7a3948bf12ce"


@pytest.fixture(scope="module")
def inc(flagship):
    return flagship.incidents[0]


# --- grounding ---------------------------------------------------------------------------------
EVIDENCE = [
    EvidenceItem(id="E1", kind="anomaly", statement="Failure rate changed from 0.40% to 8.50% in ARM64 + App 2.1."),
    EvidenceItem(id="C1", kind="code", statement="Commit 3f28a91 ranks 1 of 7 commits."),
]
SHAS = [CULPRIT]


def _out(text="Failure rate rose from 0.40% to 8.50%.", refs=("E1",), conf="medium"):
    return {"summary": text,
            "hypotheses": [{"title": "Commit 3f28a91 may be responsible", "rationale": text, "confidence": conf,
                            "supporting_evidence": list(refs)}],
            "recommended_steps": [{"step": "Review the commit.", "evidence": ["C1"]}]}


def test_grounded_output_passes():
    assert validate_output(_out(), EVIDENCE, SHAS) == []


@pytest.mark.parametrize("mutate,needle", [
    (lambda o: o.update(summary="Failure rate rose to 37%."), "numbers not present"),
    (lambda o: o["hypotheses"][0].update(supporting_evidence=["E9"]), "unknown evidence"),
    (lambda o: o["hypotheses"][0].update(supporting_evidence=[]), "no supporting evidence"),
    (lambda o: o["hypotheses"][0].update(rationale="Blame commit deadbee1 for this."), "unknown commit hash"),
    (lambda o: o["hypotheses"][0].update(rationale="This definitely caused it."), "certainty"),
    (lambda o: o["hypotheses"][0].update(confidence="certain"), "invalid confidence"),
    (lambda o: o.pop("hypotheses"), "schema"),
])
def test_ungrounded_output_is_rejected(mutate, needle):
    out = _out()
    mutate(out)
    problems = validate_output(out, EVIDENCE, SHAS)
    assert any(needle in p for p in problems), problems


# --- agent -------------------------------------------------------------------------------------
def test_rules_report_is_grounded_and_names_the_injected_commit(inc):
    inv = inc.investigation
    assert inv.guardrail["provider_output_valid"] and inv.guardrail["generated_by"] == "rules"
    assert inv.candidate_commits[0].sha == CULPRIT
    assert inv.candidate_commits[0].score - inv.candidate_commits[1].score > 0.5
    assert inv.hypotheses[0].title.startswith("Commit 3f28a91")
    assert inv.hypotheses[0].label == "hypothesis"
    cited = {r for h in inv.hypotheses for r in h.supporting_evidence}
    assert cited <= {e.id for e in inv.observed_facts}


def test_facts_and_hypotheses_are_kept_separate_in_reports(inc):
    md = render_markdown(inc)
    assert "Observed facts (deterministic analysis)" in md and "not verified facts" in md
    title, body = render_issue(inc)
    assert title.startswith("[AIL-1001]")
    assert body.index("## Observed facts") < body.index("## AI-generated hypotheses (unverified)")
    assert "not proof of causation" in body


class _Scripted:
    def __init__(self, text):
        self.name, self.text, self.prompts = "scripted", text, []

    def complete(self, system, user):
        self.prompts.append(user)
        return LLMResult(self.text, 10, 5)


def test_agent_falls_back_when_model_invents_telemetry(flagship, inc):
    bad = json.dumps(_out("Failure rate rose to 99.9% on 12345 devices.", refs=("E1",)))
    provider = _Scripted(bad)
    inv = InvestigationAgent(provider).investigate(inc)
    assert inv.guardrail["provider_output_valid"] is False and inv.guardrail["fallback_used"] is True
    assert inv.provider == "rules"
    assert "99.9" not in inv.summary


def test_agent_falls_back_on_non_json_and_provider_errors(inc):
    assert InvestigationAgent(_Scripted("not json")).investigate(inc).guardrail["fallback_used"] is True

    class Boom:
        name = "boom"

        def complete(self, *_):
            raise RuntimeError("network down")

    assert InvestigationAgent(Boom()).investigate(inc).provider == "rules"


def test_untrusted_commit_messages_are_not_sent_to_the_model(inc):
    provider = _Scripted("{}")
    InvestigationAgent(provider).investigate(inc)
    assert "Cache decoded asset buffers" not in provider.prompts[0]
    assert "E1" in provider.prompts[0]


def test_valid_model_output_is_accepted(inc):
    good = _Scripted(RulesProvider().complete("", json.dumps({"evidence": [
        {"id": e.id, "kind": e.kind, "statement": e.statement, "data": e.data} for e in inc.investigation.observed_facts]})).text)
    inv = InvestigationAgent(good).investigate(inc)
    assert inv.provider == "scripted" and inv.guardrail["provider_output_valid"]


# --- providers and repos -----------------------------------------------------------------------
def test_openai_compatible_provider_parses_usage_and_requires_https():
    def handler(request: httpx.Request) -> httpx.Response:
        assert request.url.path == "/v1/chat/completions"
        body = json.loads(request.content)
        assert body["temperature"] == 0 and body["response_format"] == {"type": "json_object"}
        return httpx.Response(200, json={"choices": [{"message": {"content": "{}"}}],
                                         "usage": {"prompt_tokens": 7, "completion_tokens": 3}})

    client = httpx.Client(base_url="https://llm.example/v1", transport=httpx.MockTransport(handler))
    res = OpenAICompatibleProvider("https://llm.example/v1", "k", "m", client=client).complete("s", "u")
    assert (res.prompt_tokens, res.completion_tokens) == (7, 3)
    with pytest.raises(ValueError):
        OpenAICompatibleProvider("http://llm.example/v1", "k", "m")
    assert OpenAICompatibleProvider("http://localhost:11434/v1", None, "m")


def test_provider_factory_defaults_to_offline_rules():
    assert create_provider(env={}).name == "rules"
    with pytest.raises(ValueError):
        create_provider("nonsense", env={})


def test_github_repo_compare_uses_read_only_endpoints():
    seen = []

    def handler(request: httpx.Request) -> httpx.Response:
        seen.append((request.method, request.url.path))
        if "/compare/" in request.url.path:
            return httpx.Response(200, json={"commits": [{"sha": "abc1234", "commit": {
                "message": "Fix\n\nbody", "author": {"date": "2026-03-01T00:00:00Z"}}, "author": {"login": "dev"}}]})
        return httpx.Response(200, json={"files": [{"filename": "a.cpp", "patch": "+x"}]})

    repo = GitHubRepo("octo/atlas", token="t", client=httpx.Client(base_url=GitHubRepo.API, transport=httpx.MockTransport(handler)))
    (commit,) = repo.compare("v1", "v2")
    assert commit.message == "Fix" and commit.files[0].path == "a.cpp"
    assert {m for m, _ in seen} == {"GET"}
    with pytest.raises(ValueError):
        GitHubRepo("not a repo; rm -rf /")


def test_code_ranking_finds_the_culprit_among_decoys():
    repo = FixtureRepo(repo_root() / "docs" / "scenarios" / "repos" / "atlas.json")
    commits = repo.compare("v2.0", "v2.1")
    assert len(commits) == 7
    ranked = rank_commits(commits, "failure_rate", ["OOM_KILL|MemoryCache.Allocate"], ["memory_mb"], "arm64")
    assert ranked[0].sha == CULPRIT and ranked[0].component == "MemoryCache"
    assert all(c.score < 0.25 for c in ranked[1:])
    # without the failure signature the memory keywords and diff patterns alone still rank it first
    blind = rank_commits(commits, "memory_mb", [], [], None)
    assert blind[0].sha == CULPRIT
