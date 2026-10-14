"use strict";
/* OS Intelligence Lab portal. Dynamic text is always inserted as text nodes, never as HTML. */

const app = document.getElementById("app");
const COLORS = { observed: "#f85149", expected: "#8b98a9", global: "#4aa3ff", exposure: "#a371f7", secondary: "#d29922" };
const METRICS = { failure_rate: "Failure rate", latency_ms: "Latency", memory_mb: "Memory usage" };

const el = (tag, props = {}, ...kids) => {
  const n = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (k === "class") n.className = v;
    else if (k.startsWith("on")) n.addEventListener(k.slice(2), v);
    else if (v !== null && v !== undefined && v !== false) n.setAttribute(k, v === true ? "" : v);
  }
  for (const kid of kids.flat(Infinity)) if (kid !== null && kid !== undefined && kid !== false) n.append(kid.nodeType ? kid : document.createTextNode(String(kid)));
  return n;
};
const api = async (path) => {
  const r = await fetch(path);
  if (!r.ok) throw new Error(`${r.status}: ${(await r.json().catch(() => ({}))).detail || r.statusText}`);
  return r.json();
};
const fmtVal = (metric, v) => (v === null || v === undefined ? "-" : metric === "failure_rate" ? (v * 100).toFixed(2) + "%" : metric === "latency_ms" ? Math.round(v) + " ms" : metric === "memory_mb" ? Math.round(v) + " MB" : String(v));
const pill = (cls, text) => el("span", { class: `pill ${cls}` }, text);
const sev = (s) => pill(`sev-${s}`, s);
const hhmm = (iso) => (iso ? new Date(iso).toISOString().slice(11, 16) : "-");
const idxOf = (iso, t0) => Math.round((Date.parse(iso) - Date.parse(t0)) / 60000);
const label = (dims) => (Object.keys(dims || {}).length ? Object.entries(dims).map(([k, v]) => ({ architecture: v.toUpperCase(), application_version: "App " + v, driver_version: "Driver " + v, os_version: "OS " + v })[k] || v).join(" + ") : "all devices");
const kpi = (value, text, cls = "") => el("div", { class: `card kpi ${cls}` }, el("div", { class: "v" }, value), el("div", { class: "l" }, text));
const metricTabs = (current, onPick) => el("div", { class: "filters" }, Object.entries(METRICS).map(([k, v]) => el("button", { class: k === current ? "primary" : "", onclick: () => onPick(k) }, v)));
const page = (title, sub) => { app.replaceChildren(el("h1", {}, title), el("div", { class: "sub" }, sub || "")); };
const show = (...nodes) => app.append(...nodes);
const incidentLink = (id, text) => el("a", { href: `#/incidents/${encodeURIComponent(id)}` }, text || id);

/* ---------- Command Center ---------- */
async function renderHome() {
  page("Command Center", "System health across the monitored population.");
  const [ov, incs] = await Promise.all([api("/api/v1/overview"), api("/api/v1/incidents")]);
  const h = ov.health || {}, i = ov.incidents, sim = ov.meta.simulation;
  show(
    el("div", { class: "grid g5" },
      kpi((h.healthy_series ?? 0).toLocaleString(), "Healthy metrics", "ok"),
      kpi(i.active, "Active anomalies", i.active ? "warn" : "ok"),
      kpi(i.critical, "Critical", i.critical ? "crit" : ""),
      kpi(i.investigating, "Investigating"),
      kpi(i.resolved, "Resolved")),
    el("div", { class: "grid g4", style: "margin-top:12px" },
      kpi(ov.failure_rate ? fmtVal("failure_rate", ov.failure_rate.current) : "-", "Failure rate (last 10 min)"),
      kpi(ov.failure_rate ? fmtVal("failure_rate", ov.failure_rate.baseline) : "-", "Failure rate baseline"),
      kpi(sim ? sim.devices.toLocaleString() : "-", "Devices"),
      kpi((ov.meta.counts.aggregate_rows || 0).toLocaleString(), "Aggregate rows")));
  const chartBox = el("div", { class: "card", style: "margin-top:12px" });
  show(askBox(), el("h2", {}, "Population health"), chartBox);
  const draw = async (metric) => {
    const s = await api(`/api/v1/series?metric=${metric}`);
    chartBox.replaceChildren(metricTabs(metric, draw), lineChart({
      labels: s.minutes, fmt: (v) => fmtVal(metric, v), shadeTo: s.baseline_end,
      series: [{ name: "Observed", values: s.observed, color: COLORS.global }, { name: "Expected", values: s.expected, color: COLORS.expected, dashed: true }],
      markers: incs.map((x) => ({ index: idxOf(x.onset, s.t0), label: `${x.id} onset`, color: "#f85149" })),
    }));
  };
  await draw("failure_rate");
  show(el("h2", {}, "Incidents"), incidentTable(incs));
}

