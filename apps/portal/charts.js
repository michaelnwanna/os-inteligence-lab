/* Dependency-free SVG line chart. All text is inserted with textContent. */
(function () {
  const NS = "http://www.w3.org/2000/svg";
  const s = (tag, attrs = {}, text) => {
    const n = document.createElementNS(NS, tag);
    for (const [k, v] of Object.entries(attrs)) n.setAttribute(k, v);
    if (text !== undefined) n.textContent = text;
    return n;
  };

  /**
   * opts: labels[], series[{name, values[] (null = gap), color, dashed}], markers[{index, label, color}],
   * fmt(v), height, shadeTo (index), cursor (index), onHover(index)
   */
  window.lineChart = function (opts) {
    const W = 820, H = opts.height || 230, ml = 58, mr = 14, mt = 14, mb = 26;
    const n = opts.labels.length;
    const fmt = opts.fmt || ((v) => String(v));
    const all = opts.series.flatMap((x) => x.values).filter((v) => v !== null && Number.isFinite(v));
    const yMax = opts.yMax || (all.length ? Math.max(...all) : 1) * 1.12 || 1;
    const x = (i) => ml + (n <= 1 ? 0 : (i * (W - ml - mr)) / (n - 1));
    const y = (v) => mt + (1 - v / yMax) * (H - mt - mb);
    const svg = s("svg", { viewBox: `0 0 ${W} ${H}`, class: "chart", width: "100%", role: "img" });

    if (opts.shadeTo != null) svg.appendChild(s("rect", { x: ml, y: mt, width: x(opts.shadeTo) - ml, height: H - mt - mb, fill: "#4aa3ff14" }));
    for (let g = 0; g <= 4; g++) {
      const v = (yMax * g) / 4;
      svg.appendChild(s("line", { x1: ml, x2: W - mr, y1: y(v), y2: y(v), class: "grid" }));
      svg.appendChild(s("text", { x: ml - 6, y: y(v) + 4, "text-anchor": "end" }, fmt(v)));
    }
    const step = Math.max(1, Math.round(n / 8));
    for (let i = 0; i < n; i += step) svg.appendChild(s("text", { x: x(i), y: H - 8, "text-anchor": "middle" }, opts.labels[i]));

    for (const ser of opts.series) {
      let d = "", pen = false;
      ser.values.forEach((v, i) => {
        if (v === null || !Number.isFinite(v)) { pen = false; return; }
        d += `${pen ? "L" : "M"}${x(i).toFixed(1)},${y(v).toFixed(1)} `;
        pen = true;
      });
      svg.appendChild(s("path", { d, fill: "none", stroke: ser.color, "stroke-width": 2, "stroke-dasharray": ser.dashed ? "5 4" : "none" }));
    }
    (opts.markers || []).forEach((m, k) => {
      const g = s("g");
      g.appendChild(s("line", { x1: x(m.index), x2: x(m.index), y1: mt, y2: H - mb, stroke: m.color || "#a371f7", "stroke-dasharray": "2 3", "stroke-width": 1 }));
      g.appendChild(s("circle", { cx: x(m.index), cy: mt + 6 + (k % 3) * 12, r: 4, fill: m.color || "#a371f7" }));
      g.appendChild(s("title", {}, m.label));
      svg.appendChild(g);
    });
    if (opts.cursor != null) svg.appendChild(s("line", { x1: x(opts.cursor), x2: x(opts.cursor), y1: mt, y2: H - mb, stroke: "#e6edf3", "stroke-width": 1.5 }));

    const readout = s("text", { x: W - mr, y: mt + 2, "text-anchor": "end" }, "");
    svg.appendChild(readout);
    const hit = s("rect", { x: ml, y: mt, width: W - ml - mr, height: H - mt - mb, fill: "transparent" });
    hit.addEventListener("mousemove", (ev) => {
      const box = svg.getBoundingClientRect();
      const px = ((ev.clientX - box.left) / box.width) * W;
      const i = Math.max(0, Math.min(n - 1, Math.round(((px - ml) / (W - ml - mr)) * (n - 1))));
      readout.textContent = `${opts.labels[i]}  ` + opts.series.map((q) => (q.values[i] == null ? "" : `${q.name}: ${fmt(q.values[i])}`)).filter(Boolean).join("   ");
      if (opts.onHover) opts.onHover(i);
    });
    hit.addEventListener("mouseleave", () => { readout.textContent = ""; });
    svg.appendChild(hit);

    const legend = document.createElement("div");
    legend.className = "muted";
    legend.style.fontSize = "12px";
    opts.series.forEach((q) => {
      const sw = document.createElement("span");
      sw.textContent = "\u25A0 ";
      sw.style.color = q.color;
      legend.append(sw, q.name + "   ");
    });
    const wrap = document.createElement("div");
    wrap.append(svg, legend);
    return wrap;
  };
})();
