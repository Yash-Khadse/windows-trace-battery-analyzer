import { useState, type FormEvent } from "react";
import { useSearchParams } from "react-router-dom";
import { useApi } from "../api/context";
import { METRIC_NAMES, type MetricName } from "../api/types";
import { ComparisonView } from "../components/ComparisonView";
import { CardsSkeleton, ErrorState, NoDataState, Skeleton } from "../components/States";
import { METRIC_META } from "../lib/status";
import { useAsync } from "../lib/useAsync";

interface Query {
  scenario: string;
  a: string;
  b: string;
  metric: MetricName | "all";
  thr: number;
}

function Controls({
  q,
  scenarios,
  labels,
  onApply,
}: {
  q: Query;
  scenarios: string[];
  labels: string[];
  onApply: (q: Query) => void;
}) {
  const [draft, setDraft] = useState(q);
  const groups = ["A", "B", ...labels];
  const set = <K extends keyof Query>(k: K, v: Query[K]) => setDraft((d) => ({ ...d, [k]: v }));
  const submit = (e: FormEvent) => {
    e.preventDefault();
    onApply(draft);
  };
  return (
    <form className="controls" onSubmit={submit} aria-label="Comparison selection">
      <label className="field">
        Scenario
        <select value={draft.scenario} onChange={(e) => set("scenario", e.target.value)}>
          {scenarios.map((s) => (
            <option key={s}>{s}</option>
          ))}
        </select>
      </label>
      <label className="field">
        Group A (baseline)
        <select value={draft.a} onChange={(e) => set("a", e.target.value)}>
          {groups.map((g) => (
            <option key={g}>{g}</option>
          ))}
        </select>
      </label>
      <label className="field">
        Group B (candidate)
        <select value={draft.b} onChange={(e) => set("b", e.target.value)}>
          {groups.map((g) => (
            <option key={g}>{g}</option>
          ))}
        </select>
      </label>
      <label className="field">
        Metric
        <select value={draft.metric} onChange={(e) => set("metric", e.target.value as Query["metric"])}>
          <option value="all">All metrics</option>
          {METRIC_NAMES.map((m) => (
            <option key={m} value={m}>
              {METRIC_META[m].label} ({METRIC_META[m].unit})
            </option>
          ))}
        </select>
      </label>
      <label className="field">
        Threshold (% change)
        <input
          type="number"
          min={0}
          max={1000}
          step={0.5}
          value={draft.thr}
          onChange={(e) => set("thr", Number(e.target.value))}
        />
      </label>
      <div className="field" style={{ justifyContent: "flex-end" }}>
        <button type="submit" className="btn btn-primary">
          Compare
        </button>
      </div>
    </form>
  );
}

export function ComparePage() {
  const api = useApi();
  const [params, setParams] = useSearchParams();
  const scen = useAsync((s) => api.scenarios(s), [api]);

  const firstScenario = scen.status === "ok" ? (scen.data[0]?.name ?? "") : "";
  const rawMetric = params.get("metric");
  const q: Query = {
    scenario: params.get("scenario") ?? firstScenario,
    a: params.get("a") ?? "A",
    b: params.get("b") ?? "B",
    metric: (METRIC_NAMES as readonly string[]).includes(rawMetric ?? "") ? (rawMetric as MetricName) : "all",
    thr: Number(params.get("thr") ?? 5) || 5,
  };

  const runs = useAsync(
    (s) => (q.scenario ? api.runs({ scenario: q.scenario, limit: 500 }, s) : Promise.resolve(null)),
    [api, q.scenario],
  );
  const labels =
    runs.status === "ok" && runs.data
      ? [...new Set(runs.data.items.map((r) => r.label))].sort()
      : [];

  const result = useAsync(
    (s) =>
      q.scenario
        ? api.compare({ scenario: q.scenario, a: q.a, b: q.b, metric: q.metric, threshold_pct: q.thr }, s)
        : Promise.resolve(null),
    [api, q.scenario, q.a, q.b, q.metric, q.thr],
  );

  if (scen.status === "loading") {
    return (
      <>
        <h1>Compare</h1>
        <Skeleton label="Loading scenarios" />
      </>
    );
  }
  if (scen.status === "error") return <ErrorState error={scen.error} onRetry={scen.reload} />;
  if (scen.data.length === 0) return <NoDataState />;

  const apply = (n: Query) =>
    setParams({ scenario: n.scenario, a: n.a, b: n.b, metric: n.metric, thr: String(n.thr) });

  return (
    <div className="stack">
      <div className="page-head">
        <h1>Compare conditions</h1>
        <span className="muted">Is B worse than A? Effects are B minus A.</span>
      </div>
      <Controls
        key={`${q.scenario}|${q.a}|${q.b}|${q.metric}|${q.thr}`}
        q={q}
        scenarios={scen.data.map((s) => s.name)}
        labels={labels}
        onApply={apply}
      />
      {result.status === "loading" && <CardsSkeleton />}
      {result.status === "error" && (
        <ErrorState error={result.error} onRetry={result.reload} />
      )}
      {result.status === "ok" && result.data && <ComparisonView data={result.data} />}
    </div>
  );
}
