import type { ProcessComparison } from "../api/types";
import { fmtCI, fmtCompact, fmtSigned } from "../lib/format";
import { statusOf } from "../lib/status";

const W = 720;
const ROW_H = 24;
const LABEL_W = 190;
const PAD_R = 24;
const TOP = 28;
const BOTTOM = 34;

function niceTicks(lo: number, hi: number, count = 5): number[] {
  const span = hi - lo || 1;
  const raw = span / count;
  const mag = 10 ** Math.floor(Math.log10(raw));
  const step = [1, 2, 5, 10].map((m) => m * mag).find((s) => s >= raw) ?? raw;
  const out: number[] = [];
  for (let v = Math.ceil(lo / step) * step; v <= hi + 1e-9; v += step) out.push(Math.abs(v) < 1e-9 ? 0 : v);
  return out;
}

export function ForestPlot({
  title,
  unit,
  rows,
}: {
  title: string;
  unit: string;
  rows: ProcessComparison[];
}) {
  const bounds = rows.flatMap((r) => {
    const c = r.comparison;
    return [c.moses_ci_low, c.moses_ci_high, c.hl_shift].filter(
      (v): v is number => v !== null,
    );
  });
  const lo = Math.min(0, ...bounds);
  const hi = Math.max(0, ...bounds);
  const pad = (hi - lo || 1) * 0.05;
  const x0 = lo - pad;
  const x1 = hi + pad;
  const plotW = W - LABEL_W - PAD_R;
  const sx = (v: number) => LABEL_W + ((v - x0) / (x1 - x0)) * plotW;
  const H = TOP + rows.length * ROW_H + BOTTOM;
  const ticks = niceTicks(x0, x1);

  return (
    <figure style={{ margin: 0 }}>
      <figcaption className="panel-head">
        <h3>{title}</h3>
        <span className="muted" style={{ fontSize: "0.8rem" }}>
          Shift B minus A in {unit}; whiskers are 95% Moses CI
        </span>
      </figcaption>
      <div className="chart-wrap" tabIndex={0} role="region" aria-label={`${title} forest plot (scrollable)`}>
        <svg
          className="chart"
          viewBox={`0 0 ${W} ${H}`}
          style={{ minWidth: 560 }}
          role="img"
          aria-label={`${title}: ${rows.length} processes. Full values are in the table below.`}
          data-testid="forest-plot"
        >
          {ticks.map((t) => (
            <g key={t}>
              <line className="gridline" x1={sx(t)} x2={sx(t)} y1={TOP - 6} y2={H - BOTTOM + 4} />
              <text x={sx(t)} y={H - BOTTOM + 18} textAnchor="middle">
                {fmtCompact(t)}
              </text>
            </g>
          ))}
          <line
            className="zero"
            data-testid="zero-line"
            x1={sx(0)}
            x2={sx(0)}
            y1={TOP - 10}
            y2={H - BOTTOM + 4}
          />
          <text x={sx(0)} y={12} textAnchor="middle">
            no change
          </text>
          {rows.map((r, i) => {
            const c = r.comparison;
            const st = statusOf(c).kind;
            const y = TOP + i * ROW_H + ROW_H / 2;
            const hasCI = c.moses_ci_low !== null && c.moses_ci_high !== null;
            const wcls = st === "regression" ? "whisker whisker-flag" : st === "new_activity" ? "whisker whisker-new" : "whisker";
            return (
              <g key={r.process} data-testid="forest-row">
                <title>
                  {`${r.process}: shift ${fmtSigned(c.hl_shift, 0)} ${unit}, 95% CI ${fmtCI(c.moses_ci_low, c.moses_ci_high, 0)}, ${statusOf(c).label}`}
                </title>
                <text className="rowlabel" x={LABEL_W - 8} y={y + 4} textAnchor="end">
                  {r.process.length > 24 ? `${r.process.slice(0, 23)}…` : r.process}
                </text>
                {hasCI && (
                  <g className={wcls}>
                    <line x1={sx(c.moses_ci_low!)} x2={sx(c.moses_ci_high!)} y1={y} y2={y} />
                    <line x1={sx(c.moses_ci_low!)} x2={sx(c.moses_ci_low!)} y1={y - 4} y2={y + 4} />
                    <line x1={sx(c.moses_ci_high!)} x2={sx(c.moses_ci_high!)} y1={y - 4} y2={y + 4} />
                  </g>
                )}
                {c.hl_shift !== null &&
                  (st === "regression" ? (
                    <path className={`mark mark-${st}`} d={`M${sx(c.hl_shift)} ${y - 6}L${sx(c.hl_shift) + 6} ${y + 5}H${sx(c.hl_shift) - 6}Z`} />
                  ) : st === "new_activity" ? (
                    <path className={`mark mark-${st}`} d={`M${sx(c.hl_shift)} ${y - 6}L${sx(c.hl_shift) + 6} ${y}L${sx(c.hl_shift)} ${y + 6}L${sx(c.hl_shift) - 6} ${y}Z`} />
                  ) : (
                    <circle className={`mark mark-${st}`} cx={sx(c.hl_shift)} cy={y} r={4.5} />
                  ))}
              </g>
            );
          })}
        </svg>
      </div>
      <ul className="legend" aria-label="Marker key">
        <li>
          <svg width="14" height="14" viewBox="0 0 14 14" aria-hidden="true">
            <path d="M7 1.5 13 12.5H1Z" fill="var(--bad)" />
          </svg>
          Regression
        </li>
        <li>
          <svg width="14" height="14" viewBox="0 0 14 14" aria-hidden="true">
            <path d="M7 1 13 7 7 13 1 7Z" fill="var(--warn)" />
          </svg>
          New activity
        </li>
        <li>
          <svg width="14" height="14" viewBox="0 0 14 14" aria-hidden="true">
            <circle cx="7" cy="7" r="4.5" fill="var(--ink-2)" />
          </svg>
          Other
        </li>
      </ul>
    </figure>
  );
}
