import { useState } from "react";
import type { BatterySample } from "../api/types";
import { fmtCompact, fmtNum } from "../lib/format";

const W = 760;
const H = 280;
const M = { l: 56, r: 16, t: 28, b: 40 };

export function DischargeChart({ samples }: { samples: BatterySample[] }) {
  const [idx, setIdx] = useState<number | null>(null);
  if (samples.length === 0) return null;

  const tMax = Math.max(...samples.map((s) => s.t_s)) || 1;
  const vMax = Math.max(...samples.map((s) => s.discharge_mw)) * 1.08;
  const sx = (t: number) => M.l + (t / tMax) * (W - M.l - M.r);
  const sy = (v: number) => H - M.b - (v / vMax) * (H - M.t - M.b);
  const yTicks = [0, 0.25, 0.5, 0.75, 1].map((f) => f * vMax);
  const xTicks = [0, 0.25, 0.5, 0.75, 1].map((f) => f * tMax);
  const cur = idx === null ? null : samples[idx];

  const onKey = (e: React.KeyboardEvent) => {
    const step = e.shiftKey ? 10 : 1;
    if (e.key === "ArrowRight") setIdx((i) => Math.min(samples.length - 1, (i ?? -1) + step));
    else if (e.key === "ArrowLeft") setIdx((i) => Math.max(0, (i ?? 1) - step));
    else if (e.key === "Home") setIdx(0);
    else if (e.key === "End") setIdx(samples.length - 1);
    else if (e.key === "Escape") setIdx(null);
    else return;
    e.preventDefault();
  };

  const onMove = (e: React.PointerEvent<SVGSVGElement>) => {
    const r = e.currentTarget.getBoundingClientRect();
    const x = ((e.clientX - r.left) / r.width) * W;
    const t = ((x - M.l) / (W - M.l - M.r)) * tMax;
    let best = 0;
    for (let i = 1; i < samples.length; i++) {
      if (Math.abs(samples[i]!.t_s - t) < Math.abs(samples[best]!.t_s - t)) best = i;
    }
    setIdx(best);
  };

  return (
    <div>
      <div className="chart-wrap">
        <svg
          className="chart"
          viewBox={`0 0 ${W} ${H}`}
          style={{ minWidth: 520 }}
          tabIndex={0}
          role="img"
          aria-label={`Discharge rate over time, ${samples.length} samples. Use left and right arrow keys to inspect values.`}
          onKeyDown={onKey}
          onPointerMove={onMove}
          onPointerLeave={() => setIdx(null)}
          data-testid="discharge-chart"
        >
          {yTicks.map((v) => (
            <g key={v}>
              <line className="gridline" x1={M.l} x2={W - M.r} y1={sy(v)} y2={sy(v)} />
              <text x={M.l - 8} y={sy(v) + 4} textAnchor="end">
                {fmtCompact(v)}
              </text>
            </g>
          ))}
          {xTicks.map((t) => (
            <text key={t} x={sx(t)} y={H - M.b + 18} textAnchor="middle">
              {Math.round(t)}s
            </text>
          ))}
          <text x={M.l - 8} y={12}>Discharge rate (mW)</text>
          <polyline
            className="line line-neutral"
            points={samples.map((s) => `${sx(s.t_s)},${sy(s.discharge_mw)}`).join(" ")}
          />
          {cur && (
            <g>
              <line className="cross" x1={sx(cur.t_s)} x2={sx(cur.t_s)} y1={M.t} y2={H - M.b} />
              <circle cx={sx(cur.t_s)} cy={sy(cur.discharge_mw)} r={5} fill="var(--accent)" stroke="var(--surface)" strokeWidth={2} />
            </g>
          )}
        </svg>
      </div>
      <div className="tooltip" aria-live="polite">
        {cur ? (
          <>
            t = {fmtNum(cur.t_s, 0)} s: <strong>{fmtNum(cur.discharge_mw, 0)} mW</strong>
          </>
        ) : (
          <span className="muted">Hover the chart, or focus it and use arrow keys (Shift for bigger steps).</span>
        )}
      </div>
    </div>
  );
}
