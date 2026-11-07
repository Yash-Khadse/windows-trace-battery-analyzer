import type {
  BatterySample,
  CompareResult,
  MetricComparison,
  MetricName,
  ProcessComparison,
  ProcessRow,
  Run,
  RunPage,
  Scenario,
  TrendResult,
} from "./types";
import { METRIC_NAMES } from "./types";

export class ShapeError extends Error {
  constructor(path: string, expected: string) {
    super(`Unexpected API response at ${path}: expected ${expected}`);
    this.name = "ShapeError";
  }
}

type Rec = Record<string, unknown>;

function rec(v: unknown, path: string): Rec {
  if (typeof v !== "object" || v === null || Array.isArray(v)) {
    throw new ShapeError(path, "object");
  }
  return v as Rec;
}
function arr(v: unknown, path: string): unknown[] {
  if (!Array.isArray(v)) throw new ShapeError(path, "array");
  return v;
}
function str(o: Rec, k: string, path: string): string {
  const v = o[k];
  if (typeof v !== "string") throw new ShapeError(`${path}.${k}`, "string");
  return v;
}
function num(o: Rec, k: string, path: string): number {
  const v = o[k];
  if (typeof v !== "number" || !Number.isFinite(v)) {
    throw new ShapeError(`${path}.${k}`, "finite number");
  }
  return v;
}
function bool(o: Rec, k: string, path: string): boolean {
  const v = o[k];
  if (typeof v !== "boolean") throw new ShapeError(`${path}.${k}`, "boolean");
  return v;
}
function numOrNull(o: Rec, k: string, path: string): number | null {
  const v = o[k];
  if (v === null || v === undefined) return null;
  if (typeof v !== "number" || !Number.isFinite(v)) {
    throw new ShapeError(`${path}.${k}`, "number or null");
  }
  return v;
}
function strOrNull(o: Rec, k: string, path: string): string | null {
  const v = o[k];
  if (v === null || v === undefined) return null;
  if (typeof v !== "string") throw new ShapeError(`${path}.${k}`, "string or null");
  return v;
}

export function isMetricName(v: unknown): v is MetricName {
  return typeof v === "string" && (METRIC_NAMES as readonly string[]).includes(v);
}

export function parseScenario(v: unknown, path = "scenario"): Scenario {
  const o = rec(v, path);
  return {
    name: str(o, "name", path),
    run_count: num(o, "run_count", path),
    first_started_utc: strOrNull(o, "first_started_utc", path),
    last_started_utc: strOrNull(o, "last_started_utc", path),
  };
}

export function parseScenarios(v: unknown): Scenario[] {
  return arr(v, "scenarios").map((x, i) => parseScenario(x, `scenarios[${i}]`));
}

export function parseRun(v: unknown, path = "run"): Run {
  const o = rec(v, path);
  return {
    run_id: str(o, "run_id", path),
    scenario: str(o, "scenario", path),
    machine_id_hash: str(o, "machine_id_hash", path),
    condition: str(o, "condition", path),
    label: str(o, "label", path),
    started_utc: str(o, "started_utc", path),
    duration_s: num(o, "duration_s", path),
    on_ac: bool(o, "on_ac", path),
    battery_saver: bool(o, "battery_saver", path),
    plan_guid: str(o, "plan_guid", path),
    start_pct: num(o, "start_pct", path),
    end_pct: num(o, "end_pct", path),
    dropped_events: num(o, "dropped_events", path),
    sampler_gaps_s: num(o, "sampler_gaps_s", path),
    gates_passed: bool(o, "gates_passed", path),
    mean_discharge_mw: numOrNull(o, "mean_discharge_mw", path),
    total_cpu_ms: num(o, "total_cpu_ms", path),
    total_wakeups: num(o, "total_wakeups", path),
  };
}

export function parseRunPage(v: unknown): RunPage {
  const o = rec(v, "runs");
  return {
    total: num(o, "total", "runs"),
    limit: num(o, "limit", "runs"),
    offset: num(o, "offset", "runs"),
    items: arr(o.items, "runs.items").map((x, i) => parseRun(x, `runs.items[${i}]`)),
  };
}

export function parseProcesses(v: unknown): ProcessRow[] {
  return arr(v, "processes").map((x, i) => {
    const p = `processes[${i}]`;
    const o = rec(x, p);
    return {
      process: str(o, "process", p),
      cpu_ms: num(o, "cpu_ms", p),
      context_switches: num(o, "context_switches", p),
      wakeups: num(o, "wakeups", p),
      wakeups_per_s: num(o, "wakeups_per_s", p),
    };
  });
}

