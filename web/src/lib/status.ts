import type { MetricComparison, MetricName } from "../api/types";

export type StatusKind = "regression" | "none" | "new_activity" | "insufficient";

export const MIN_N = 5;

export interface Status {
  kind: StatusKind;
  label: string;
  /** Short sentence for screen readers and tooltips. */
  description: string;
}

const LABELS: Record<StatusKind, string> = {
  regression: "Regression",
  none: "No regression",
  new_activity: "New activity",
  insufficient: "Insufficient data (N<5)",
};

export function statusOf(c: MetricComparison): Status {
  let kind: StatusKind;
  if (c.n_a < MIN_N || c.n_b < MIN_N) kind = "insufficient";
  else if (c.new_activity) kind = "new_activity";
  else if (c.regression) kind = "regression";
  else kind = "none";
  return { kind, label: LABELS[kind], description: c.reason };
}

export const METRIC_META: Record<
  MetricName,
  { label: string; unit: string; digits: number }
> = {
  mean_discharge_mw: { label: "Mean discharge", unit: "mW", digits: 0 },
  total_cpu_ms: { label: "Total CPU", unit: "ms", digits: 0 },
  total_wakeups: { label: "Total wakeups", unit: "wakeups", digits: 0 },
};
