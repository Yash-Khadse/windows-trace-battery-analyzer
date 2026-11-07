import { useSearchParams } from "react-router-dom";
import { useApi } from "../api/context";
import { METRIC_NAMES, type MetricName } from "../api/types";
import { EmptyState, ErrorState, NoDataState, Skeleton } from "../components/States";
import { TrendChart } from "../components/TrendChart";
import { METRIC_META } from "../lib/status";
import { useAsync } from "../lib/useAsync";

export function TrendPage() {
  const api = useApi();
  const [params, setParams] = useSearchParams();
  const scen = useAsync((s) => api.scenarios(s), [api]);
  const scenario = params.get("scenario") ?? (scen.status === "ok" ? (scen.data[0]?.name ?? "") : "");
  const m = params.get("metric");
  const metric: MetricName = (METRIC_NAMES as readonly string[]).includes(m ?? "")
    ? (m as MetricName)
    : "mean_discharge_mw";

  const trend = useAsync(
    (s) => (scenario ? api.trend({ scenario, metric }, s) : Promise.resolve(null)),
    [api, scenario, metric],
  );

  if (scen.status === "loading") {
    return (
      <>
        <h1>Trend</h1>
        <Skeleton label="Loading scenarios" />
      </>
    );
  }
  if (scen.status === "error") return <ErrorState error={scen.error} onRetry={scen.reload} />;
  if (scen.data.length === 0) return <NoDataState />;

  const meta = METRIC_META[metric];
  const upd = (k: string, v: string) => {
    const n = new URLSearchParams(params);
    n.set(k, v);
    setParams(n);
  };

  return (
    <div className="stack">
      <div className="page-head">
        <h1>Trend over time</h1>
        <span className="muted">Each point is one run.</span>
      </div>
      <div className="controls" role="group" aria-label="Trend selection">
        <label className="field">
          Scenario
          <select value={scenario} onChange={(e) => upd("scenario", e.target.value)}>
            {scen.data.map((s) => (
              <option key={s.name}>{s.name}</option>
            ))}
          </select>
        </label>
        <label className="field">
          Metric
          <select value={metric} onChange={(e) => upd("metric", e.target.value)}>
            {METRIC_NAMES.map((k) => (
              <option key={k} value={k}>
                {METRIC_META[k].label} ({METRIC_META[k].unit})
              </option>
            ))}
          </select>
        </label>
      </div>
      {trend.status === "loading" && <Skeleton label="Loading trend" rows={2} />}
      {trend.status === "error" && <ErrorState error={trend.error} onRetry={trend.reload} />}
      {trend.status === "ok" && trend.data && (
        <section className="panel" aria-labelledby="h-trend">
          <h2 id="h-trend">
            {meta.label} ({meta.unit}), {trend.data.points.length} runs
          </h2>
          {trend.data.excluded_failed_gates > 0 && (
            <div className="notice notice-warn" role="note" style={{ margin: "0.75rem 0" }}>
              {trend.data.excluded_failed_gates} run(s) that failed quality gates are not shown.
            </div>
          )}
          {trend.data.points.length === 0 ? (
            <EmptyState title="No runs for this scenario">
              <p className="muted">Run the experiment first, then reload.</p>
            </EmptyState>
          ) : (
            <TrendChart points={trend.data.points} unit={meta.unit} metricLabel={meta.label} />
          )}
        </section>
      )}
    </div>
  );
}
