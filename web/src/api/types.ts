/** TypeScript contract mirroring api/src/wtba_api/schemas.py. */

export type MetricName = "mean_discharge_mw" | "total_cpu_ms" | "total_wakeups";
export const METRIC_NAMES: readonly MetricName[] = [
  "mean_discharge_mw",
  "total_cpu_ms",
  "total_wakeups",
];

export interface Scenario {
  name: string;
  run_count: number;
  first_started_utc: string | null;
  last_started_utc: string | null;
}

export interface Run {
  run_id: string;
  scenario: string;
  machine_id_hash: string;
  condition: string;
  label: string;
  started_utc: string;
  duration_s: number;
  on_ac: boolean;
  battery_saver: boolean;
  plan_guid: string;
  start_pct: number;
  end_pct: number;
  dropped_events: number;
  sampler_gaps_s: number;
  gates_passed: boolean;
  mean_discharge_mw: number | null;
  total_cpu_ms: number;
  total_wakeups: number;
}

export interface RunPage {
  total: number;
  limit: number;
  offset: number;
  items: Run[];
}

export type ProcessSort = "cpu_ms" | "wakeups" | "context_switches";

export interface ProcessRow {
  process: string;
  cpu_ms: number;
  context_switches: number;
  wakeups: number;
  wakeups_per_s: number;
}

export interface MetricComparison {
  n_a: number;
  n_b: number;
  median_a: number | null;
  median_b: number | null;
  hl_shift: number | null;
  moses_ci_low: number | null;
  moses_ci_high: number | null;
  boot_ci_low: number | null;
  boot_ci_high: number | null;
  pct_change: number | null;
  p_value: number;
  q_value: number;
  cliffs_delta: number;
  regression: boolean;
  new_activity: boolean;
  reason: string;
  warnings: string[];
}

export interface ProcessComparison {
  process: string;
  comparison: MetricComparison;
}

export interface CompareResult {
  scenario: string;
  a: string;
  b: string;
  threshold_pct: number;
  include_warmup: boolean;
  include_failed_gates: boolean;
  excluded_failed_gates: { a: number; b: number };
  runs_a: number;
  runs_b: number;
  metrics: Partial<Record<MetricName, MetricComparison>>;
  per_process: { cpu_ms: ProcessComparison[]; wakeups: ProcessComparison[] };
}

export interface TrendPoint {
  started_utc: string;
  run_id: string;
  condition: string;
  label: string;
  value: number;
}

export interface TrendResult {
  scenario: string;
  metric: MetricName;
  excluded_failed_gates: number;
  points: TrendPoint[];
}

/** Discharge samples for a run (battery_samples table). Not yet exposed by the API. */
export interface BatterySample {
  t_s: number;
  discharge_mw: number;
  remaining_mwh: number;
}

export interface CompareParams {
  scenario: string;
  a: string;
  b: string;
  metric?: MetricName | "all";
  threshold_pct?: number;
}

export interface WtbaApi {
  scenarios(signal?: AbortSignal): Promise<Scenario[]>;
  runs(
    params: { scenario?: string; condition?: "A" | "B"; limit?: number },
    signal?: AbortSignal,
  ): Promise<RunPage>;
  run(id: string, signal?: AbortSignal): Promise<Run>;
  processes(id: string, sort: ProcessSort, signal?: AbortSignal): Promise<ProcessRow[]>;
  compare(params: CompareParams, signal?: AbortSignal): Promise<CompareResult>;
  trend(
    params: { scenario: string; metric: MetricName; condition?: "A" | "B" },
    signal?: AbortSignal,
  ): Promise<TrendResult>;
  /** Resolves to null when the API does not provide samples. */
  samples(id: string, signal?: AbortSignal): Promise<BatterySample[] | null>;
}
