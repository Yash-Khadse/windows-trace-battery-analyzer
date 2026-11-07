/**
 * Fake API responses (deterministic) for tests and `VITE_USE_MOCKS=true`.
 * Shapes follow api/src/wtba_api/schemas.py. All data is synthetic.
 */
import type {
  BatterySample,
  CompareResult,
  MetricComparison,
  MetricName,
  ProcessComparison,
  ProcessRow,
  Run,
  Scenario,
} from "../api/types";

export const SCENARIO_REGRESSION = "idle-browser";
export const SCENARIO_CLEAN = "video-playback";
export const SCENARIO_SMALL = "file-indexing";

function rng(seed: number): () => number {
  let s = seed >>> 0;
  return () => {
    s = (s + 0x6d2b79f5) >>> 0;
    let t = s;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

interface CmpIn {
  nA: number;
  nB: number;
  medA: number;
  shift: number;
  half: number;
  p: number;
  q: number;
  delta: number;
  regression?: boolean;
  newActivity?: boolean;
  reason?: string;
  warnings?: string[];
}

export function cmp(i: CmpIn): MetricComparison {
  const insufficient = i.nA < 5 || i.nB < 5;
  return {
    n_a: i.nA,
    n_b: i.nB,
    median_a: i.medA,
    median_b: i.medA + i.shift,
    hl_shift: i.shift,
    moses_ci_low: i.shift - i.half,
    moses_ci_high: i.shift + i.half,
    boot_ci_low: i.shift - i.half * 0.85,
    boot_ci_high: i.shift + i.half * 0.85,
    pct_change: i.medA === 0 ? null : (i.shift / i.medA) * 100,
    p_value: i.p,
    q_value: i.q,
    cliffs_delta: i.delta,
    regression: !insufficient && (i.regression ?? false),
    new_activity: i.newActivity ?? false,
    reason:
      i.reason ??
      (insufficient
        ? "refusing to flag: fewer than 5 runs in a group"
        : i.regression
          ? "shift exceeds threshold, CI excludes 0, q < 0.05, direction is worse"
          : "no flag criteria met"),
    warnings:
      i.warnings ??
      (insufficient
        ? ["N < 5 per condition: result is not reliable"]
        : i.nA < 8 || i.nB < 8
          ? ["N < 8 per condition: low statistical power"]
          : []),
  };
}

const PROC_NAMES = [
  "msedge.exe", "chrome.exe", "System", "dwm.exe", "svchost.exe", "explorer.exe",
  "MsMpEng.exe", "SearchIndexer.exe", "csrss.exe", "audiodg.exe", "RuntimeBroker.exe",
  "Teams.exe", "OneDrive.exe", "taskhostw.exe", "ctfmon.exe", "WmiPrvSE.exe",
  "sihost.exe", "fontdrvhost.exe", "lsass.exe", "services.exe", "Widgets.exe",
  "ShellExperienceHost.exe", "SecurityHealthService.exe", "spoolsv.exe", "conhost.exe",
  "Code.exe", "NVDisplay.Container.exe",
]; // prettier-ignore

interface ProcPlan {
  flagged: Set<string>;
  newActivity: Set<string>;
  nA: number;
  nB: number;
  seed: number;
  scale: number;
}

function procComparisons(plan: ProcPlan): ProcessComparison[] {
  const r = rng(plan.seed);
  const out: ProcessComparison[] = PROC_NAMES.slice(0, 25).map((name, idx) => {
    const medA = Math.round((3000 / (idx + 1.5)) * plan.scale * (0.7 + r() * 0.6)) + 5;
    const flagged = plan.flagged.has(name);
    const isNew = plan.newActivity.has(name);
    const shift = isNew
      ? medA * 0.9
      : flagged
        ? medA * (0.12 + r() * 0.25)
        : medA * (r() - 0.5) * 0.06;
    const half = Math.max(medA * 0.04, Math.abs(shift) * (flagged || isNew ? 0.45 : 1.6));
    return {
      process: name,
      comparison: cmp({
        nA: plan.nA,
        nB: plan.nB,
        medA: isNew ? 0 : medA,
        shift,
        half,
        p: flagged || isNew ? 0.0004 + r() * 0.004 : 0.15 + r() * 0.8,
        q: flagged || isNew ? 0.003 + r() * 0.02 : 0.3 + r() * 0.69,
        delta: flagged || isNew ? 0.6 + r() * 0.35 : (r() - 0.5) * 0.4,
        regression: flagged,
        newActivity: isNew,
        reason: isNew ? "process absent in A, active in B" : undefined,
      }),
    };
  });
  return out;
}

function compareFor(
  scenario: string,
  a: string,
  b: string,
  o: {
    nA: number;
    nB: number;
    metrics: Record<MetricName, CmpIn>;
    flagged: string[];
    newActivity: string[];
    excluded: { a: number; b: number };
    seed: number;
  },
): CompareResult {
  const plan = (kind: number): ProcPlan => ({
    flagged: new Set(o.flagged),
    newActivity: new Set(o.newActivity),
    nA: o.nA,
    nB: o.nB,
    seed: o.seed + kind,
    scale: kind === 0 ? 1 : 0.4,
  });
  return {
    scenario,
    a,
    b,
    threshold_pct: 5,
    include_warmup: false,
    include_failed_gates: false,
    excluded_failed_gates: o.excluded,
    runs_a: o.nA,
    runs_b: o.nB,
    metrics: {
      mean_discharge_mw: cmp(o.metrics.mean_discharge_mw),
      total_cpu_ms: cmp(o.metrics.total_cpu_ms),
      total_wakeups: cmp(o.metrics.total_wakeups),
    },
    per_process: {
      cpu_ms: procComparisons(plan(0)),
      wakeups: procComparisons(plan(1)),
    },
  };
}

export function compareFixture(scenario: string, a: string, b: string): CompareResult | null {
  if (scenario === SCENARIO_REGRESSION) {
    return compareFor(scenario, a, b, {
      nA: 10,
      nB: 10,
      seed: 11,
      flagged: ["msedge.exe", "dwm.exe", "MsMpEng.exe"],
      newActivity: ["Widgets.exe"],
      excluded: { a: 0, b: 1 },
      metrics: {
        mean_discharge_mw: { nA: 10, nB: 10, medA: 4210, shift: 690, half: 260, p: 0.0004, q: 0.0012, delta: 0.82, regression: true },
        total_cpu_ms: { nA: 10, nB: 10, medA: 18400, shift: 3050, half: 1400, p: 0.0011, q: 0.0016, delta: 0.74, regression: true },
        total_wakeups: { nA: 10, nB: 10, medA: 5230, shift: 880, half: 520, p: 0.0035, q: 0.0035, delta: 0.64, regression: true },
      },
    }); // prettier-ignore
  }
  if (scenario === SCENARIO_CLEAN) {
    return compareFor(scenario, a, b, {
      nA: 10,
      nB: 10,
      seed: 23,
      flagged: [],
      newActivity: [],
      excluded: { a: 0, b: 0 },
      metrics: {
        mean_discharge_mw: { nA: 10, nB: 10, medA: 8190, shift: 40, half: 310, p: 0.62, q: 0.62, delta: 0.1 },
        total_cpu_ms: { nA: 10, nB: 10, medA: 61200, shift: -900, half: 2600, p: 0.41, q: 0.62, delta: -0.16 },
        total_wakeups: { nA: 10, nB: 10, medA: 9800, shift: 120, half: 640, p: 0.55, q: 0.62, delta: 0.12 },
      },
    }); // prettier-ignore
  }
  if (scenario === SCENARIO_SMALL) {
    return compareFor(scenario, a, b, {
      nA: 4,
      nB: 4,
      seed: 31,
      flagged: ["SearchIndexer.exe"],
      newActivity: [],
      excluded: { a: 1, b: 0 },
      metrics: {
        mean_discharge_mw: { nA: 4, nB: 4, medA: 5600, shift: 700, half: 900, p: 0.057, q: 0.057, delta: 0.75, regression: true },
        total_cpu_ms: { nA: 4, nB: 4, medA: 30100, shift: 4800, half: 6100, p: 0.057, q: 0.057, delta: 0.75 },
        total_wakeups: { nA: 4, nB: 4, medA: 7000, shift: 300, half: 1300, p: 0.34, q: 0.34, delta: 0.25 },
      },
    }); // prettier-ignore
  }
  return null;
}

export const SCENARIOS: Scenario[] = [];
export const RUNS: Run[] = [];

function hex(r: () => number, n: number): string {
  let s = "";
  for (let i = 0; i < n; i++) s += Math.floor(r() * 16).toString(16);
  return s;
}

function uuid(r: () => number): string {
  return `${hex(r, 8)}-${hex(r, 4)}-4${hex(r, 3)}-a${hex(r, 3)}-${hex(r, 12)}`;
}

function buildRuns(
  scenario: string,
  nPerCond: number,
  base: { mw: number; cpu: number; wk: number },
  bump: number,
  seed: number,
  startDay: number,
): void {
  const r = rng(seed);
  const times: Run[] = [];
  for (let i = 0; i < nPerCond * 2; i++) {
    const cond = i % 2 === 0 ? "A" : "B";
    const k = cond === "B" ? 1 + bump : 1;
    const j = () => 1 + (r() - 0.5) * 0.08;
    const when = new Date(Date.UTC(2026, 9, startDay, 8, 0, 0) + i * 5_400_000);
    const gates = !(scenario === SCENARIO_REGRESSION && i === 13);
    times.push({
      run_id: uuid(r),
      scenario,
      machine_id_hash: "3f9a1c7be204d5a8",
      condition: cond,
      label: `${cond === "A" ? "baseline" : "candidate"}-${String(Math.floor(i / 2) + 1).padStart(2, "0")}`,
      started_utc: when.toISOString(),
      duration_s: 300,
      on_ac: false,
      battery_saver: false,
      plan_guid: "381b4222-f694-41f0-9685-ff5bb260df2e",
      start_pct: 92 - Math.floor(i / 2),
      end_pct: 91 - Math.floor(i / 2),
      dropped_events: gates ? Math.floor(r() * 3) : 412,
      sampler_gaps_s: gates ? 0 : 14.5,
      gates_passed: gates,
      mean_discharge_mw: Math.round(base.mw * k * j()),
      total_cpu_ms: Math.round(base.cpu * k * j()),
      total_wakeups: Math.round(base.wk * k * j()),
    });
  }
  RUNS.push(...times);
  SCENARIOS.push({
    name: scenario,
    run_count: times.length,
    first_started_utc: times[0]?.started_utc ?? null,
    last_started_utc: times[times.length - 1]?.started_utc ?? null,
  });
}

buildRuns(SCENARIO_REGRESSION, 10, { mw: 4210, cpu: 18400, wk: 5230 }, 0.16, 101, 12);
buildRuns(SCENARIO_CLEAN, 10, { mw: 8190, cpu: 61200, wk: 9800 }, 0.004, 202, 14);
buildRuns(SCENARIO_SMALL, 4, { mw: 5600, cpu: 30100, wk: 7000 }, 0.12, 303, 18);

export function processesFor(runId: string): ProcessRow[] {
  const r = rng(
    [...runId].reduce((a, c) => (a * 31 + c.charCodeAt(0)) >>> 0, 7),
  );
  return PROC_NAMES.slice(0, 20).map((process, idx) => ({
    process,
    cpu_ms: Math.round((2400 / (idx + 1.3)) * (0.8 + r() * 0.4) * 10) / 10,
    context_switches: Math.round((9000 / (idx + 1.1)) * (0.8 + r() * 0.4)),
    wakeups: Math.round((700 / (idx + 1.2)) * (0.8 + r() * 0.4)),
    wakeups_per_s: Math.round((2.3 / (idx + 1.2)) * (0.8 + r() * 0.4) * 100) / 100,
  }));
}

export function samplesFor(run: Run): BatterySample[] {
  const r = rng([...run.run_id].reduce((a, c) => (a * 31 + c.charCodeAt(0)) >>> 0, 3));
  const base = run.mean_discharge_mw ?? 4000;
  const out: BatterySample[] = [];
  for (let t = 0; t <= run.duration_s; t += 5) {
    const burst = t % 90 < 10 ? 1.25 : 1;
    const mw = Math.round(base * burst * (0.93 + r() * 0.14));
    out.push({ t_s: t, discharge_mw: mw, remaining_mwh: Math.round(40000 - (mw * t) / 3600) });
  }
  return out;
}
