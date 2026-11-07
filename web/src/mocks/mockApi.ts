import { ApiError } from "../api/client";
import type { ProcessRow, WtbaApi } from "../api/types";
import { RUNS, SCENARIOS, compareFixture, processesFor, samplesFor } from "./fixtures";

function delay<T>(v: T, signal?: AbortSignal): Promise<T> {
  return new Promise((resolve, reject) => {
    const t = setTimeout(() => resolve(v), 120);
    signal?.addEventListener("abort", () => {
      clearTimeout(t);
      reject(new ApiError("aborted", "Request cancelled"));
    });
  });
}

function notFound(msg: string): ApiError {
  return new ApiError("http", msg, 404, "not_found");
}

export const mockApi: WtbaApi = {
  scenarios: (s) => delay(SCENARIOS, s),
  runs: (p, s) => {
    const items = RUNS.filter(
      (r) =>
        (p.scenario === undefined || r.scenario === p.scenario) &&
        (p.condition === undefined || r.condition === p.condition),
    ).sort((x, y) => y.started_utc.localeCompare(x.started_utc));
    return delay(
      { total: items.length, limit: p.limit ?? 100, offset: 0, items: items.slice(0, p.limit ?? 100) },
      s,
    );
  },
  run: (id, s) => {
    const r = RUNS.find((x) => x.run_id === id);
    return r ? delay(r, s) : Promise.reject(notFound(`no run with id ${id}`));
  },
  processes: (id, sort, s) => {
    if (!RUNS.some((x) => x.run_id === id)) return Promise.reject(notFound(`no run with id ${id}`));
    const rows: ProcessRow[] = [...processesFor(id)].sort((x, y) => y[sort] - x[sort]);
    return delay(rows, s);
  },
  compare: (p, s) => {
    const c = compareFixture(p.scenario, p.a, p.b);
    if (!c) {
      return Promise.reject(
        new ApiError("http", `group a (condition '${p.a}') has no runs in scenario '${p.scenario}'`, 404, "group_empty"),
      );
    }
    const metrics = p.metric && p.metric !== "all" ? { [p.metric]: c.metrics[p.metric] } : c.metrics;
    return delay({ ...c, metrics, threshold_pct: p.threshold_pct ?? c.threshold_pct }, s);
  },
  trend: (p, s) => {
    const pts = RUNS.filter(
      (r) =>
        r.scenario === p.scenario &&
        r.gates_passed &&
        (p.condition === undefined || r.condition === p.condition),
    )
      .sort((x, y) => x.started_utc.localeCompare(y.started_utc))
      .map((r) => ({
        started_utc: r.started_utc,
        run_id: r.run_id,
        condition: r.condition,
        label: r.label,
        value: r[p.metric] ?? 0,
      }));
    const failed = RUNS.filter((r) => r.scenario === p.scenario && !r.gates_passed).length;
    return delay({ scenario: p.scenario, metric: p.metric, excluded_failed_gates: failed, points: pts }, s);
  },
  samples: (id, s) => {
    const r = RUNS.find((x) => x.run_id === id);
    return r ? delay(samplesFor(r), s) : Promise.reject(notFound(`no run with id ${id}`));
  },
};
