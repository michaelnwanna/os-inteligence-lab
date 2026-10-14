"""`ail` command-line interface."""

from __future__ import annotations

import argparse
import os
import sys
import time
from pathlib import Path

import httpx
from ail_detection import DetectionConfig
from ail_ingestion.store import TelemetryStore
from ail_investigation import create_provider, run_analysis
from ail_investigation.history import IncidentArchive
from ail_investigation.report import render_issue, render_markdown
from ail_simulator import Simulator, iter_events, list_scenarios, load_scenario, simulate_to_store

DEFAULT_DB = "data/ail.db"
FLAGSHIP = "scenario-003"


def _archive() -> IncidentArchive:
    return IncidentArchive(os.environ.get("AIL_ARCHIVE", "data/archive.db"))


def _fresh_store(path: str, force: bool) -> TelemetryStore:
    p = Path(path)
    if p.exists():
        if not force:
            sys.exit(f"{path} already exists; pass --force to overwrite it")
        for suffix in ("", "-wal", "-shm"):
            Path(str(p) + suffix).unlink(missing_ok=True)
    return TelemetryStore(path)


def _progress(done: int, total: int) -> None:
    if done == total or done % 30 == 0:
        print(f"\r  simulated {done}/{total} minutes", end="", flush=True)
    if done == total:
        print()


def _print_summary(incidents) -> None:
    if not incidents:
        print("No incidents detected.")
        return
    for inc in incidents:
        dims = inc.cohorts[0].dimensions if inc.cohorts else {}
        trig = inc.correlations[0] if inc.correlations else None
        print(f"\n{inc.id}  {inc.title}  [{inc.severity}, {inc.status}]")
        print(f"  onset {inc.onset:%H:%M} UTC, first detection {inc.first_detection:%H:%M} UTC, "
              f"{inc.metric}: {inc.baseline:.4g} -> {inc.observed:.4g}")
        if dims:
            print(f"  cohort: {dims}")
        if trig:
            print(f"  top correlated change: {trig.change.component} {trig.change.to_version or ''} "
                  f"(score {trig.score:.2f})")
        if inc.investigation and inc.investigation.hypotheses:
            h = inc.investigation.hypotheses[0]
            print(f"  hypothesis [{h.confidence}]: {h.title}")


def cmd_simulate(a: argparse.Namespace) -> None:
    sc = load_scenario(a.scenario)
    store = _fresh_store(a.db, a.force)
    print(f"Simulating {sc.id} ({sc.name}) with {a.devices or sc.population.devices:,} devices -> {a.db}")
    simulate_to_store(sc, store, devices=a.devices, seed=a.seed, progress=_progress)
    print(store.counts())


def cmd_analyze(a: argparse.Namespace) -> None:
    store = TelemetryStore(a.db)
    start = time.perf_counter()
    incidents = run_analysis(store, DetectionConfig(), create_provider(a.llm), a.repo_mode, not a.no_investigate,
                             archive=_archive())
    print(f"Analysis finished in {time.perf_counter() - start:.1f}s")
    _print_summary(incidents)


def cmd_serve(a: argparse.Namespace) -> None:
    import uvicorn
    from ail_api.app import create_app

    print(f"Portal: http://{a.host}:{a.port}")
    uvicorn.run(create_app(a.db), host=a.host, port=a.port, log_level="warning")


def cmd_demo(a: argparse.Namespace) -> None:
    sc = load_scenario(a.scenario)
    store = _fresh_store(a.db, True)
    n = a.devices or sc.population.devices
    print(f"1/3 Simulating {n:,} devices for '{sc.name}'")
    simulate_to_store(sc, store, devices=a.devices, progress=_progress)
    print("2/3 Detecting anomalies, isolating cohorts, correlating changes, investigating")
    incidents = run_analysis(store, DetectionConfig(), create_provider(a.llm), "auto", archive=_archive())
    _print_summary(incidents)
    print("\n3/3 Failure Time Machine and reports are ready.")
    store.close()
    if a.serve:
        cmd_serve(a)
    else:
        print(f"Run `ail serve --db {a.db}` to open the portal.")


def cmd_report(a: argparse.Namespace) -> None:
    inc = TelemetryStore(a.db).get_incident(a.incident)
    if inc is None:
        sys.exit(f"incident {a.incident} not found")
    print(render_markdown(inc))


