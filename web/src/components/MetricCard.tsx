import type { MetricComparison, MetricName } from "../api/types";
import { fmtCI, fmtDelta, fmtNum, fmtP, fmtPct, fmtSigned } from "../lib/format";
import { METRIC_META, statusOf } from "../lib/status";
import { StatusChip } from "./StatusChip";

export function MetricCard({
  metric,
  c,
  thresholdPct,
}: {
  metric: MetricName;
  c: MetricComparison;
  thresholdPct: number;
}) {
  const meta = METRIC_META[metric];
  const status = statusOf(c);
  const hl = fmtSigned(c.hl_shift, meta.digits);
  return (
    <article className="card" data-status={status.kind} aria-labelledby={`m-${metric}`}>
      <div className="card-head">
        <h3 id={`m-${metric}`}>
          {meta.label} <span className="muted">({meta.unit})</span>
        </h3>
        <StatusChip status={status} />
      </div>
      <div className="big">
        {fmtPct(c.pct_change)} <small>vs A median</small>
      </div>
      <dl className="kv">
        <dt>N (A / B)</dt>
        <dd>
          {c.n_a} / {c.n_b}
        </dd>
        <dt>Median A</dt>
        <dd>{fmtNum(c.median_a, meta.digits)}</dd>
        <dt>Median B</dt>
        <dd>{fmtNum(c.median_b, meta.digits)}</dd>
        <dt>Hodges-Lehmann shift</dt>
        <dd>
          {hl} {meta.unit}
        </dd>
        <dt>95% CI (Moses)</dt>
        <dd>
          <strong>{fmtCI(c.moses_ci_low, c.moses_ci_high, meta.digits)}</strong>
        </dd>
        <dt>95% CI (bootstrap)</dt>
        <dd className="sub">{fmtCI(c.boot_ci_low, c.boot_ci_high, meta.digits)}</dd>
        <dt>p / q</dt>
        <dd>
          {fmtP(c.p_value)} / {fmtP(c.q_value)}
        </dd>
        <dt>Cliff&apos;s delta</dt>
        <dd>{fmtDelta(c.cliffs_delta)}</dd>
        <dt>Threshold</dt>
        <dd>{thresholdPct}% change</dd>
      </dl>
      <p className="muted" style={{ margin: 0, fontSize: "0.82rem" }}>
        {c.reason}
      </p>
      {c.warnings.length > 0 && (
        <div className="notice notice-warn" role="note">
          <strong>Warnings</strong>
          <ul>
            {c.warnings.map((w) => (
              <li key={w}>{w}</li>
            ))}
          </ul>
        </div>
      )}
    </article>
  );
}
