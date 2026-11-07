# Windows Trace & Battery Regression Analyzer (WTBA)

WTBA records ETW CPU, context-switch and wakeup traces plus battery drain on a Windows PC, loads them into PostgreSQL, and uses non-parametric statistics to decide whether condition B (for example, Battery Saver on) is worse than condition A. It is observer-only: it never changes power settings, registry values or services.

> **Status: experiment not yet run.** The first real experiment (Battery Saver on vs off) has not been executed. See [Results](#results). Demo images below use synthetic data.

## Architecture

```mermaid
flowchart LR
    subgraph Windows PC
        C["Collector<br/>C# .NET 8 (ETW, battery sampler)"]
    end
    C -->|"data/runs/&lt;runid&gt;/bundle.json<br/>(schema v1)"| P["Pipeline<br/>Python: validate, scrub, load, stats"]
    P -->|"wtba ingest"| DB[("PostgreSQL 16<br/>Docker, 127.0.0.1:5433")]
    DB -->|"wtba_ro role"| A["API<br/>FastAPI, 127.0.0.1:8000"]
    A -->|"JSON, CORS to Vite origin"| W["Dashboard<br/>React + Vite + TypeScript<br/>127.0.0.1:5173"]
```

| Component | Folder | Role |
|---|---|---|
| Collector | `collector\` (`Wtba.Collector`, `Wtba.Collector.Tests`, `Wtba.sln`) | Runs the experiment, records ETW, samples battery, extracts the run bundle |
| Scenarios | `scenarios\` | YAML plans: duration, conditions, run counts, gates, seed |
| Pipeline | `pipeline\` (package `wtba`) | Bundle validation, PII scrubbing, DB load, statistics, `wtba` CLI |
| Database | `db\` | Isolated Docker Postgres 16, roles, Alembic migrations in `pipeline\migrations` |
| API | `api\` (package `wtba_api`) | Read-only FastAPI service over the database |
| Dashboard | `web\` | Compare, Run detail and Trend views |
| Contract | `docs\bundle.schema.json` | JSON schema for the run bundle |
| Guards | `scripts\ci\guard.sh` | Repository privacy and safety checks |

## Methodology

**Design.** Each scenario compares condition A and condition B on battery power. Run order is randomized and interleaved (for example ABBA BAAB), with a seed stored in the scenario file. One warm-up run per condition is discarded. The default is N = 10 measured runs per condition. Cooldown and settle periods separate runs.

**Quality gates.** A run is accepted only if it starts on battery at or above 80% charge, Battery Saver matches the declared condition, free disk is at least 5 GB, and the run does not drop below the 25% abort threshold. The bundle records `dropped_events`, `sampler_gaps_s` and `gates_passed`.

**Metrics** (definitions from SPEC section 7):
- CPU time (ms) per process = Profile sample count x sample interval.
- Context switches per process = ContextSwitch events where the process's thread is switched in.
- Wakeups per process = context switches where the outgoing thread on that CPU is the Idle thread, attributed to the incoming process. Reported as total and per second.
- Mean discharge rate (mW) = mean of `discharge_mw` samples over the run, excluding the first 10 s.
- Energy used (mWh) = start `remaining_mwh` minus end `remaining_mwh`. Coarse; cross-check only.

**Statistics** (per metric: mean discharge mW, total CPU ms, total wakeups; and per process present in at least 50% of runs):
- Mann-Whitney U, two-sided. Exact distribution when there are no ties.
- Effect size: Cliff's delta.
- Shift estimate: Hodges-Lehmann median difference with a distribution-free Moses 95% CI (headline). A seeded percentile bootstrap CI (10,000 resamples) is reported as a secondary check, together with percent change versus A's median.
- Multiple comparisons across processes: Benjamini-Hochberg q-values.
- **Regression flag** = |percent change| >= threshold (default 5%) AND 95% CI excludes 0 AND q < 0.05 AND the direction is "worse" for the metric.
- Guards: a warning is shown if N < 8 per condition; no flag is raised if N < 5. Every output states N, p, q, delta and CI.

**Validation so far.**
- CPU sample counts agree with Windows Performance Analyzer (WPA) within about 0.4 to 2.8 percent on a 30 second pilot trace, for the top 14 processes, with the same ranking.
- Wakeups have **not yet** been validated against WPA.

## Safety and privacy model

Derived from SPEC section 3.

- **Observer-only.** No power setting is written (no `setdcvalueindex`, `setactive`, or registry writes). Battery Saver is toggled by the person, by hand.
- **Admin only for the collector.** Verbs that start ETW sessions require an elevated terminal. Read-only checks do not.
- **Named sessions with guaranteed stop.** Only ETW sessions named `WTBA-*` are started, stopped or cleaned. Sessions are stopped in `finally`, on Ctrl+C, and by a max-duration watchdog.
- **Start and abort guards.** Runs refuse to start below 80% battery on a battery scenario, on AC power, with less than 5 GB free disk, or if Battery Saver differs from the declared condition. A run aborts at 25% battery or below.
- **Throwaway Edge profile.** The Edge workload uses a temporary `--user-data-dir` and kills only the process the harness started.
- **Writes confined to the project.** The path resolver rejects paths outside the project directory and the harness temp directory.
- **Traces never committed.** `.etl`, raw exports, `.env` and `data\` are gitignored. The repository guard and gitleaks run in CI.
- **Scrubbing at ingest.** User paths are removed, command lines are not stored, and the machine id is a salted hash (`WTBA_HASH_SALT`).
- **Isolated database.** Postgres runs in a dedicated Docker container bound to `127.0.0.1:5433`.
- **Read-only API.** The API binds to `127.0.0.1` and connects with the read-only role `wtba_ro`.
- **No cloud upload.** LLM triage is optional, off by default, and would receive aggregated statistics only.

## Quick start

### Prerequisites

- .NET 8 SDK
- Python 3.11 and [uv](https://docs.astral.sh/uv/)
- Node.js 20 and npm
- Docker Desktop (or Docker Engine with the Compose plugin)
- Windows Performance Analyzer (optional, for spot checks)
- Windows laptop on battery power for real runs

### Steps

Commands are PowerShell. Run from the repository root `D:\WorkSpace\ME\WTBA` unless stated otherwise.

1. **Create the local environment file.** Copy `.env.example` to `.env` and replace every `change-me` value with a strong random one. Never commit `.env`. The database uses three roles: a bootstrap admin (`WTBA_DB_ADMIN_*`, used only to initialize the container), a read-write app role (`WTBA_DB_USER`, runs migrations and ingest) and a read-only role (`WTBA_DB_RO_USER`, used by the API). `WTBA_HASH_SALT` must be random and at least 16 characters; placeholder values are rejected at ingest.

   ```powershell
   Copy-Item .env.example .env
   ```

2. **Start the database.** Run from `db\`. The compose file reads `WTBA_DB_PASSWORD` and `WTBA_DB_RO_PASSWORD` from the environment file.

   ```powershell
   cd db
   docker compose --env-file ..\.env up -d
   cd ..
   ```

3. **Apply migrations.** The pipeline reads the database URL from `WTBA_DATABASE_URL`.

   ```powershell
   cd pipeline
   uv sync --frozen
   $env:WTBA_DATABASE_URL = "postgresql+psycopg://wtba_rw:<WTBA_DB_PASSWORD>@127.0.0.1:5433/wtba"
   uv run alembic upgrade head
   cd ..
   ```

4. **Build the collector.** Warnings are errors in CI.

   ```powershell
   dotnet build collector\Wtba.sln -c Release
   ```

5. **Check the environment (no elevation needed).** Reports elevation, free disk, battery, power source, Battery Saver state and active `WTBA-*` sessions.

   ```powershell
   dotnet run --project collector\Wtba.Collector -- status
   ```

6. **Try a synthetic run (no ETW, no elevation).** Writes `data\runs\<runid>\bundle.json`.

   ```powershell
   dotnet run --project collector\Wtba.Collector -- simulate --scenario idle_10m --condition A --seed 1
   ```

7. **Dry-run an experiment plan (no ETW, no elevation).** Prints the planned slots without capturing anything.

   ```powershell
   dotnet run --project collector\Wtba.Collector -- experiment --plan scenarios\rehearsal_idle_30s.yaml --dry-run
   ```

8. **Run the real experiment (elevated terminal required, real battery).** Open PowerShell as Administrator. The collector asks you to set Battery Saver by hand before each slot and to type `yes` to confirm. Ctrl+C stops safely.

   Run the DLL built in step 4 rather than `dotnet run`, so nothing is compiled as Administrator and no admin-owned build files appear in the project:

   ```powershell
   $dll = 'collector\Wtba.Collector\bin\Release\net8.0\Wtba.Collector.dll'
   dotnet $dll status
   dotnet $dll experiment --plan scenarios\idle_10m.yaml
   ```

   Use `--resume <id>` to continue an interrupted experiment. Use `cleanup` (elevated) to stop any leftover `WTBA-*` sessions.

   **Trace files are cleaned up automatically.** A 10-minute capture is roughly 440 MB. After each run is extracted and its bundle validated, the experiment keeps only the newest N traces (default 2, set with `etl_retention` in the plan) for spot-checks in Windows Performance Analyzer and deletes older ones. An ETL is deleted only if its `bundle.json` is valid, it sits at `data\runs\<run-id>\<run-id>.etl` with no symlinks or junctions on the path, and its run was not rejected or interrupted. To review or clean manually (dry run by default, no elevation needed):

   ```powershell
   dotnet $dll etl-clean                 # list what would be deleted
   dotnet $dll etl-clean --apply         # delete after a typed 'yes'
   ```

   Energy saver (Battery Saver) detection reads the registry value `HKLM\SYSTEM\CurrentControlSet\Control\Power\EnergySaverState` (read-only). On the Windows 11 25H2 test machine the documented API flag did not change when the toggle did, so this value is used (1 = on, 2 = off). Run `status` once with the toggle on and once off to confirm it on your machine.

9. **Ingest bundles (no elevation needed).** Each run bundle is validated, scrubbed and loaded. Runs already in the database are skipped.

   ```powershell
   cd pipeline
   uv run wtba ingest ..\data\runs\<runid>\bundle.json
   cd ..
   ```

   Add `--dry-run` to validate and print row counts without a database.

   **For a whole experiment, use the manifest-driven helper instead of ingesting `data\runs\*`.** Warm-up runs are flagged only in the experiment manifest, not in their bundles, so a blind ingest would count them as real data. The helper loads only completed, non-warm-up, non-rejected slots (safe to re-run):

   ```powershell
   .\scripts\ingest-experiment.ps1 -ExperimentId exp-YYYYMMDD-HHMMSS-xxxxxx -DryRun   # preview
   .\scripts\ingest-experiment.ps1 -ExperimentId exp-YYYYMMDD-HHMMSS-xxxxxx
   ```

10. **Run the API (localhost only).** It uses the read-only role.

    ```powershell
    cd api
    uv sync --frozen
    $env:WTBA_API_DATABASE_URL = "postgresql+psycopg://wtba_ro:<WTBA_DB_RO_PASSWORD>@127.0.0.1:5433/wtba"
    uv run wtba-api
    cd ..
    ```

    The API listens on `127.0.0.1:8000` by default (`WTBA_API_PORT`). The API only accepts requests whose Host header is `localhost`, `127.0.0.1` or `[::1]` (extendable via `WTBA_API_ALLOWED_HOSTS`), and rejects any browser Origin not in `WTBA_API_CORS_ORIGINS`. `/compare` is capped at 200 runs per group. OpenAPI docs are served by FastAPI.

11. **Run the dashboard.** The Vite dev server on `127.0.0.1:5173` proxies API calls to port 8000.

    ```powershell
    cd web
    npm ci
    npm run dev
    ```

    Open `http://127.0.0.1:5173`.

### Elevation summary

| Step | Elevated terminal |
|---|---|
| `status`, `simulate`, `experiment --dry-run`, `extract`, `ingest`, API, dashboard | No |
| `experiment` (real run), `run`, `cleanup` | **Yes** |

## Results

> **Battery Saver result: PENDING.** The real experiment (Energy saver on vs off, N = 10 per condition, scenarios `idle_10m` and `edge_video_5m`) has not been run yet, so this README makes no claim about whether Energy saver helps or hurts. The sections below report only what has actually been measured so far: a validation of the collector against Windows Performance Analyzer, a check of the tool's own overhead, and a 4-run rehearsal that is far too small to conclude anything.

All numbers below come from one machine (an Intel Core i7-1360P laptop, 16 logical CPUs, Windows 11 25H2, on battery, no charger) and were measured with this tool. They are single short runs, not statistics.

### 1. Collector validation against Windows Performance Analyzer

One 30-second idle trace was opened in WPA (CPU Usage (Sampled), Utilization by Process) and compared with the collector's per-process CPU sample counts for the same `.etl` file.

| Check | Result |
|---|---|
| Processes compared | top 14 by CPU |
| Ranking | identical |
| Sample count, collector vs WPA | collector higher by +0.4% to +2.8% per process (systematic, about +1.5%) |
| Total non-idle CPU | 53.7 s (collector) vs about 53.4 s (WPA), within 0.5% |
| Unattributed samples | 364 of 53,661 before a thread-to-process fix, 10 after |

The first comparison exposed a real bug: events from processes that were already running when tracing started carried no process id, which made two processes read 8% and 18% low. They are now resolved through the thread map, and all 14 processes fall within +0.4% to +2.8%. Wakeups are **not** yet validated against WPA.

### 2. Does the battery sampler disturb the measurement?

The sampler polls WMI every 5 seconds, and the WMI host process (`WmiPrvSE.exe`) was the top CPU consumer at idle. To test whether the tool caused that, the same 30-second idle capture was repeated with the sampler on and off.

| Run | Sampler | Duration | `WmiPrvSE.exe` CPU time | per second |
|---|---|---|---|---|
| Pilot | on | 31.7 s | 13,968 ms | 441 ms/s |
| Repeat | on | 31.2 s | 13,855 ms | 444 ms/s |
| Repeat | off | 30.5 s | 13,003 ms | 426 ms/s |

The difference between sampler on and off is about 4%, inside the run-to-run noise (N = 1 per setting). The sampler is not what keeps the WMI host busy: something else on the test machine issues WMI queries continuously, using roughly 0.43 of a core while the machine is otherwise idle. This is an observation, not yet a root-caused finding.

### 3. Rehearsal: 4 runs of 30 s (N = 2 per condition)

This exercised the experiment harness end to end (randomized order, Energy saver toggled by hand and verified by the tool, quality gates). All 4 slots completed, 0 rejected. With N = 2 per group the statistics module correctly refuses to flag anything, and no conclusion should be drawn from these numbers.

| Slot | Condition | Energy saver | Duration | Total CPU time | Mean discharge |
|---|---|---|---|---|---|
| 1 | B | on | 32.9 s | 75,558 ms | 10,555 mW |
| 2 | A | off | 30.5 s | 59,464 ms | 10,519 mW |
| 3 | B | on | 30.7 s | 64,003 ms | 11,525 mW |
| 4 | A | off | 30.4 s | 52,314 ms | 13,523 mW |

Run-to-run variation is large (total CPU time 52 s to 76 s, mean discharge 10.5 W to 13.5 W), which is exactly why the real experiment uses N = 10 per condition, randomized interleaved order and a non-parametric test.

### Real experiment: result table (to be filled from the database)

<!-- fill only with measured numbers from the DB -->

| Scenario | Metric | N (A / B) | Median A | Median B | HL shift (B - A) | 95% CI (Moses) | Percent change | Cliff's delta | p | q | Regression flag |
|---|---|---|---|---|---|---|---|---|---|---|---|
| idle_10m | Mean discharge (mW) | | | | | | | | | | |
| idle_10m | Total CPU (ms) | | | | | | | | | | |
| idle_10m | Total wakeups | | | | | | | | | | |
| edge_video_5m | Mean discharge (mW) | | | | | | | | | | |
| edge_video_5m | Total CPU (ms) | | | | | | | | | | |
| edge_video_5m | Total wakeups | | | | | | | | | | |

### Demo screenshots (synthetic data, not real measurements)

These images show the dashboard layout using synthetic data. They are not measurements of any machine.

**Compare view, dark theme**

![Compare view in dark theme, synthetic data](docs/images/compare-dark.png)

**Compare view, light theme**

![Compare view in light theme, synthetic data](docs/images/compare-light.png)

**Compare view, mobile width**

![Compare view at mobile width, synthetic data](docs/images/compare-mobile.png)

**Trend view, dark theme**

![Trend view in dark theme, synthetic data](docs/images/trend-dark.png)

**Run detail view, dark theme**

![Run detail view in dark theme, synthetic data](docs/images/run-detail-dark.png)

## Testing and CI

### Local test suites

**Collector (.NET).** Uses fakes only. No ETW session is created.

```powershell
dotnet build collector\Wtba.sln -c Release
dotnet test collector\Wtba.sln -c Release
```

**Pipeline and API (Python).** Database tests run only when `WTBA_TEST_DATABASE_URL` is set and are skipped otherwise.

> **Warning:** the database tests call `drop_all` and remove every table in the target database. Point `WTBA_TEST_DATABASE_URL` only at a throwaway database, never at the database that holds real experiment data.

```powershell
$env:WTBA_TEST_DATABASE_URL = "postgresql+psycopg://wtba_ci:wtba_ci_password@127.0.0.1:5432/wtba_test"
$env:WTBA_HASH_SALT = "local-test-salt"
cd pipeline
uv sync --frozen
uv run ruff check .
uv run mypy src
uv run pytest -q
cd ..\api
uv sync --frozen
uv run ruff check .
uv run mypy src
uv run pytest -q
cd ..
```

**Dashboard (web).**

```powershell
cd web
npm ci
npm run lint
npm run typecheck
npm test
npm run build
cd ..
```

**Repository guard.** Run with bash (Git Bash on Windows works).

```bash
bash scripts/ci/guard.sh
```

### What CI runs

Defined in `.github/workflows/ci.yml`.

| Job | Runner | Steps |
|---|---|---|
| Repo guard and secret scan | ubuntu | `scripts/ci/guard.sh` (trace files, env files, files over 1 MB, banned system calls, user paths), gitleaks |
| Collector (.NET 8) | windows | `dotnet build -c Release -warnaserror`, `dotnet test --no-build`, vulnerable-package check |
| Pipeline and API | ubuntu with Postgres 16 service | `ruff`, `mypy src`, `pytest` for both packages; `pip-audit` on exported requirements |
| Dashboard | ubuntu, Node 20 | `npm ci --ignore-scripts`, lint, typecheck, test, build, `npm audit --omit=dev --audit-level=high` |

## Known limitations

- **Wakeups are not validated against WPA.** CPU sample counts are; wakeup definitions still need the pilot comparison.
- **Systematic sample-count difference.** There is about a 1.5% systematic difference in sample counts relative to WPA.
- **Battery readings are coarse.** Discharge and remaining-capacity values have limited resolution, so energy figures are cross-checks, not primary evidence.
- **Edge workload launcher is manual.** The Edge video workload is started by hand; the harness does not launch it yet.
- **Single machine.** Results describe one laptop and do not generalize to other hardware, firmware or Windows builds.
- **Battery Saver is toggled manually.** The person sets it before each slot, so human error is possible; the collector checks the state against the declared condition.
- **Observer effect tested once.** The battery sampler was tested for overhead once, with 30 s runs: the WMI host process used about 13.0 s of CPU without the sampler and about 13.7 s with it. No measurable difference was seen beyond that single test.

## Project layout

```text
WTBA\
  README.md                  this file
  SPEC.md                    specification (safety, metrics, statistics, acceptance)
  .env.example               environment template (copy to .env, never commit .env)
  .gitleaks.toml             secret scanner configuration
  collector\                 C# .NET 8 collector
    Wtba.sln
    Wtba.Collector\          CLI, ETW recorder, battery sampler, extractor, gates
    Wtba.Collector.Tests\    tests (fakes only)
  pipeline\                  Python package "wtba"
    src\wtba\                ingest, scrub, load, models, stats, cli
    migrations\              Alembic migrations
    tests\
  api\                       Python package "wtba_api" (FastAPI, read-only)
    src\wtba_api\
    tests\
  web\                       React + Vite + TypeScript dashboard
    src\
    scripts\
  db\
    docker-compose.yml       isolated Postgres 16 on 127.0.0.1:5433
    init\                    role creation for the read-only API role
  scenarios\                 experiment plans (idle_10m, edge_video_5m, rehearsal_idle_30s)
  docs\
    bundle.schema.json       run bundle contract
    images\                  dashboard screenshots (synthetic data)
  fixtures\                  small synthetic bundles for tests
  scripts\ci\guard.sh        repository privacy and safety guard
  .github\workflows\ci.yml   CI pipeline
  data\                      local traces and bundles (gitignored, never committed)
```

## License

MIT. See [LICENSE](LICENSE).

## Roadmap

- Edge workload launcher: start and stop the throwaway-profile Edge workload from the harness.
- Wakeup validation: compare wakeup counts against WPA on the pilot trace and correct the definitions if needed.
- Optional LLM triage hint: off by default; would receive aggregated statistics only, never raw traces.
