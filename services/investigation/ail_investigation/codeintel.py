"""Heuristic code intelligence: rank commits in a release range as candidate causes.

This is review triage, not proof. Signals: the failure-signature component appearing in changed
files, symptom keywords, risky patterns added (or mitigations removed) in the diff, architecture
specific changes and the commit message.
"""

from __future__ import annotations

import re

from ail_schemas import CandidateCommit

from ail_investigation.repos import CommitInfo

SYMPTOM_KEYWORDS = {
    "memory": ["cache", "buffer", "pool", "alloc", "retain", "static", "singleton", "shared", "heap", "lifetime"],
    "latency": ["sync", "lock", "mutex", "query", "retry", "sleep", "loop", "wait", "blocking"],
    "failure": ["exception", "throw", "null", "abort", "assert", "crash", "timeout"],
}
_RISK = [
    r"\bstatic\b.*\b(map|vector|list|set)\b", r"make_shared|shared_ptr", r"\bentries_?\[", r"emplace|push_back",
    r"\bcache\b", r"\bretain", r"keep\s+\w*\s*alive", r"strong\s+ref",
]
_MITIGATION = [r"\berase\b", r"\bevict", r"\bremove\b", r"\bclear\b", r"\brelease\b", r"weak_ptr|WeakReference",
               r"dispose|\bfree\(|\bdelete\b"]
_ARCH_TOKENS = {"arm64": ["arm", "aarch64", "neon"], "x64": ["x64", "x86_64", "avx", "sse"]}


def _norm(text: str) -> str:
    return re.sub(r"[^a-z0-9]", "", text.lower())


def _symptoms(metric: str, signatures: list[str], supporting_significant: list[str]) -> list[str]:
    kinds = set()
    blob = " ".join(signatures).lower()
    if metric == "memory_mb" or "memory_mb" in supporting_significant or "oom" in blob or "memory" in blob:
        kinds.add("memory")
    if metric == "latency_ms" or "timeout" in blob:
        kinds.add("latency")
    if metric == "failure_rate":
        kinds.add("failure")
    return [kw for k in sorted(kinds) for kw in SYMPTOM_KEYWORDS[k]]


def _signature_components(signatures: list[str]) -> list[str]:
    comps = []
    for s in signatures:
        tail = s.split("|", 1)[-1]
        comps.extend(p for p in tail.split(".") if len(p) > 3)
    return list(dict.fromkeys(comps))


def rank_commits(
    commits: list[CommitInfo],
    metric: str,
    signatures: list[str],
    supporting_significant: list[str],
    architecture: str | None,
    top: int = 5,
) -> list[CandidateCommit]:
    keywords = _symptoms(metric, signatures, supporting_significant)
    components = _signature_components(signatures)
    arch_tokens = _ARCH_TOKENS.get((architecture or "").lower(), [])
    scored: list[tuple[float, CommitInfo, list[str], list[str], str | None]] = []
    for c in commits:
        reasons: list[str] = []
        snippets: list[str] = []
        paths_norm = " ".join(_norm(f.path) for f in c.files)
        added = [ln[1:].strip() for f in c.files for ln in f.patch.splitlines() if ln.startswith("+")]
        removed = [ln[1:].strip() for f in c.files for ln in f.patch.splitlines() if ln.startswith("-")]

        matched = next((comp for comp in components if _norm(comp) in paths_norm), None)
        sig_score = 1.0 if matched else 0.0
        if matched:
            reasons.append(f"changes files for failure-signature component '{matched}'")

        hits = [kw for kw in keywords if kw in paths_norm or kw in " ".join(added).lower()]
        kw_score = min(1.0, len(set(hits)) / 3)
        if hits:
            reasons.append("symptom keywords in changed code: " + ", ".join(sorted(set(hits))[:4]))

        risk_lines = [ln for ln in added if any(re.search(p, ln, re.I) for p in _RISK)]
        mit_added = sum(1 for ln in added if any(re.search(p, ln, re.I) for p in _MITIGATION))
        mit_removed = [ln for ln in removed if any(re.search(p, ln, re.I) for p in _MITIGATION)]
        net_risk = len(risk_lines) + 2 * len(mit_removed) - mit_added
        risk_score = max(0.0, min(1.0, net_risk / 4))
        if risk_lines:
            reasons.append(f"adds {len(risk_lines)} risky retention/allocation pattern(s) without matching cleanup")
            snippets.extend(risk_lines[:2])
        if mit_removed:
            reasons.append(f"removes {len(mit_removed)} cleanup/release line(s)")
            snippets.extend(mit_removed[:1])

        arch_hit = bool(arch_tokens) and any(t in paths_norm or t in " ".join(added).lower() for t in arch_tokens)
        arch_score = 1.0 if arch_hit else 0.0
        if arch_hit:
            reasons.append(f"touches {architecture} specific code")
        msg_hit = any(kw in c.message.lower() for kw in keywords)
        msg_score = 1.0 if msg_hit else 0.0

        score = 0.35 * sig_score + 0.25 * kw_score + 0.25 * risk_score + 0.10 * arch_score + 0.05 * msg_score
        scored.append((round(score, 4), c, reasons, snippets, matched))

    scored.sort(key=lambda t: t[0], reverse=True)
    out = []
    for rank, (score, c, reasons, snippets, matched) in enumerate(scored[:top], start=1):
        stem = c.files[0].path.rsplit("/", 1)[-1].split(".")[0] if c.files else ""
        component = matched or "".join(p.title() for p in re.split(r"[_\-]", stem)) or None
        out.append(CandidateCommit(
            sha=c.sha, message=c.message, files=[f.path for f in c.files], component=component,
            score=score, rank=rank, reasons=reasons, snippets=snippets,
        ))
    return out
