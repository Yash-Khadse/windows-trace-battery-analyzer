import { useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import type { TrendPoint } from "../api/types";
import { fmtCompact, fmtDateTime, fmtNum, fmtTick } from "../lib/format";

const W = 760;
const H = 320;
const M = { l: 56, r: 16, t: 28, b: 40 };

function Marker({ cond, x, y, r, className }: {
  cond: string; x: number; y: number; r: number; className: string;
}) {
  return cond === "B" ? (
    <rect className={className} x={x - r} y={y - r} width={r * 2} height={r * 2} rx={1} />
  ) : (
    <circle className={className} cx={x} cy={y} r={r} />
  );
}

export function TrendChart({ points, unit, metricLabel }: {
  points: TrendPoint[]; unit: string; metricLabel: string;
}) {
  const nav = useNavigate();
  const [active, setActive] = useState<TrendPoint | null>(null);

  const geo = useMemo(() => {
    const ts = points.map((p) => Date.parse(p.started_utc));
    const vs = points.map((p) => p.value);
    const t0 = Math.min(...ts);
    const t1 = Math.max(...ts);
    const vmin = Math.min(...vs);
    const vmax = Math.max(...vs);
    const padV = (vmax - vmin || vmax || 1) * 0.12;
    const y0 = Math.max(0, vmin - padV);
    const y1 = vmax + padV;
    const sx = (t: number) => M.l + (t1 === t0 ? 0.5 : (t - t0) / (t1 - t0)) * (W - M.l - M.r);
    const sy = (v: number) => H - M.b - ((v - y0) / (y1 - y0)) * (H - M.t - M.b);
    const yTicks = Array.from({ length: 5 }, (_, i) => y0 + ((y1 - y0) * i) / 4);
    const xTicks = Array.from({ length: 4 }, (_, i) => t0 + ((t1 - t0) * i) / 3);
    return { sx, sy, yTicks, xTicks, span: t1 - t0 };
  }, [points]);

  const byCond = (c: string) => points.filter((p) => p.condition === c);

  return (
    <div>
      <ul className="legend" aria-label="Condition key">
        <li>
          <svg width="14" height="14" viewBox="0 0 14 14" aria-hidden="true">
            <circle cx="7" cy="7" r="5" fill="var(--cond-a)" />
          </svg>
          Condition A (circle)
        </li>
        <li>
          <svg width="14" height="14" viewBox="0 0 14 14" aria-hidden="true">
            <rect x="2" y="2" width="10" height="10" rx="1" fill="var(--cond-b)" />
          </svg>
          Condition B (square)
        </li>
      </ul>
      <div className="chart-wrap">
        <svg
          className="chart"
          viewBox={`0 0 ${W} ${H}`}
          style={{ minWidth: 520 }}
          role="group"
          aria-label={`${metricLabel} over time, ${points.length} runs. Use Tab to move between points, Enter to open a run.`}
          data-testid="trend-chart"
        >
          {geo.yTicks.map((v) => (
            <g key={v}>
              <line className="gridline" x1={M.l} x2={W - M.r} y1={geo.sy(v)} y2={geo.sy(v)} />
              <text x={M.l - 8} y={geo.sy(v) + 4} textAnchor="end">
                {fmtCompact(v)}
              </text>
            </g>
          ))}
          {geo.xTicks.map((t) => (
            <text key={t} x={geo.sx(t)} y={H - M.b + 18} textAnchor="middle">
              {fmtTick(t, geo.span)}
            </text>
          ))}
          <text x={M.l - 8} y={12} textAnchor="start">
            {metricLabel} ({unit})
          </text>
          {(["A", "B"] as const).map((c) => {
            const pts = byCond(c);
            if (pts.length < 2) return null;
            return (
              <polyline
                key={c}
                className={`line line-${c}`}
                strokeOpacity={0.45}
                points={pts.map((p) => `${geo.sx(Date.parse(p.started_utc))},${geo.sy(p.value)}`).join(" ")}
              />
            );
          })}
          {points.map((p) => {
            const x = geo.sx(Date.parse(p.started_utc));
            const y = geo.sy(p.value);
            return (
              <g
                key={p.run_id}
                tabIndex={0}
                role="link"
                aria-label={`${p.label}, condition ${p.condition}, ${fmtNum(p.value, 0)} ${unit}, ${fmtDateTime(p.started_utc)}`}
                onMouseEnter={() => setActive(p)}
                onFocus={() => setActive(p)}
                onKeyDown={(e) => {
                  if (e.key === "Enter") nav(`/runs/${p.run_id}`);
                }}
                onClick={() => nav(`/runs/${p.run_id}`)}
                data-testid="trend-point"
              >
                <circle cx={x} cy={y} r={14} fill="transparent" />
                <Marker cond={p.condition} x={x} y={y} r={5.5} className={`pt pt-${p.condition}`} />
              </g>
            );
          })}
        </svg>
      </div>
      <div className="tooltip" aria-live="polite">
        {active ? (
          <>
            <strong>{active.label}</strong> - condition {active.condition}
            <br />
            {fmtNum(active.value, 0)} {unit} at {fmtDateTime(active.started_utc)}.{" "}
            <a href={`/runs/${active.run_id}`} onClick={(e) => { e.preventDefault(); nav(`/runs/${active.run_id}`); }}>
              Open run
            </a>
          </>
        ) : (
          <span className="muted">Hover or focus a point to see its run. Press Enter to open it.</span>
        )}
      </div>
    </div>
  );
}