function askBox() {
  const out = el("div", { class: "muted", style: "margin-top:8px" }, "Try: Show regressions in the last 7 days affecting ARM64 but not x64");
  const input = el("input", { type: "text", style: "flex:1;min-width:240px", placeholder: "Ask about the telemetry…", "aria-label": "Ask a question" });
  const ask = async () => {
    if (input.value.trim().length < 3) return;
    try {
      const r = await api(`/api/v1/query?q=${encodeURIComponent(input.value)}`);
      const q = r.query;
      out.replaceChildren(el("div", {}, el("b", {}, r.answer)),
        el("div", { class: "muted" }, `Understood as: ${METRICS[q.metric] || q.metric}, cohort ${label(q.include)}${q.exclude.length ? ", excluding " + q.exclude.map(label).join(", ") : ""}, window ${r.window[0]}\u2013${r.window[1]} UTC${r.window_clipped_to_available_data ? " (all available data)" : ""}.`),
        q.notes.map((n) => el("div", { class: "muted" }, "Note: " + n)),
        r.incidents.map((i) => el("div", {}, incidentLink(i.id), ` ${i.title} `, sev(i.severity))));
    } catch (e) { out.replaceChildren(el("span", { class: "err" }, String(e.message))); }
  };
  input.addEventListener("keydown", (e) => { if (e.key === "Enter") ask(); });
  return el("div", { class: "card", style: "margin-top:12px" }, el("div", { class: "filters", style: "margin:0" }, input, el("button", { class: "primary", onclick: ask }, "Ask")), out);
}

function incidentTable(incs) {
  if (!incs.length) return el("div", { class: "card muted" }, "No incidents. Run `ail demo` to generate one.");
  return el("div", { class: "card" }, el("table", {},
    el("thead", {}, el("tr", {}, ["Incident", "Severity", "Status", "Cohort", "Metric", "Change", "Likely trigger", "Detected"].map((t) => el("th", {}, t)))),
    el("tbody", {}, incs.map((x) => el("tr", { class: "click", onclick: () => (location.hash = `#/incidents/${encodeURIComponent(x.id)}`) },
      el("td", {}, el("b", {}, x.id), " ", x.title), el("td", {}, sev(x.severity)), el("td", {}, pill(`st-${x.status}`, x.status)),
      el("td", {}, x.cohort), el("td", {}, METRICS[x.metric] || x.metric), el("td", {}, `${fmtVal(x.metric, x.baseline)} \u2192 ${fmtVal(x.metric, x.observed)}`),
      el("td", {}, x.trigger ? `${x.trigger} (${Math.round(x.correlation * 100)}%)` : "-"), el("td", {}, hhmm(x.first_detection) + " UTC"))))));
}