def cmd_issue(a: argparse.Namespace) -> None:
    inc = TelemetryStore(a.db).get_incident(a.incident)
    if inc is None:
        sys.exit(f"incident {a.incident} not found")
    title, body = render_issue(inc)
    if not a.create:
        print(f"# {title}\n\n{body}\n\n(dry run: pass --create with --repo owner/name to open the issue)")
        return
    from ail_investigation.repos import GitHubRepo

    if not a.repo:
        sys.exit("--repo owner/name is required with --create")
    print(GitHubRepo(a.repo).create_issue(title, body, ["os-intelligence-lab"]))


def cmd_stream(a: argparse.Namespace) -> None:
    from ail_sdk import IngestionClient

    sc = load_scenario(a.scenario)
    sim = Simulator(sc, devices=a.devices)
    client = IngestionClient(a.url, token=a.token)
    try:
        print(client.send_deployments(c.model_dump(mode="json") for c in sim.change_events()))
        print(client.send_events(iter_events(sim, minutes=a.minutes)))
    except httpx.TransportError:
        sys.exit(f"cannot reach {a.url}; start the server first with `ail serve`")


def cmd_scenarios(_: argparse.Namespace) -> None:
    for sc in list_scenarios():
        print(f"{sc.id}  {sc.name}  (expected incidents: {sc.expected.incidents})")


def cmd_ask(a: argparse.Namespace) -> None:
    from ail_detection import Workspace
    from ail_detection.nlq import answer, parse, vocab_of

    store = TelemetryStore(a.db)
    ws = Workspace(store, DetectionConfig())
    res = answer(ws, parse(" ".join(a.question), vocab_of(ws)), store.list_incidents())
    q = res["query"]
    print(f"Understood: metric={q['metric']} include={q['include']} exclude={q['exclude']} "
          f"window={res['window'][0]}-{res['window'][1]} UTC")
    for note in q["notes"]:
        print(f"  note: {note}")
    print(res["answer"])
    for i in res["incidents"]:
        print(f"  {i['id']}  {i['title']} [{i['severity']}]")


def cmd_history(_: argparse.Namespace) -> None:
    past = _archive().all()
    if not past:
        print("The archive is empty. Run `ail analyze` or `ail demo` first.")
    for inc in past:
        print(f"{inc.id}  {inc.title}  [{inc.severity}]  onset {inc.onset:%Y-%m-%d %H:%M}  data {inc.reproducibility.get('data_fingerprint')}")


def cmd_agent(a: argparse.Namespace) -> None:
    import json

    from ail_agent import SIGNALS, build_payload, grant, load_consent, revoke
    from ail_agent.agent import run_loop
    from ail_agent.consent import config_dir

    if a.action == "signals":
        for name, text in SIGNALS.items():
            print(f"{name}: {text}")
        return
    if a.action == "revoke":
        print("consent revoked and local device id deleted" if revoke() else "nothing to revoke")
        return
    if a.action == "enroll":
        chosen = a.signals or list(SIGNALS)
        print("The agent will send ONLY these signals, to " + a.endpoint + ":")
        for name in chosen:
            print(f"  - {name}: {SIGNALS.get(name, '?')}")
        print("Plus coarse machine facts: OS, OS version, architecture, CPU core count, memory size.")
        print(f"It never collects names, hostnames, files, processes, network data or location. Consent is stored in {config_dir()}.")
        if not a.yes and input("Enable these signals? [y/N] ").strip().lower() != "y":
            sys.exit("not enrolled")
        grant(chosen, a.endpoint)
        print("enrolled. Run `ail agent preview` to see exactly what would be sent.")
        return
    consent = load_consent()
    if consent is None:
        sys.exit("not enrolled: run `ail agent enroll` first (the agent never runs without explicit consent)")
    if a.action == "preview":
        print(json.dumps(build_payload(consent), indent=2))
        return
    token = os.environ.get("AIL_API_TOKEN")
    try:
        run_loop(consent, a.interval, 1 if a.once else a.count, token)
    except httpx.TransportError:
        sys.exit(f"cannot reach {consent.endpoint}")


def cmd_evaluate(a: argparse.Namespace) -> None:
    from ail_cli.evaluate import evaluate

    devices = a.devices or (None if a.heldout else 50_000)
    report = evaluate(devices, a.seeds, heldout=a.heldout)
    print(report)
    if a.output:
        out = Path(a.output)
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(report)
        print(f"written to {out}")


