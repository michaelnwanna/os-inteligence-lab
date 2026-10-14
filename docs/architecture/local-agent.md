# Local telemetry agent

`ail agent` sends telemetry from a real machine into the same backend schema the simulator uses. It is opt-in, transparent and revocable.

```bash
ail agent signals                         # what could ever be collected
ail agent enroll --endpoint http://127.0.0.1:8000   # lists exactly what will be sent, asks y/N
ail agent preview                         # prints the exact JSON that run would send
ail agent run --interval 60               # send every minute (--once, --count N)
ail agent revoke                          # delete consent and the local device id
```

Without `consent.json` (default `~/.ail-agent/`, override with `AIL_AGENT_HOME`) the agent refuses to run.

## Signals (fixed allowlist)

| Signal | Event | Meaning |
|---|---|---|
| `session_heartbeat` | `session` | One event per interval so failure-rate denominators exist. No content. |
| `cpu_load_pct` | `metric` | System CPU load 0-100. |
| `memory_mb` | `metric` | System memory in use. Shares its name with the simulator's `memory_mb`, so it lands in the same cohort cube. |

Plus coarse machine facts on each event: OS name and version, architecture, core count, installed memory (rounded to GB), and a generic `device_model` of `generic-<arch>`.

## Never collected
Names, hostnames, serial numbers, file paths or contents, process lists, network addresses or traffic, location, keystrokes, browsing, installed software. The device id is a random 128-bit value generated locally and stored only in the consent file; deleting consent deletes it.

## Per-OS collection

| OS | Memory | CPU load |
|---|---|---|
| Linux | `/proc/meminfo` (`MemTotal - MemAvailable`) | load average / cores |
| macOS | `vm_stat` (active + wired + compressor pages) and `sysctl hw.memsize` | load average / cores |
| Windows | `GlobalMemoryStatusEx` | `GetSystemTimes` delta over 0.5 s |

Status: the real collectors run on Linux, macOS and Windows in CI. On commit `c73d427` the `agent` job passed on GitHub-hosted Ubuntu, macOS and Windows runners: the live-collector tests passed on each, `ail agent preview` printed a valid payload, and an end-to-end test sent each OS's real heartbeat, CPU and memory events through the ingestion pipeline into the same schema. Two caveats: the ingestion in that test is in-process (the agent and server run in one process, not across a network), and the runners are virtual machines, not personal hardware. macOS was also run by hand on a real laptop; Linux and Windows have not been run on physical machines.

## Sending to a protected backend
Set `AIL_API_TOKEN` in the agent's environment; it is sent as a bearer token and never written to disk by the agent.