/* ---------- Anomaly Explorer ---------- */
const anomalyFilters = {};
async function renderAnomalies() {
  page("Anomaly Explorer", "Every detector result, filterable by metric, severity, detector and cohort.");
  const co = await api("/api/v1/cohorts?metric=failure_rate");
  const dimValues = Object.fromEntries(co.dimensions.map((d) => [d.dimension, d.values.map((v) => v.value)]));
  const select = (key, options) => el("select", { onchange: (e) => { anomalyFilters[key] = e.target.value; load(); } },
    el("option", { value: "" }, `${key.replace("_", " ")}: any`), options.map((o) => el("option", { value: o, selected: anomalyFilters[key] === o }, o)));
  const filters = el("div", { class: "filters" },
    select("metric", Object.keys(METRICS)), select("severity", ["critical", "high", "moderate", "low"]),
    select("detector", ["ZScoreDetector", "EwmaCusumDetector", "ChangePointDetector", "DistributionShiftDetector", "MLDetector", "CohortScanDetector"]),
    ["architecture", "os_version", "application_version", "device_model", "driver_version"].filter((d) => dimValues[d]).map((d) => select(d, dimValues[d])));
  const out = el("div", { class: "card" });
  show(filters, out);
  async function load() {
    const q = new URLSearchParams(Object.entries(anomalyFilters).filter(([, v]) => v)).toString();
    const rows = await api("/api/v1/anomalies" + (q ? "?" + q : ""));
    out.replaceChildren(rows.length ? el("table", {}, el("thead", {}, el("tr", {}, ["Detected", "Incident", "Metric", "Scope", "Detector", "Severity", "Confidence", "Baseline \u2192 observed", "Population"].map((t) => el("th", {}, t)))),
      el("tbody", {}, rows.map((r) => [el("tr", {}, el("td", {}, hhmm(r.timestamp)), el("td", {}, incidentLink(r.incident_id)), el("td", {}, METRICS[r.metric] || r.metric), el("td", {}, r.scope), el("td", {}, r.detector), el("td", {}, sev(r.severity)),
        el("td", {}, Math.round(r.confidence * 100) + "%"), el("td", {}, `${fmtVal(r.metric, r.baseline)} \u2192 ${fmtVal(r.metric, r.observed_value)}`), el("td", {}, (r.affected_population * 100).toFixed(1) + "%")),
        el("tr", {}, el("td", { colspan: 9 }, el("details", {}, el("summary", {}, "Evidence"), el("pre", {}, JSON.stringify(r.evidence, null, 2)))))]))) : el("div", { class: "muted" }, "No anomalies match these filters."));
  }
  await load();
}

/* ---------- Cohort Explorer ---------- */
const cohortState = { metric: "failure_rate", filters: {} };
async function renderCohorts() {
  page("Cohort Explorer", "Drill down by dimension: click a value to add it to the cohort.");
  const f = cohortState.filters;
  const q = Object.entries(f).map(([k, v]) => `${k}=${v}`).join(",");
  const [data, series] = await Promise.all([
    api(`/api/v1/cohorts?metric=${cohortState.metric}&filters=${encodeURIComponent(q)}`),
    api(`/api/v1/series?metric=${cohortState.metric}&filters=${encodeURIComponent(q)}`)]);
  const m = cohortState.metric;
  const crumbs = el("div", { class: "crumbs" }, el("span", { class: "muted" }, "Cohort:"), el("span", { class: "crumb", onclick: () => { cohortState.filters = {}; route(); } }, "all devices"),
    Object.entries(f).map(([k, v]) => el("span", { class: "crumb", title: "remove filter", onclick: () => { delete cohortState.filters[k]; route(); } }, `${k.replace("_", " ")}: ${v} \u00d7`)));
  show(metricTabs(m, (k) => { cohortState.metric = k; route(); }), crumbs,
    el("div", { class: "card" }, el("div", { class: "muted" }, `Selection: ${fmtVal(m, data.selection.baseline)} expected \u2192 ${fmtVal(m, data.selection.observed)} observed (${data.selection.ratio.toFixed(2)}x) over ${hhmm(data.window[0])}\u2013${hhmm(data.window[1])} UTC`),
      lineChart({ labels: series.minutes, fmt: (v) => fmtVal(m, v), shadeTo: series.baseline_end, series: [{ name: "Observed", values: series.observed, color: COLORS.observed }, { name: "Expected", values: series.expected, color: COLORS.expected, dashed: true }] })));
  const grid = el("div", { class: "grid g2", style: "margin-top:12px" });
  for (const d of data.dimensions) {
    grid.append(el("div", { class: "card" }, el("h2", { style: "margin-top:0" }, d.dimension.replace("_", " ")), el("table", {},
      el("thead", {}, el("tr", {}, ["Value", "Share", "Observed", "Expected", "Ratio"].map((t) => el("th", {}, t)))),
      el("tbody", {}, d.values.map((v) => el("tr", { class: "click", onclick: () => { cohortState.filters[d.dimension] = v.value; route(); } },
        el("td", {}, v.value), el("td", {}, el("div", { class: "bar" }, el("i", { style: `width:${Math.min(100, v.share * 100)}%` }))), el("td", {}, fmtVal(m, v.observed)), el("td", {}, fmtVal(m, v.baseline)),
        el("td", {}, el("span", { class: v.llr > 12 && v.ratio > 1.2 ? "err" : "" }, v.ratio.toFixed(2) + "x"))))))));
  }
  show(grid);
}