export function parseComparison(v: unknown, path = "comparison"): MetricComparison {
  const o = rec(v, path);
  return {
    n_a: num(o, "n_a", path),
    n_b: num(o, "n_b", path),
    median_a: numOrNull(o, "median_a", path),
    median_b: numOrNull(o, "median_b", path),
    hl_shift: numOrNull(o, "hl_shift", path),
    moses_ci_low: numOrNull(o, "moses_ci_low", path),
    moses_ci_high: numOrNull(o, "moses_ci_high", path),
    boot_ci_low: numOrNull(o, "boot_ci_low", path),
    boot_ci_high: numOrNull(o, "boot_ci_high", path),
    pct_change: numOrNull(o, "pct_change", path),
    p_value: num(o, "p_value", path),
    q_value: num(o, "q_value", path),
    cliffs_delta: num(o, "cliffs_delta", path),
    regression: bool(o, "regression", path),
    new_activity: bool(o, "new_activity", path),
    reason: str(o, "reason", path),
    warnings: arr(o.warnings, `${path}.warnings`).map((w, i) => {
      if (typeof w !== "string") throw new ShapeError(`${path}.warnings[${i}]`, "string");
      return w;
    }),
  };
}

function parseProcComparisons(v: unknown, path: string): ProcessComparison[] {
  return arr(v, path).map((x, i) => {
    const p = `${path}[${i}]`;
    const o = rec(x, p);
    return {
      process: str(o, "process", p),
      comparison: parseComparison(o.comparison, `${p}.comparison`),
    };
  });
}

export function parseCompare(v: unknown): CompareResult {
  const p = "compare";
  const o = rec(v, p);
  const ex = rec(o.excluded_failed_gates, `${p}.excluded_failed_gates`);
  const metricsRaw = rec(o.metrics, `${p}.metrics`);
  const metrics: CompareResult["metrics"] = {};
  for (const [k, val] of Object.entries(metricsRaw)) {
    if (isMetricName(k)) metrics[k] = parseComparison(val, `${p}.metrics.${k}`);
  }
  const pp = rec(o.per_process, `${p}.per_process`);
  return {
    scenario: str(o, "scenario", p),
    a: str(o, "a", p),
    b: str(o, "b", p),
    threshold_pct: num(o, "threshold_pct", p),
    include_warmup: bool(o, "include_warmup", p),
    include_failed_gates: bool(o, "include_failed_gates", p),
    excluded_failed_gates: {
      a: num(ex, "a", `${p}.excluded_failed_gates`),
      b: num(ex, "b", `${p}.excluded_failed_gates`),
    },
    runs_a: num(o, "runs_a", p),
    runs_b: num(o, "runs_b", p),
    metrics,
    per_process: {
      cpu_ms: parseProcComparisons(pp.cpu_ms, `${p}.per_process.cpu_ms`),
      wakeups: parseProcComparisons(pp.wakeups, `${p}.per_process.wakeups`),
    },
  };
}

export function parseTrend(v: unknown): TrendResult {
  const p = "trend";
  const o = rec(v, p);
  const metric = o.metric;
  if (!isMetricName(metric)) throw new ShapeError(`${p}.metric`, "metric name");
  return {
    scenario: str(o, "scenario", p),
    metric,
    excluded_failed_gates: num(o, "excluded_failed_gates", p),
    points: arr(o.points, `${p}.points`).map((x, i) => {
      const q = `${p}.points[${i}]`;
      const r = rec(x, q);
      return {
        started_utc: str(r, "started_utc", q),
        run_id: str(r, "run_id", q),
        condition: str(r, "condition", q),
        label: str(r, "label", q),
        value: num(r, "value", q),
      };
    }),
  };
}

export function parseSamples(v: unknown): BatterySample[] {
  const root = rec(v, "samples");
  str(root, "run_id", "samples");
  return arr(root.samples, "samples.samples").map((x, i) => {
    const p = `samples.samples[${i}]`;
    const o = rec(x, p);
    return {
      t_s: num(o, "t_s", p),
      discharge_mw: num(o, "discharge_mw", p),
      remaining_mwh: num(o, "remaining_mwh", p),
    };
  });
}

/** Parses the uniform error body, tolerating anything else. */
export function parseErrorBody(v: unknown): { code: string; message: string } | null {
  if (typeof v !== "object" || v === null) return null;
  const e = (v as Rec).error;
  if (typeof e !== "object" || e === null) return null;
  const { code, message } = e as Rec;
  if (typeof code === "string" && typeof message === "string") return { code, message };
  return null;
}
