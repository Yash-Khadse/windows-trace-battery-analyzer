import type { CompareResult, MetricName, ProcessComparison } from "../api/types";
import { fmtCI, fmtDelta, fmtNum, fmtP, fmtPct, fmtSigned } from "../lib/format";
import { METRIC_NAMES } from "../api/types";
import { statusOf } from "../lib/status";
import { ForestPlot } from "./ForestPlot";
import { MetricCard } from "./MetricCard";
import { SortableTable, type Column } from "./SortableTable";
import { StatusChip } from "./StatusChip";

function ProcessTable({ title, unit, rows }: { title: string; unit: string; rows: ProcessComparison[] }) {
  const cols: Column<ProcessComparison>[] = [
    { key: "process", header: "Process", className: "proc", render: (r) => r.process, sortValue: (r) => r.process },
    { key: "status", header: "Status", render: (r) => <StatusChip status={statusOf(r.comparison)} />, sortValue: (r) => statusOf(r.comparison).kind },
    { key: "n", header: "N (A/B)", numeric: true, render: (r) => `${r.comparison.n_a}/${r.comparison.n_b}` },
    { key: "ma", header: `Median A (${unit})`, numeric: true, render: (r) => fmtNum(r.comparison.median_a, 0), sortValue: (r) => r.comparison.median_a },
    { key: "mb", header: `Median B (${unit})`, numeric: true, render: (r) => fmtNum(r.comparison.median_b, 0), sortValue: (r) => r.comparison.median_b },
    { key: "pct", header: "% change", numeric: true, render: (r) => fmtPct(r.comparison.pct_change), sortValue: (r) => r.comparison.pct_change },
    { key: "hl", header: "HL shift", numeric: true, render: (r) => fmtSigned(r.comparison.hl_shift, 0), sortValue: (r) => r.comparison.hl_shift },
    { key: "ci", header: "95% CI (Moses)", numeric: true, render: (r) => fmtCI(r.comparison.moses_ci_low, r.comparison.moses_ci_high, 0) },
    { key: "p", header: "p", numeric: true, render: (r) => fmtP(r.comparison.p_value), sortValue: (r) => r.comparison.p_value },
    { key: "q", header: "q", numeric: true, render: (r) => fmtP(r.comparison.q_value), sortValue: (r) => r.comparison.q_value },
    { key: "d", header: "Cliff's d", numeric: true, render: (r) => fmtDelta(r.comparison.cliffs_delta), sortValue: (r) => r.comparison.cliffs_delta },
  ]; // prettier-ignore
  return (
    <SortableTable
      caption={`${title} - top ${rows.length} processes by effect size`}
      columns={cols}
      rows={rows}
      rowKey={(r) => r.process}
    />
  );
}

export function ComparisonView({ data }: { data: CompareResult }) {
  const metrics = METRIC_NAMES.filter((m): m is MetricName => data.metrics[m] !== undefined);
  const excluded = data.excluded_failed_gates;
  const smallN = Math.min(data.runs_a, data.runs_b) < 5;
  return (
    <div className="stack">
      <p className="muted" style={{ margin: 0 }} data-testid="compare-summary">
        <strong>{data.scenario}</strong>: Group A (<code>{data.a}</code>): {data.runs_a} runs vs Group B (<code>{data.b}</code>): {data.runs_b} runs (N={data.runs_a} / N={data.runs_b}). Regression threshold: {data.threshold_pct}% change,
        95% CI excluding 0, q &lt; 0.05, direction worse. Failed-gate runs excluded: A {excluded.a}, B{" "}
        {excluded.b}.
      </p>
      {smallN && (
        <div className="notice notice-warn" role="note">
          <strong>Insufficient data.</strong> At least one group has fewer than 5 runs, so nothing can be
          flagged as a regression.
        </div>
      )}
      {(excluded.a > 0 || excluded.b > 0) && (
        <div className="notice notice-warn" role="note">
          <strong>Excluded runs.</strong> {excluded.a} run(s) in A and {excluded.b} run(s) in B failed
          quality gates and are not part of these statistics.
        </div>
      )}
      <section aria-labelledby="h-metrics">
        <h2 id="h-metrics" className="sr-only">
          Overall metrics
        </h2>
        <div className="cards">
          {metrics.map((m) => (
            <MetricCard key={m} metric={m} c={data.metrics[m]!} thresholdPct={data.threshold_pct} />
          ))}
        </div>
      </section>
      <section className="panel" aria-labelledby="h-forest">
        <h2 id="h-forest">Per-process effects</h2>
        <p className="muted" style={{ marginTop: 0 }}>
          Top {data.per_process.cpu_ms.length} by effect size for each measure; processes present in at
          least half of the runs in both groups. N = {data.runs_a} (A) / {data.runs_b} (B), threshold{" "}
          {data.threshold_pct}%.
        </p>
        <div className="stack">
          {data.per_process.cpu_ms.length > 0 ? (
            <ForestPlot title="CPU time" unit="ms" rows={data.per_process.cpu_ms} />
          ) : (
            <p className="muted">No per-process CPU data.</p>
          )}
          {data.per_process.wakeups.length > 0 ? (
            <ForestPlot title="Wakeups" unit="wakeups" rows={data.per_process.wakeups} />
          ) : (
            <p className="muted">No per-process wakeup data.</p>
          )}
        </div>
      </section>
      <section aria-labelledby="h-tables">
        <h2 id="h-tables" style={{ marginBottom: "0.75rem" }}>
          Process detail
        </h2>
        <div className="stack">
          <ProcessTable title="CPU time" unit="ms" rows={data.per_process.cpu_ms} />
          <ProcessTable title="Wakeups" unit="count" rows={data.per_process.wakeups} />
        </div>
      </section>
    </div>
  );
}