/* ---------- Investigations ---------- */
async function renderIncidents() {
  page("Investigations", "All incidents, newest first.");
  show(incidentTable(await api("/api/v1/incidents")));
}

async function renderIncident(id) {
  const inc = await api(`/api/v1/incidents/${encodeURIComponent(id)}`);
  const inv = inc.investigation;
  page(`Incident ${inc.id}`, inc.title);
  const top = inc.correlations[0];
  show(
    el("div", { class: "grid g5" },
      kpi(inc.severity, "Severity", inc.severity === "critical" ? "crit" : "warn"),
      kpi(inc.status, "Status"),
      kpi(`${fmtVal(inc.metric, inc.baseline)} \u2192 ${fmtVal(inc.metric, inc.observed)}`, METRICS[inc.metric] || inc.metric),
      kpi(hhmm(inc.first_detection) + " UTC", "First detection"),
      kpi(top && !top.after_onset ? Math.round(top.score * 100) + "%" : "-", top && !top.after_onset ? `Correlation: ${top.change.component} ${top.change.to_version || ""}` : "Correlation")),
    el("div", { class: "filters", style: "margin-top:12px" },
      el("a", { href: `#/timemachine/${encodeURIComponent(inc.id)}` }, el("button", { class: "primary" }, "Open Failure Time Machine")),
      el("a", { href: `/api/v1/incidents/${encodeURIComponent(inc.id)}/report`, target: "_blank", rel: "noopener" }, el("button", {}, "Markdown report"))));

  if (inv) {
    const factEls = {}, hypEls = {};
    const refs = (list) => el("div", { class: "refs" }, list.map((r) => el("a", { onclick: () => { Object.values(factEls).forEach((n) => n.classList.remove("hl")); factEls[r]?.classList.add("hl"); factEls[r]?.scrollIntoView({ block: "center", behavior: "smooth" }); } }, r)));
    show(el("h2", {}, "AI assessment"), el("div", { class: "card" }, el("div", { class: "tag" }, `Generated by ${inv.provider} \u00b7 grounded in the evidence below \u00b7 not verified`), el("p", {}, inv.summary),
      inv.hypotheses.map((h) => el("div", { class: "hyp" }, el("div", { class: "tag" }, "Hypothesis"), el("div", { class: "t" }, h.title), el("div", {}, h.rationale), el("div", { style: "margin-top:6px" }, pill(`conf-${h.confidence}`, `${h.confidence} confidence`), " ", el("span", { class: "muted" }, "evidence: "), refs(h.supporting_evidence)))),
      el("h2", {}, "Recommended investigation"), el("ol", {}, inv.recommended_steps.map((s) => el("li", {}, s.step, " ", refs(s.evidence))))));
    show(el("h2", {}, "Observed facts (deterministic)"), el("div", { class: "card" }, inv.observed_facts.map((f) => { const n = el("div", { class: "fact" }, el("b", {}, f.id), f.statement); factEls[f.id] = n; return n; })));
  }

  const s = await api(`/api/v1/series?metric=${inc.metric}&filters=${encodeURIComponent(Object.entries(inc.cohorts[0]?.dimensions || {}).map(([k, v]) => `${k}=${v}`).join(","))}`);
  const gl = await api(`/api/v1/series?metric=${inc.metric}`);
  show(el("h2", {}, `${METRICS[inc.metric] || inc.metric}: ${inc.cohort_label}`), el("div", { class: "card" }, lineChart({
    labels: s.minutes, fmt: (v) => fmtVal(inc.metric, v), shadeTo: s.baseline_end,
    series: [{ name: `Observed (${inc.cohort_label})`, values: s.observed, color: COLORS.observed }, { name: "Expected", values: s.expected, color: COLORS.expected, dashed: true }, { name: "All devices", values: gl.observed, color: COLORS.global }],
    markers: inc.timeline.map((e) => ({ index: idxOf(e.ts, s.t0), label: `${hhmm(e.ts)} ${e.title}`, color: "#a371f7" })),
  })));

  if (inc.cohorts.length) show(el("h2", {}, "Affected cohorts (scan statistic)"), el("div", { class: "card" }, el("table", {},
    el("thead", {}, el("tr", {}, ["#", "Cohort", "Baseline \u2192 observed", "Ratio", "Severity", "Share of sessions", "Share of excess"].map((t) => el("th", {}, t)))),
    el("tbody", {}, inc.cohorts.map((c) => el("tr", {}, el("td", {}, c.rank), el("td", {}, label(c.dimensions)), el("td", {}, `${fmtVal(c.metric, c.baseline)} \u2192 ${fmtVal(c.metric, c.observed)}`), el("td", {}, c.ratio.toFixed(1) + "x"), el("td", {}, sev(c.severity)),
      el("td", {}, (c.population_share * 100).toFixed(1) + "%"), el("td", {}, el("div", { class: "bar" }, el("i", { style: `width:${c.excess_share * 100}%` })))))))));
  show(el("h2", {}, "Correlated changes (evidence, not causation)"), el("div", { class: "card" }, el("table", {},
    el("thead", {}, el("tr", {}, ["Change", "Started", "Offset vs onset", "Population overlap", "Score"].map((t) => el("th", {}, t)))),
    el("tbody", {}, inc.correlations.map((c) => el("tr", {}, el("td", {}, `${c.change.component} ${c.change.from_version || ""}\u2192${c.change.to_version || ""} (${c.change.kind.replace("_", " ")})`), el("td", {}, hhmm(c.change.timestamp)),
      el("td", {}, c.after_onset ? "after onset" : `${Math.max(0, Math.round(c.delta_minutes))} min before`), el("td", {}, Math.round(c.overlap * 100) + "%"),
      el("td", {}, el("div", { class: "bar" }, el("i", { style: `width:${c.score * 100}%` })), Math.round(c.score * 100) + "%")))))));
  if (inc.signatures.length) show(el("h2", {}, "Failure signatures"), el("div", { class: "card" }, el("table", {},
    el("thead", {}, el("tr", {}, ["Signature", "Share of excess", "Share of failures", "Baseline share"].map((t) => el("th", {}, t)))),
    el("tbody", {}, inc.signatures.map((g) => el("tr", {}, el("td", { class: "mono" }, g.signature), el("td", {}, Math.round(g.excess_share * 100) + "%"), el("td", {}, Math.round(g.share_of_failures * 100) + "%"), el("td", {}, Math.round(g.baseline_share * 100) + "%")))))));
  if (inv?.candidate_commits.length) show(el("h2", {}, "Candidate commits (heuristic code review)"), el("div", { class: "card" }, inv.candidate_commits.map((c) => el("div", { class: "fact" },
    el("b", { class: "mono" }, c.sha.slice(0, 7)), `#${c.rank} ${c.message} `, el("span", { class: "muted" }, `(${c.component || "n/a"}, score ${c.score.toFixed(2)})`),
    c.reasons.length ? el("ul", { class: "muted" }, c.reasons.map((r) => el("li", {}, r))) : null, c.snippets.length ? el("pre", { class: "mono" }, c.snippets.join("\n")) : null))));
  if (inc.similar_incidents.length) show(el("h2", {}, "Similar earlier incidents"), el("div", { class: "card" }, inc.similar_incidents.map((s) => el("div", { class: "fact" },
    el("b", {}, s.id), s.title, " ", s.recurring ? pill("sev-high", "possible recurrence") : null,
    el("div", { class: "muted" }, `similarity ${s.score.toFixed(2)} (metric ${s.components.metric}, cohort ${s.components.cohort.toFixed(2)}, signature ${s.components.signature.toFixed(2)}, trigger ${s.components.trigger})`)))));
  show(el("h2", {}, "Investigation timeline"), el("div", { class: "card" }, el("ul", { class: "events" }, inc.timeline.map((e) => el("li", { class: "seen" }, el("span", { class: "time" }, hhmm(e.ts)), el("b", {}, e.title), e.simulated_latency ? el("span", { class: "sim" }, "simulated pipeline clock") : null, e.detail ? el("div", { class: "muted" }, e.detail) : null)))));
  show(el("details", { style: "margin-top:16px" }, el("summary", {}, "Reproducibility"), el("pre", {}, JSON.stringify({ config_hash: inc.reproducibility.config_hash, data_fingerprint: inc.reproducibility.data_fingerprint, engine: inc.reproducibility.engine_version, detectors: inc.results.map((r) => `${r.detector}/${r.metric}`) }, null, 2))));
}

