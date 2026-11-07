"""Map a validated, scrubbed bundle to table rows and load them idempotently."""

from __future__ import annotations

import uuid
from dataclasses import dataclass, field
from typing import Any

from sqlalchemy.orm import Session

from .ingest import parse_utc
from .models import BatterySample, Machine, ProcessCpu, Run, Scenario, Wakeup

SETTLE_S = 10.0  # SPEC 7: exclude first 10 s from mean discharge


@dataclass
class BundleRows:
    scenario: Scenario
    machine: Machine
    run: Run
    process_cpu: list[ProcessCpu] = field(default_factory=list)
    wakeups: list[Wakeup] = field(default_factory=list)
    battery_samples: list[BatterySample] = field(default_factory=list)

    def counts(self) -> dict[str, int]:
        return {
            "scenarios": 1,
            "machines": 1,
            "runs": 1,
            "process_cpu": len(self.process_cpu),
            "wakeups": len(self.wakeups),
            "battery_samples": len(self.battery_samples),
        }


def mean_discharge_mw(samples: list[dict[str, Any]]) -> float | None:
    vals = [s["discharge_mw"] for s in samples if s["t_s"] >= SETTLE_S]
    return sum(vals) / len(vals) if vals else None


def _dedupe_samples(samples: list[dict[str, Any]]) -> list[dict[str, Any]]:
    seen: dict[float, dict[str, Any]] = {}
    for s in samples:
        seen[s["t_s"]] = s  # (run_id, t_s) is the PK; last one wins
    return list(seen.values())


def bundle_to_rows(bundle: dict[str, Any]) -> BundleRows:
    """Pure mapping (no DB). Processes sharing an image name are summed."""
    duration = float(bundle["duration_s"])
    agg: dict[str, dict[str, float]] = {}
    for p in bundle["processes"]:
        a = agg.setdefault(p["name"], {"cpu_ms": 0.0, "cs": 0, "wk": 0})
        a["cpu_ms"] += p["cpu_ms"]
        a["cs"] += p["context_switches"]
        a["wk"] += p["wakeups"]
    run_id = uuid.UUID(bundle["run_id"])
    m, pw, q = bundle["machine"], bundle["power"], bundle["quality"]
    run = Run(
        run_id=run_id,
        scenario=bundle["scenario"],
        machine_id_hash=m["id_hash"],
        condition=bundle["condition"],
        label=bundle["label"],
        started_utc=parse_utc(bundle["started_utc"]),
        duration_s=duration,
        on_ac=pw["on_ac"],
        battery_saver=pw["battery_saver"],
        plan_guid=pw["plan_guid"],
        start_pct=pw["start_pct"],
        end_pct=pw["end_pct"],
        dropped_events=q["dropped_events"],
        sampler_gaps_s=q["sampler_gaps_s"],
        gates_passed=q["gates_passed"],
        mean_discharge_mw=mean_discharge_mw(bundle["battery_samples"]),
        total_cpu_ms=sum(a["cpu_ms"] for a in agg.values()),
        total_wakeups=int(sum(a["wk"] for a in agg.values())),
    )
    return BundleRows(
        scenario=Scenario(name=bundle["scenario"]),
        machine=Machine(
            id_hash=m["id_hash"],
            os_build=m["os_build"],
            cpu=m["cpu"],
            logical_cpus=m["logical_cpus"],
        ),
        run=run,
        process_cpu=[
            ProcessCpu(
                run_id=run_id,
                process=n,
                cpu_ms=a["cpu_ms"],
                context_switches=int(a["cs"]),
            )
            for n, a in agg.items()
        ],
        wakeups=[
            Wakeup(
                run_id=run_id,
                process=n,
                wakeups=int(a["wk"]),
                wakeups_per_s=a["wk"] / duration,
            )
            for n, a in agg.items()
        ],
        battery_samples=[
            BatterySample(
                run_id=run_id,
                t_s=s["t_s"],
                discharge_mw=s["discharge_mw"],
                remaining_mwh=s["remaining_mwh"],
            )
            for s in _dedupe_samples(bundle["battery_samples"])
        ],
    )


def load_rows(session: Session, rows: BundleRows) -> bool:
    """Insert rows; return False (and write nothing) if run_id already exists."""
    if session.get(Run, rows.run.run_id) is not None:
        return False
    if session.get(Scenario, rows.scenario.name) is None:
        session.add(rows.scenario)
    if session.get(Machine, rows.machine.id_hash) is None:
        session.add(rows.machine)
    session.flush()
    session.add(rows.run)
    session.flush()
    session.add_all(rows.process_cpu)
    session.add_all(rows.wakeups)
    session.add_all(rows.battery_samples)
    session.flush()
    return True
