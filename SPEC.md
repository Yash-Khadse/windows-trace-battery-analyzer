# SPEC: Windows Trace & Battery Regression Analyzer (WTBA)

Status: DRAFT v0.1, awaiting owner review (checkpoint at end of Phase 0).

## 1. Purpose
Record performance and power traces on a Windows PC, load them into a database, and state with statistical confidence whether condition B (a build, or a setting such as Battery Saver) is slower or drains more battery than condition A.

Primary deliverable: one real, measured finding (first experiment: **Battery Saver on vs off**).

## 2. Non-goals
- No modification of system settings (power plans, registry, services, security). The tool observes only.
- No boot-time or always-on tracing, no autologgers.
- No upload of trace data to any cloud service. LLM triage is optional, off by default, aggregated stats only.
- Not a replacement for WPA; WPA is used to spot-check numbers.

## 3. Safety requirements (hard, testable)
| ID | Requirement | Test |
|---|---|---|
| S1 | Writes only under the project directory (and the harness-owned temp dir for the Edge profile) | Unit test on path resolver rejects paths outside allowed roots |
| S2 | Only ETW sessions named `WTBA-*` are started, stopped or cleaned | Test with fake session list; `cleanup` never targets other names |
| S3 | Session is stopped in `finally`, on Ctrl+C, and by a max-duration watchdog | Simulated failure test + manual `logman query -ets` check |
| S4 | Refuse to start if: not elevated, free disk < 5 GB, battery < start threshold (default 80%) on a battery scenario, Battery Saver state differs from declared condition | `status` command tests |
| S5 | Abort run if battery <= 25% | Simulated battery feed test |
| S6 | No power setting is ever written; Battery Saver is toggled by the human | Code review + grep gate in CI for `setdcvalueindex`, `setactive`, registry writes |
| S7 | Edge workload uses a throwaway `--user-data-dir`; kill only harness-started PID | Unit test on launch args; no `taskkill /im` in code (CI grep) |
| S8 | `.etl`, raw exports, `.env`, `data/` never committed | `.gitignore` + CI guard + gitleaks |
| S9 | Process command lines and user paths are dropped or hashed at ingest; machine id is a salted hash | Scrubber tests on synthetic PII |
| S10 | DB is an isolated Docker container on 127.0.0.1:5433; API binds 127.0.0.1 and uses a read-only DB role | Compose file review + API test |

## 4. Scenarios
| Scenario | Duration | Workload | Conditions compared |
|---|---|---|---|
| `idle_10m` | 10 min | Desktop idle, screen on at fixed brightness, all other apps closed | Battery Saver off (A) vs on (B) |
| `edge_video_5m` | 5 min | Edge (throwaway profile) plays a local looping video file at fixed volume | Battery Saver off (A) vs on (B) |

Preconditions per run: on battery (not AC), start charge >= 80%, fixed brightness, no charger change mid-run, Wi-Fi state constant, warm-up run discarded. Run order is randomized and interleaved (e.g. ABBA BAAB ...), N = 10 per condition after warm-up.

## 5. Collector (`Wtba.Collector`, C# .NET 8)
Verbs:
- `status`: read-only environment check (elevation, disk, battery %, AC/DC, Battery Saver flag, existing `WTBA-*` sessions).
- `run --scenario <yaml> --condition <A|B> --label <text>`: start session, run workload, sample battery, stop, write ETL under `data/runs/<runid>/`, then `extract`.
- `extract <etl>`: aggregate ETL into a **run bundle** (section 6).
- `simulate`: generate a synthetic bundle without ETW (for tests, CI, dry runs).
- `cleanup`: stop only `WTBA-*` sessions.

ETW providers (kernel session via TraceEvent): Process, Profile (CPU sampling), ContextSwitch, ReadyThread, plus Microsoft-Windows-Kernel-Power / Processor-Power where available. Battery sampler: WMI `root\wmi` `BatteryStatus` (`DischargeRate` mW, `RemainingCapacity` mWh) every 5 s, plus `GetSystemPowerStatus`. Cross-check outputs: `powercfg /batteryreport /output ... /xml`, `typeperf` counters (`\Processor Information(_Total)\% Processor Utility`).

