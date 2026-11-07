"""Pydantic v2 response models."""

from __future__ import annotations

import datetime as dt
import math
import uuid
from typing import Literal

from pydantic import BaseModel
from wtba.stats import MetricComparison

MetricName = Literal["mean_discharge_mw", "total_cpu_ms", "total_wakeups"]


class Health(BaseModel):
    status: Literal["ok"]
    database: Literal["ok", "unavailable"]


class ScenarioOut(BaseModel):
    name: str
    run_count: int
    first_started_utc: dt.datetime | None
    last_started_utc: dt.datetime | None


class RunOut(BaseModel):
    run_id: uuid.UUID
    scenario: str
    machine_id_hash: str
    condition: str
    label: str
    started_utc: dt.datetime
    duration_s: float
    on_ac: bool
    battery_saver: bool
    plan_guid: str
    start_pct: int
    end_pct: int
    dropped_events: int
    sampler_gaps_s: float
    gates_passed: bool
    mean_discharge_mw: float | None
    total_cpu_ms: float
    total_wakeups: int


class RunPage(BaseModel):
    total: int
    limit: int
    offset: int
    items: list[RunOut]


class ProcessOut(BaseModel):
    process: str
    cpu_ms: float
    context_switches: int
    wakeups: int
    wakeups_per_s: float


class SampleOut(BaseModel):
    t_s: float
    discharge_mw: float
    remaining_mwh: float


class SamplesOut(BaseModel):
    run_id: str
    samples: list[SampleOut]


class ComparisonOut(BaseModel):
    n_a: int
    n_b: int
    median_a: float | None
    median_b: float | None
    hl_shift: float | None
    moses_ci_low: float | None
    moses_ci_high: float | None
    boot_ci_low: float | None
    boot_ci_high: float | None
    pct_change: float | None
    p_value: float
    q_value: float
    cliffs_delta: float
    regression: bool
    new_activity: bool
    reason: str
    warnings: list[str]

    @classmethod
    def from_stats(cls, r: MetricComparison) -> ComparisonOut:
        def f(x: float) -> float | None:
            return None if math.isnan(x) or math.isinf(x) else x

        return cls(
            n_a=r.n_a, n_b=r.n_b,
            median_a=f(r.median_a), median_b=f(r.median_b),
            hl_shift=f(r.hl_shift),
            moses_ci_low=f(r.ci_low), moses_ci_high=f(r.ci_high),
            boot_ci_low=f(r.boot_ci_low), boot_ci_high=f(r.boot_ci_high),
            pct_change=None if r.pct_change is None else f(r.pct_change),
            p_value=r.p_value, q_value=r.q_value, cliffs_delta=r.cliffs_delta,
            regression=r.regression, new_activity=r.new_activity,
            reason=r.reason, warnings=list(r.warnings),
        )  # fmt: skip


class ProcessComparison(BaseModel):
    process: str
    comparison: ComparisonOut


class PerProcess(BaseModel):
    cpu_ms: list[ProcessComparison]
    wakeups: list[ProcessComparison]
    truncated: bool = False  # candidate set capped for cost


class ExcludedCounts(BaseModel):
    a: int
    b: int


class CompareOut(BaseModel):
    scenario: str
    a: str
    b: str
    threshold_pct: float
    include_warmup: bool
    include_failed_gates: bool
    excluded_failed_gates: ExcludedCounts
    runs_a: int
    runs_b: int
    metrics: dict[str, ComparisonOut]
    per_process: PerProcess


class TrendPoint(BaseModel):
    started_utc: dt.datetime
    run_id: uuid.UUID
    condition: str
    label: str
    value: float


class TrendOut(BaseModel):
    scenario: str
    metric: MetricName
    excluded_failed_gates: int
    points: list[TrendPoint]
