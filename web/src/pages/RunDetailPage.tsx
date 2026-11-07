import { useState } from "react";
import { Link, useParams } from "react-router-dom";
import { useApi } from "../api/context";
import type { ProcessRow, ProcessSort, Run } from "../api/types";
import { DischargeChart } from "../components/DischargeChart";
import { EmptyState, ErrorState, Skeleton } from "../components/States";
import { SortableTable, type Column } from "../components/SortableTable";
import { fmtDateTime, fmtDuration, fmtNum } from "../lib/format";
import { useAsync } from "../lib/useAsync";

function Facts({ run }: { run: Run }) {
  const flags: string[] = [];
  if (!run.gates_passed) flags.push("Failed quality gates");
  if (run.dropped_events > 0) flags.push(`${run.dropped_events} dropped events`);
  if (run.sampler_gaps_s > 0) flags.push(`${fmtNum(run.sampler_gaps_s, 1)} s sampler gaps`);
  if (run.battery_saver) flags.push("Battery saver on");
  const items: [string, string][] = [
    ["Scenario", run.scenario],
    ["Condition", run.condition],
    ["Label", run.label],
    ["Started", fmtDateTime(run.started_utc)],
    ["Duration", fmtDuration(run.duration_s)],
    ["Power", run.on_ac ? "On AC" : "On battery"],
    ["Battery", `${run.start_pct}% to ${run.end_pct}%`],
    ["Power plan", run.plan_guid],
    ["Mean discharge", run.mean_discharge_mw === null ? "—" : `${fmtNum(run.mean_discharge_mw, 0)} mW`],
    ["Total CPU", `${fmtNum(run.total_cpu_ms, 0)} ms`],
    ["Total wakeups", fmtNum(run.total_wakeups, 0)],
  ];
  return (
    <section className="panel" aria-labelledby="h-facts">
      <h2 id="h-facts">Run facts</h2>
      <dl className="facts">
        {items.map(([k, v]) => (
          <div key={k}>
            <dt>{k}</dt>
            <dd className={k === "Power plan" ? "mono" : undefined}>{v}</dd>
          </div>
        ))}
      </dl>
      <div style={{ marginTop: "1rem" }}>
        <h3>Quality flags</h3>
        {flags.length === 0 ? (
          <p className="muted" style={{ margin: "0.25rem 0 0" }}>
            None. All quality gates passed.
          </p>
        ) : (
          <div className="notice notice-warn" style={{ marginTop: "0.5rem" }}>
            <ul style={{ margin: 0 }}>
              {flags.map((f) => (
                <li key={f}>{f}</li>
              ))}
            </ul>
          </div>
        )}
      </div>
    </section>
  );
}

const SORTS: { key: ProcessSort; label: string }[] = [
  { key: "cpu_ms", label: "CPU ms" },
  { key: "wakeups", label: "Wakeups" },
  { key: "context_switches", label: "Context switches" },
];

const COLS: Column<ProcessRow>[] = [
  { key: "process", header: "Process", className: "proc", render: (r) => r.process, sortValue: (r) => r.process },
  { key: "cpu_ms", header: "CPU (ms)", numeric: true, render: (r) => fmtNum(r.cpu_ms, 1), sortValue: (r) => r.cpu_ms },
  { key: "wakeups", header: "Wakeups", numeric: true, render: (r) => fmtNum(r.wakeups, 0), sortValue: (r) => r.wakeups },
  { key: "wps", header: "Wakeups/s", numeric: true, render: (r) => fmtNum(r.wakeups_per_s, 2), sortValue: (r) => r.wakeups_per_s },
  { key: "cs", header: "Context switches", numeric: true, render: (r) => fmtNum(r.context_switches, 0), sortValue: (r) => r.context_switches },
]; // prettier-ignore

function Processes({ id }: { id: string }) {
  const api = useApi();
  const [sort, setSort] = useState<ProcessSort>("cpu_ms");
  const res = useAsync((s) => api.processes(id, sort, s), [api, id, sort]);
  return (
    <section className="panel" aria-labelledby="h-proc">
      <div className="panel-head">
        <h2 id="h-proc">Top processes</h2>
        <div className="seg" role="group" aria-label="Server-side ranking">
          {SORTS.map((s) => (
            <button key={s.key} type="button" aria-pressed={sort === s.key} onClick={() => setSort(s.key)}>
              {s.label}
            </button>
          ))}
        </div>
      </div>
      {res.status === "loading" && <Skeleton label="Loading processes" />}
      {res.status === "error" && <ErrorState error={res.error} onRetry={res.reload} />}
      {res.status === "ok" &&
        (res.data.length === 0 ? (
          <EmptyState title="No process data for this run" />
        ) : (
          <SortableTable
            key={sort}
            caption={`Top ${res.data.length} processes, ranked by ${SORTS.find((s) => s.key === sort)?.label}`}
            columns={COLS}
            rows={res.data}
            rowKey={(r) => r.process}
          />
        ))}
    </section>
  );
}

function Discharge({ id }: { id: string }) {
  const api = useApi();
  const res = useAsync((s) => api.samples(id, s), [api, id]);
  return (
    <section className="panel" aria-labelledby="h-dis">
      <h2 id="h-dis">Discharge rate over time</h2>
      {res.status === "loading" && <Skeleton label="Loading discharge samples" rows={1} />}
      {res.status === "error" && <ErrorState error={res.error} onRetry={res.reload} />}
      {res.status === "ok" &&
        (res.data === null || res.data.length === 0 ? (
          <p className="muted" role="status" style={{ margin: 0 }}>
            Battery samples are not available from the API for this run (no samples endpoint yet).
          </p>
        ) : (
          <DischargeChart samples={res.data} />
        ))}
    </section>
  );
}

export function RunDetailPage() {
  const { id = "" } = useParams();
  const api = useApi();
  const run = useAsync((s) => api.run(id, s), [api, id]);

  if (run.status === "loading") {
    return (
      <>
        <h1>Run</h1>
        <Skeleton label="Loading run" />
      </>
    );
  }
  if (run.status === "error") {
    return (
      <>
        <ErrorState error={run.error} onRetry={run.reload} />
        <p style={{ textAlign: "center" }}>
          <Link to="/">Back to Compare</Link>
        </p>
      </>
    );
  }
  return (
    <div className="stack">
      <div className="page-head">
        <h1>
          Run <span className="mono">{run.data.run_id.slice(0, 8)}</span>
        </h1>
        <Link to={`/trend?scenario=${encodeURIComponent(run.data.scenario)}`}>See scenario trend</Link>
      </div>
      <Facts run={run.data} />
      <Discharge id={id} />
      <Processes id={id} />
    </div>
  );
}