## 6. Run bundle (collector -> pipeline contract)
`data/runs/<runid>/bundle.json` (schema version 1):
```json
{
  "schema": 1,
  "run_id": "uuid",
  "scenario": "idle_10m",
  "condition": "A",
  "label": "battery-saver-off",
  "started_utc": "ISO-8601",
  "duration_s": 600.0,
  "machine": {"id_hash": "...", "os_build": "10.0.26200", "cpu": "...", "logical_cpus": 16},
  "power": {"on_ac": false, "battery_saver": false, "plan_guid": "...", "start_pct": 87, "end_pct": 86},
  "quality": {"dropped_events": 0, "sampler_gaps_s": 0, "gates_passed": true},
  "processes": [
    {"name": "msedge.exe", "pid": 1234, "cpu_samples": 5821, "cpu_ms": 5821.0,
     "context_switches": 40210, "wakeups": 9100}
  ],
  "battery_samples": [{"t_s": 0.0, "discharge_mw": 7200, "remaining_mwh": 41000}]
}
```
`cpu_ms = cpu_samples x sample_interval_ms`. Process identity = image name (pid kept for within-run joins, hashed or dropped at ingest). No command lines in the bundle.

## 7. Metric definitions (to be validated against WPA in the pilot)
- **CPU time (ms)** per process = Profile sample count x interval.
- **Context switches** per process = ContextSwitch events where the process's thread is switched in.
- **Wakeups** per process = context switches where the outgoing thread on that CPU is the Idle thread (CPU leaving idle), attributed to the incoming process. Reported as total and per second.
- **Mean discharge rate (mW)** = mean of `discharge_mw` samples over the run (excluding first 10 s settle).
- **Energy used (mWh)** = start `remaining_mwh` minus end `remaining_mwh` (coarse; cross-check only).

## 8. Database (PostgreSQL 16, Alembic migrations)
Tables: `scenarios`, `machines`, `runs` (run_id, scenario, condition, label, started_utc, duration_s, power state, quality flags, mean_discharge_mw, total_cpu_ms, total_wakeups), `process_cpu` (run_id, process, cpu_ms, context_switches), `wakeups` (run_id, process, wakeups, wakeups_per_s), `battery_samples` (run_id, t_s, discharge_mw, remaining_mwh). Primary/foreign keys, indexes on (scenario, condition, started_utc), and CHECK constraints on non-negative values. Roles: `wtba_rw` (pipeline), `wtba_ro` (API).

## 9. Statistics
For each metric (mean discharge mW, total CPU ms, total wakeups) and for each process present in >= 50% of runs:
- Mann-Whitney U, two-sided (`scipy.stats.mannwhitneyu`, exact where feasible).
- Effect size: Cliff's delta.
- Shift estimate: Hodges-Lehmann median difference, with bootstrap 95% CI (10,000 resamples, seeded) and percent change versus A's median.
- Multiple comparisons across processes: Benjamini-Hochberg q-values.
- **Regression flag** = |percent change| >= threshold (default 5%) AND 95% CI excludes 0 AND q < 0.05 AND direction is "worse" for the metric.
- Guards: warn if N < 8 per condition; refuse to flag if N < 5; every output states N, p, q, delta, CI.

## 10. API (FastAPI, 127.0.0.1 only, read-only DB role)
`GET /scenarios`, `GET /runs?scenario&condition`, `GET /compare?scenario&a&b&metric`, `GET /trend?scenario&metric`, `GET /runs/{id}/processes`. Pydantic v2 response models, OpenAPI docs, CORS limited to the local Vite origin.

## 11. Dashboard (React + Vite + TypeScript)
Views: **Compare** (forest plot of effect + CI, table with N/p/q, top processes by CPU and wakeups), **Run detail** (process table, discharge-rate chart), **Trend** (metric across runs/conditions over time). Accessible, light/dark, responsive.

## 12. CI (GitHub Actions)
`windows-latest`: dotnet build + test. `ubuntu-latest`: ruff, mypy, pytest (Postgres service), `pip-audit`; web: lint, vitest, build, `npm audit`; plus gitleaks, `dotnet list package --vulnerable`, guard against committed `*.etl` / files > 1 MB, grep gate for banned calls (S6, S7).

## 13. Acceptance criteria
1. `simulate` -> ingest -> compare works end to end with no ETW and no elevation.
2. Pilot 30 s idle trace: CPU and wakeup numbers agree with WPA within a documented tolerance (target 5%), or the definitions are corrected and documented.
3. Stats pass known-answer and null-experiment (about 5% false positives) tests; an injected 10% shift is flagged.
4. Real experiment (N = 10 per condition, both scenarios) is loaded and shown in the dashboard; README numbers match the DB.
5. No leftover `WTBA-*` sessions after normal exit, Ctrl+C or forced failure.
6. Repo audit passes (no traces, secrets, user paths) before any push.

## 14. Open items for owner
- Confirm the metric definitions (wakeups in particular) after the pilot.
- Confirm default regression threshold (5%) and N = 10.
- Confirm whether to enable the optional LLM triage hint (default: no).