/* ---------- Failure Time Machine ---------- */
let tmTimer = null;
async function renderTimeMachine(id) {
  if (!id) {
    const incs = await api("/api/v1/incidents");
    if (!incs.length) { page("Failure Time Machine", ""); show(el("div", { class: "card muted" }, "No incidents to replay. Run `ail demo`.")); return; }
    location.hash = `#/timemachine/${encodeURIComponent(incs[0].id)}`; return;
  }
  const tm = await api(`/api/v1/incidents/${encodeURIComponent(id)}/timemachine`);
  page("Failure Time Machine", `Replay of ${id}: ${tm.scope}. Facts and AI steps appear as they became available.`);
  const n = tm.minutes.length, evs = tm.events;
  let idx = Math.max(0, (evs[0]?.index ?? 0) - 8), speed = 1;
  const chartBox = el("div", { class: "card" }), exposureBox = el("div", { class: "card", style: "margin-top:12px" });
  const status = el("div", { class: "grid g4", style: "margin-bottom:12px" });
  const scrub = el("input", { type: "range", min: 0, max: n - 1, value: idx, oninput: (e) => { idx = +e.target.value; draw(); } });
  const playBtn = el("button", { class: "primary", onclick: () => toggle() }, "Play");
  const list = el("ul", { class: "events" }), inspect = el("div", { class: "card" });
  const upTo = (arr) => arr.map((v, i) => (i <= idx ? v : null));
  const upToCohort = (arr) => arr.map((v, i) => (i <= idx && tm.frames.cohort_sessions[i] > 0 ? v : null));
  const yMax = Math.max(...tm.frames.cohort, ...tm.frames.global) * 1.12;
  const stop = () => { clearInterval(tmTimer); tmTimer = null; playBtn.textContent = "Play"; };
  function toggle() { if (tmTimer) return stop(); if (idx >= n - 1) idx = 0; playBtn.textContent = "Pause"; tmTimer = setInterval(() => { idx = Math.min(n - 1, idx + speed); draw(); if (idx >= n - 1) stop(); }, 200); }
  function draw() {
    scrub.value = idx;
    const seen = evs.filter((e) => e.index <= idx), current = seen[seen.length - 1];
    chartBox.replaceChildren(lineChart({ labels: tm.minutes, fmt: (v) => fmtVal(tm.metric, v), cursor: idx, height: 260, yMax,
      series: [{ name: `${tm.label} (${tm.scope})`, values: upToCohort(tm.frames.cohort), color: COLORS.observed }, { name: "Expected", values: upToCohort(tm.frames.expected), color: COLORS.expected, dashed: true }, { name: "All devices", values: upTo(tm.frames.global), color: COLORS.global }],
      markers: seen.map((e) => ({ index: e.index, label: `${hhmm(e.ts)} ${e.title}` })) }));
    if (tm.frames.exposure) exposureBox.replaceChildren(el("div", { class: "muted" }, "Share of sessions on the changed version"), lineChart({ labels: tm.minutes, fmt: (v) => (v * 100).toFixed(0) + "%", cursor: idx, height: 130, yMax: Math.max(...tm.frames.exposure, 0.01) * 1.12, series: [{ name: "Exposure", values: upTo(tm.frames.exposure), color: COLORS.exposure }] }));
    status.replaceChildren(kpi(tm.minutes[idx] + " UTC", "Replay clock"), kpi(fmtVal(tm.metric, tm.frames.cohort[idx]), `${tm.scope}`, "crit"), kpi(fmtVal(tm.metric, tm.frames.global[idx]), "All devices"),
      kpi(tm.frames.exposure ? (tm.frames.exposure[idx] * 100).toFixed(1) + "%" : "-", "Exposure"));
    list.replaceChildren(...evs.map((e) => el("li", { class: `${e.index <= idx ? "seen" : ""} ${e === current ? "current" : ""}`, onclick: () => { stop(); idx = e.index; draw(); } },
      el("span", { class: "time" }, hhmm(e.ts)), el("b", {}, e.title), e.simulated_latency ? el("span", { class: "sim" }, "simulated pipeline clock") : null)));
    inspect.replaceChildren(el("h2", { style: "margin-top:0" }, "Evidence at this stage"), current ? el("div", {}, el("b", {}, `${hhmm(current.ts)} ${current.title}`), el("div", { class: "muted" }, current.detail),
      el("dl", {}, Object.entries(current.evidence).flatMap(([k, v]) => [el("dt", {}, k), el("dd", {}, typeof v === "object" ? JSON.stringify(v) : String(v))]))) : el("div", { class: "muted" }, "Nothing has happened yet."));
  }
  const btn = (text, fn) => el("button", { onclick: () => { stop(); fn(); draw(); } }, text);
  show(status, chartBox, el("div", { class: "tm-controls" }, btn("\u23ee Start", () => (idx = 0)), btn("\u25c0 Step", () => (idx = Math.max(0, idx - 1))), playBtn, btn("Step \u25b6", () => (idx = Math.min(n - 1, idx + 1))), btn("End \u23ed", () => (idx = n - 1)), scrub,
    el("select", { onchange: (e) => (speed = +e.target.value) }, [1, 2, 4, 8].map((s) => el("option", { value: s }, `${s}x`)))), exposureBox,
    el("div", { class: "grid g2", style: "margin-top:12px" }, el("div", { class: "card" }, el("h2", { style: "margin-top:0" }, "Incident timeline"), list), inspect), el("div", { style: "margin-top:8px" }, incidentLink(id, `Open investigation ${id}`)));
  draw();
}

