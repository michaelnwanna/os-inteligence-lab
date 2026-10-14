"""Natural-language telemetry questions, answered by the same statistics as the rest of the platform.

The parser is rule based and deliberately small: it extracts a metric, a time window and
include/exclude cohorts, then runs the cohort statistics. It never guesses silently; the parsed
query is returned with every answer so the user can see how the question was understood.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field

from ail_schemas import DIMENSIONS, Incident, hhmm

from ail_detection.labels import cohort_label, format_value, metric_label
from ail_detection.workspace import Workspace

_NUMBER_WORDS = {"a": 1, "an": 1, "one": 1, "two": 2, "three": 3, "four": 4, "five": 5, "six": 6, "seven": 7,
                 "eight": 8, "nine": 9, "ten": 10, "twelve": 12, "twenty": 20, "thirty": 30}
_UNIT_MINUTES = {"minute": 1, "min": 1, "hour": 60, "day": 1440, "week": 10080}
_EXCLUDE_SPLIT = re.compile(r"\b(?:but not|excluding|except(?: for)?|without|not on|and not)\b")
_VERSION = r"(\d+(?:\.\d+)*)"
_METRIC_WORDS = {"latency_ms": ("latency", "slow", "slower", "response time"),
                 "memory_mb": ("memory", "leak", "ram")}


@dataclass
class Query:
    text: str
    metric: str = "failure_rate"
    include: dict[str, str] = field(default_factory=dict)
    exclude: list[dict[str, str]] = field(default_factory=list)
    window_minutes: int | None = None
    notes: list[str] = field(default_factory=list)


def _cohort_terms(text: str, vocab: dict[str, list[str]]) -> dict[str, str]:
    found: dict[str, str] = {}
    for arch in vocab.get("architecture", []):
        if re.search(rf"\b{re.escape(arch.lower())}\b", text):
            found["architecture"] = arch
    for model in vocab.get("device_model", []):
        if re.search(rf"\b{re.escape(model.lower())}\b", text):
            found["device_model"] = model
    for key, pattern in (("application_version", rf"\b(?:app(?:lication)?|version|v)\s*{_VERSION}"),
                         ("driver_version", rf"\bdriver\s*{_VERSION}"),
                         ("os_version", rf"\b(?:os|build)\s*{_VERSION}")):
        m = re.search(pattern, text)
        if m and m.group(1) in vocab.get(key, []):
            found[key] = m.group(1)
    return found


def parse(text: str, vocab: dict[str, list[str]]) -> Query:
    low = text.lower().strip().rstrip("?.")
    q = Query(text=text)
    for metric, words in _METRIC_WORDS.items():
        if any(w in low for w in words):
            q.metric = metric
            break
    m = re.search(r"(?:last|past|previous|within the last)\s+(\d+|[a-z]+)\s+(minute|min|hour|day|week)s?", low)
    if m:
        n = int(m.group(1)) if m.group(1).isdigit() else _NUMBER_WORDS.get(m.group(1))
        if n:
            q.window_minutes = n * _UNIT_MINUTES[m.group(2)]
        else:
            q.notes.append(f"could not read the time window '{m.group(1)}'")
    left, *right = _EXCLUDE_SPLIT.split(low, maxsplit=1)
    q.include = _cohort_terms(left, vocab)
    if right:
        excluded = _cohort_terms(right[0], vocab)
        if excluded:
            q.exclude.append(excluded)
        else:
            q.notes.append("could not recognise the excluded cohort")
    if not q.include:
        q.notes.append("no cohort recognised; showing the whole fleet")
    return q


def _verdict(stats: dict, kind: str, ws: Workspace) -> dict:
    min_ratio = ws.cfg.cohort.min_ratio_rate if kind == "rate" else ws.cfg.cohort.min_ratio_gauge
    significant = stats["llr"] > 12 and stats["ratio"] >= min_ratio
    return {**stats, "regressed": bool(significant)}


def answer(ws: Workspace, q: Query, incidents: list[Incident] | None = None) -> dict:
    cube, scanner = ws.cube(q.metric), ws.scanner(q.metric)
    end = cube.T
    start = max(ws.baseline_end, end - q.window_minutes) if q.window_minutes else ws.baseline_end
    clipped = bool(q.window_minutes) and end - q.window_minutes < ws.baseline_end
    inc_mask = cube.mask(q.include)
    inc_stats = _verdict(scanner.evaluate(inc_mask, start, end), cube.kind, ws)
    excl = []
    for dims in q.exclude:
        stats = _verdict(scanner.evaluate(cube.mask(dims), start, end), cube.kind, ws)
        excl.append({"cohort": cohort_label(dims), "dimensions": dims, **stats})
    matches = []
    for inc in incidents or []:
        dims = inc.cohorts[0].dimensions if inc.cohorts else {}
        if inc.metric != q.metric or any(dims.get(k) != v for k, v in q.include.items()):
            continue
        if any(all(dims.get(k) == v for k, v in ex.items()) for ex in q.exclude):
            continue
        if inc.onset.timestamp() / 60 < ws.t0 + start:
            continue
        matches.append({"id": inc.id, "title": inc.title, "severity": inc.severity,
                        "cohort": cohort_label(dims), "onset": inc.onset.isoformat()})
    ok_excl = all(not e["regressed"] for e in excl)
    scope = cohort_label(q.include)
    unit = q.metric
    if inc_stats["regressed"] and ok_excl:
        verdict = "regression"
        text = (f"Yes. {metric_label(unit)} for {scope} is {format_value(unit, inc_stats['observed'])} against an expected "
                f"{format_value(unit, inc_stats['baseline'])} ({inc_stats['ratio']:.1f}x)")
        text += "".join(f", while {e['cohort']} is not elevated ({e['ratio']:.1f}x)" for e in excl) + "."
    elif inc_stats["regressed"]:
        verdict = "also_affects_excluded"
        text = (f"{scope} is elevated ({inc_stats['ratio']:.1f}x) but so is "
                + ", ".join(e["cohort"] for e in excl if e["regressed"]) + ", so this is not specific to the first cohort.")
    else:
        verdict = "no_regression"
        text = f"No significant {metric_label(unit).lower()} regression for {scope} ({inc_stats['ratio']:.1f}x expected)."
    return {
        "query": {"text": q.text, "metric": q.metric, "include": q.include, "exclude": q.exclude,
                  "window_minutes": q.window_minutes, "notes": q.notes},
        "window": [hhmm(ws.t0 + start), hhmm(ws.t0 + end - 1)],
        "window_clipped_to_available_data": clipped or (q.window_minutes or 0) > end,
        "verdict": verdict, "answer": text, "include": {"cohort": scope, **inc_stats}, "exclude": excl,
        "incidents": matches,
    }


def vocab_of(ws: Workspace) -> dict[str, list[str]]:
    cube = ws.cube("failure_rate")
    return {d: sorted(set(cube.vocab[d])) for d in DIMENSIONS}