def main(argv: list[str] | None = None) -> None:
    p = argparse.ArgumentParser(prog="ail", description="OS Intelligence Lab")
    sub = p.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("simulate", help="generate synthetic telemetry into a database")
    s.add_argument("--scenario", default=FLAGSHIP)
    s.add_argument("--devices", type=int)
    s.add_argument("--seed", type=int)
    s.add_argument("--db", default=DEFAULT_DB)
    s.add_argument("--force", action="store_true")
    s.set_defaults(fn=cmd_simulate)

    s = sub.add_parser("analyze", help="detect, correlate and investigate")
    s.add_argument("--db", default=DEFAULT_DB)
    s.add_argument("--llm", default=None, help="rules (default) | openai | azure | local")
    s.add_argument("--repo-mode", default="auto", choices=["auto", "fixture", "github", "off"])
    s.add_argument("--no-investigate", action="store_true")
    s.set_defaults(fn=cmd_analyze)

    for name, fn, helptext in (("serve", cmd_serve, "start the API and portal"),
                               ("demo", cmd_demo, "run the flagship ARM64 memory regression end to end")):
        s = sub.add_parser(name, help=helptext)
        s.add_argument("--db", default="data/demo.db" if name == "demo" else DEFAULT_DB)
        s.add_argument("--host", default="127.0.0.1")
        s.add_argument("--port", type=int, default=8000)
        if name == "demo":
            s.add_argument("--scenario", default=FLAGSHIP)
            s.add_argument("--devices", type=int)
            s.add_argument("--llm", default=None)
            s.add_argument("--serve", action="store_true")
        s.set_defaults(fn=fn)

    s = sub.add_parser("report", help="print an incident report")
    s.add_argument("incident")
    s.add_argument("--db", default=DEFAULT_DB)
    s.set_defaults(fn=cmd_report)

    s = sub.add_parser("issue", help="draft (or open) a GitHub issue for an incident")
    s.add_argument("incident")
    s.add_argument("--db", default=DEFAULT_DB)
    s.add_argument("--repo")
    s.add_argument("--create", action="store_true")
    s.set_defaults(fn=cmd_issue)

    s = sub.add_parser("stream", help="send simulated events to a running API through the SDK")
    s.add_argument("--scenario", default=FLAGSHIP)
    s.add_argument("--devices", type=int, default=2000)
    s.add_argument("--minutes", type=int, default=30)
    s.add_argument("--url", default="http://127.0.0.1:8000")
    s.add_argument("--token")
    s.set_defaults(fn=cmd_stream)

    s = sub.add_parser("agent", help="opt-in local telemetry agent (enroll, preview, run, revoke)")
    s.add_argument("action", choices=["signals", "enroll", "preview", "run", "revoke"])
    s.add_argument("--signals", nargs="+")
    s.add_argument("--endpoint", default="http://127.0.0.1:8000")
    s.add_argument("--yes", action="store_true", help="skip the confirmation prompt")
    s.add_argument("--once", action="store_true")
    s.add_argument("--count", type=int)
    s.add_argument("--interval", type=float, default=60.0)
    s.set_defaults(fn=cmd_agent)

    s = sub.add_parser("ask", help="ask a plain-English question about the telemetry")
    s.add_argument("question", nargs="+")
    s.add_argument("--db", default=DEFAULT_DB)
    s.set_defaults(fn=cmd_ask)

    s = sub.add_parser("history", help="list incidents remembered from earlier analyses")
    s.set_defaults(fn=cmd_history)

    s = sub.add_parser("scenarios", help="list built-in scenarios")
    s.set_defaults(fn=cmd_scenarios)

    s = sub.add_parser("evaluate", help="score detection quality against scenario ground truth")
    s.add_argument("--devices", type=int, help="fleet size (default 50,000; with --heldout each scenario's own)")
    s.add_argument("--seeds", type=int, default=3)
    s.add_argument("--heldout", action="store_true", help="score the held-out scenarios instead of the tuned ten")
    s.add_argument("--output")
    s.set_defaults(fn=cmd_evaluate)

    args = p.parse_args(argv)
    args.fn(args)


if __name__ == "__main__":
    main()