/* ---------- Router ---------- */
async function route() {
  clearInterval(tmTimer); tmTimer = null;
  const parts = location.hash.replace(/^#\/?/, "").split("/").filter(Boolean).map(decodeURIComponent);
  document.querySelectorAll("#nav a").forEach((a) => a.classList.toggle("active", a.dataset.route === (parts[0] || "")));
  try {
    if (!parts.length) await renderHome();
    else if (parts[0] === "anomalies") await renderAnomalies();
    else if (parts[0] === "cohorts") await renderCohorts();
    else if (parts[0] === "incidents") await (parts[1] ? renderIncident(parts[1]) : renderIncidents());
    else if (parts[0] === "timemachine") await renderTimeMachine(parts[1]);
    else app.replaceChildren(el("h1", {}, "Not found"));
  } catch (err) {
    app.replaceChildren(el("h1", {}, "Something went wrong"), el("div", { class: "card err" }, String(err.message || err)), el("p", { class: "muted" }, "If no data has been loaded yet, run `ail demo` first."));
  }
}
window.addEventListener("hashchange", route);
api("/api/v1/meta").then((m) => { document.getElementById("nav-foot").textContent = m.simulation ? `${m.simulation.scenario} \u00b7 ${m.simulation.devices.toLocaleString()} devices` : "no data loaded"; }).catch(() => {});
route();
