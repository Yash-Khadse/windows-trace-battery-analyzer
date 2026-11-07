"""FastAPI application factory. All DB access is SELECT-only with bound params."""

from __future__ import annotations

import uuid
from collections.abc import AsyncIterator, Iterator
from contextlib import asynccontextmanager
from typing import Annotated, Any, Literal

from fastapi import Depends, FastAPI, Path, Query, Request
from fastapi.middleware.cors import CORSMiddleware
from sqlalchemy import ColumnElement, Engine, create_engine, func, select, text
from sqlalchemy.exc import SQLAlchemyError
from sqlalchemy.orm import Session
from wtba.models import BatterySample, ProcessCpu, Run, Scenario, Wakeup
from wtba.stats import compare_many

from .config import Settings
from .errors import ApiError, ErrorResponse, install_handlers
from .schemas import (
    CompareOut,
    ComparisonOut,
    ExcludedCounts,
    Health,
    MetricName,
    PerProcess,
    ProcessComparison,
    ProcessOut,
    RunOut,
    RunPage,
    SampleOut,
    SamplesOut,
    ScenarioOut,
    TrendOut,
    TrendPoint,
)
from .security import HostOriginGuard

METRICS: tuple[MetricName, ...] = ("mean_discharge_mw", "total_cpu_ms", "total_wakeups")
TOP_PROCESSES = 25
MIN_PRESENCE = 0.5
MAX_RUNS_PER_GROUP = 200  # exact-CI cost grows ~n^2; 200 runs ~0.3 s/metric
MAX_PROCESSES = 200  # candidates evaluated per family, chosen by presence
PROCESS_BUDGET_S = 3.0  # rough CPU budget per family (cpu_ms / wakeups)


def resamples_for(n_a: int, n_b: int) -> int:
    """Bootstrap resamples: spec default for small groups, fewer for big ones."""
    pairs = n_a * n_b
    if pairs <= 2500:
        return 10_000
    if pairs <= 10_000:
        return 2_000
    return 200


def process_cap(n_a: int, n_b: int) -> int:
    """How many processes fit the time budget (measured ~8e-6 s per pair)."""
    est = 0.01 + 8e-6 * n_a * n_b
    return max(1, min(MAX_PROCESSES, int(PROCESS_BUDGET_S / est)))


NO_CTRL = r"^[^\x00-\x1f\x7f]+$"

ScenarioQ = Annotated[str, Query(min_length=1, max_length=128, pattern=NO_CTRL)]
GroupQ = Annotated[
    str,
    Query(
        min_length=1,
        max_length=256,
        pattern=NO_CTRL,
        description="A condition ('A' or 'B') or an exact run label",
    ),
]

ERRORS: dict[int | str, dict[str, Any]] = {
    404: {"model": ErrorResponse},
    422: {"model": ErrorResponse},
}


def make_engine(url: str) -> Engine:
    return create_engine(
        url,
        pool_pre_ping=True,
        connect_args={
            "options": "-c default_transaction_read_only=on",
            "connect_timeout": 5,
        },
    )


def _not_warmup() -> ColumnElement[bool]:
    return ~func.lower(Run.label).contains("warmup", autoescape=True)


def _session_dep(request: Request) -> Iterator[Session]:
    with Session(request.app.state.engine) as s:
        yield s


Db = Annotated[Session, Depends(_session_dep)]


def create_app(settings: Settings | None = None) -> FastAPI:
    cfg = settings or Settings.from_env()
    engine = make_engine(cfg.database_url)

    @asynccontextmanager
    async def lifespan(_: FastAPI) -> AsyncIterator[None]:
        yield
        engine.dispose()

    app = FastAPI(title="WTBA API", version="0.1.0", lifespan=lifespan)
    app.state.engine = engine
    install_handlers(app)
    app.add_middleware(
        CORSMiddleware,
        allow_origins=list(cfg.cors_origins),
        allow_methods=["GET", "OPTIONS"],
        allow_headers=["Content-Type"],
        allow_credentials=False,
    )
    # added last = outermost: Host/Origin are checked before anything else runs
    app.add_middleware(
        HostOriginGuard,
        allowed_hosts=cfg.allowed_hosts,
        allowed_origins=cfg.cors_origins,
    )

    @app.get("/health", response_model=Health)
    def health(db: Db) -> Health:
        try:
            db.execute(text("SELECT 1"))
            return Health(status="ok", database="ok")
        except SQLAlchemyError:
            return Health(status="ok", database="unavailable")

    @app.get("/scenarios", response_model=list[ScenarioOut])
    def scenarios(db: Db) -> list[ScenarioOut]:
        q = (
            select(
                Scenario.name,
                func.count(Run.run_id),
                func.min(Run.started_utc),
                func.max(Run.started_utc),
            )
            .outerjoin(Run, Run.scenario == Scenario.name)
            .group_by(Scenario.name)
            .order_by(Scenario.name)
        )
        return [
            ScenarioOut(name=n, run_count=c, first_started_utc=lo, last_started_utc=hi)
            for n, c, lo, hi in db.execute(q)
        ]

    @app.get("/runs", response_model=RunPage, responses=ERRORS)
    def runs(
        db: Db,
        scenario: Annotated[
            str | None, Query(min_length=1, max_length=128, pattern=NO_CTRL)
        ] = None,
        condition: Literal["A", "B"] | None = None,
        label: Annotated[
            str | None, Query(min_length=1, max_length=256, pattern=NO_CTRL)
        ] = None,
        limit: Annotated[int, Query(ge=1, le=500)] = 100,
        offset: Annotated[int, Query(ge=0, le=10_000_000)] = 0,
    ) -> RunPage:
        conds: list[ColumnElement[bool]] = []
        if scenario is not None:
            conds.append(Run.scenario == scenario)
        if condition is not None:
            conds.append(Run.condition == condition)
        if label is not None:
            conds.append(Run.label == label)
        total = db.scalar(select(func.count()).select_from(Run).where(*conds)) or 0
        rows = db.scalars(
            select(Run)
            .where(*conds)
            .order_by(Run.started_utc.desc(), Run.run_id)
            .limit(limit)
            .offset(offset)
        ).all()
        return RunPage(
            total=total,
            limit=limit,
            offset=offset,
            items=[RunOut.model_validate(r, from_attributes=True) for r in rows],
        )

    @app.get("/runs/{run_id}", response_model=RunOut, responses=ERRORS)
    def run_detail(db: Db, run_id: Annotated[uuid.UUID, Path()]) -> RunOut:
        r = db.get(Run, run_id)
        if r is None:
            raise ApiError(404, "run_not_found", f"no run with id {run_id}")
        return RunOut.model_validate(r, from_attributes=True)

    @app.get(
        "/runs/{run_id}/processes", response_model=list[ProcessOut], responses=ERRORS
    )
    def run_processes(
        db: Db,
        run_id: Annotated[uuid.UUID, Path()],
        limit: Annotated[int, Query(ge=1, le=500)] = 50,
        sort: Literal["cpu_ms", "wakeups", "context_switches"] = "cpu_ms",
    ) -> list[ProcessOut]:
        if db.get(Run, run_id) is None:
            raise ApiError(404, "run_not_found", f"no run with id {run_id}")
        merged: dict[str, dict[str, float]] = {}
        for p, cpu, cs in db.execute(
            select(
                ProcessCpu.process, ProcessCpu.cpu_ms, ProcessCpu.context_switches
            ).where(ProcessCpu.run_id == run_id)
        ):
            merged.setdefault(p, {}).update(cpu_ms=cpu, context_switches=cs)
        for p, w, wps in db.execute(
            select(Wakeup.process, Wakeup.wakeups, Wakeup.wakeups_per_s).where(
                Wakeup.run_id == run_id
            )
        ):
            merged.setdefault(p, {}).update(wakeups=w, wakeups_per_s=wps)
        out = [
            ProcessOut(
                process=p,
                cpu_ms=v.get("cpu_ms", 0.0),
                context_switches=int(v.get("context_switches", 0)),
                wakeups=int(v.get("wakeups", 0)),
                wakeups_per_s=v.get("wakeups_per_s", 0.0),
            )
            for p, v in merged.items()
        ]
        out.sort(key=lambda o: (-float(getattr(o, sort)), o.process))
        return out[:limit]

    @app.get("/runs/{run_id}/samples", response_model=SamplesOut, responses=ERRORS)
    def run_samples(
        db: Db,
        run_id: Annotated[uuid.UUID, Path()],
        limit: Annotated[int, Query(ge=1, le=5000)] = 2000,
    ) -> SamplesOut:
        if db.get(Run, run_id) is None:
            raise ApiError(404, "run_not_found", f"no run with id {run_id}")
        rows = db.execute(
            select(
                BatterySample.t_s,
                BatterySample.discharge_mw,
                BatterySample.remaining_mwh,
            )
            .where(BatterySample.run_id == run_id)
            .order_by(BatterySample.t_s)
            .limit(limit)
        )
        return SamplesOut(
            run_id=str(run_id),
            samples=[
                SampleOut(t_s=t, discharge_mw=d, remaining_mwh=m) for t, d, m in rows
            ],
        )

    @app.get("/compare", response_model=CompareOut, responses=ERRORS)
    def compare(
        db: Db,
        scenario: ScenarioQ,
        a: GroupQ,
        b: GroupQ,
        metric: Literal[
            "mean_discharge_mw", "total_cpu_ms", "total_wakeups", "all"
        ] = "all",
        threshold_pct: Annotated[float, Query(ge=0, le=1000)] = 5.0,
        include_warmup: bool = False,
        include_failed_gates: bool = False,
    ) -> CompareOut:
        if a == b:
            raise ApiError(422, "validation_error", "a and b must differ")
        groups: dict[str, list[Run]] = {}
        excluded = {"a": 0, "b": 0}
        for name, token in (("a", a), ("b", b)):
            match: ColumnElement[bool] = (
                Run.condition == token if token in ("A", "B") else Run.label == token
            )
            q = select(Run).where(Run.scenario == scenario, match)
            if not include_warmup:
                q = q.where(_not_warmup())
            rs = list(db.scalars(q.order_by(Run.started_utc, Run.run_id)).all())
            if not include_failed_gates:
                excluded[name] = sum(1 for r in rs if not r.gates_passed)
                rs = [r for r in rs if r.gates_passed]
            if not rs:
                kind = "condition" if token in ("A", "B") else "label"
                raise ApiError(
                    404,
                    "group_empty",
                    f"group {name} ({kind} '{token}') has no runs in scenario "
                    f"'{scenario}'" + ("" if include_warmup else " (warmup excluded)"),
                )
            if len(rs) > MAX_RUNS_PER_GROUP:
                raise ApiError(
                    422,
                    "too_many_runs",
                    f"group {name} has {len(rs)} runs; the limit is "
                    f"{MAX_RUNS_PER_GROUP}. Narrow it with a label or scenario.",
                )
            groups[name] = rs
        ra, rb = groups["a"], groups["b"]
        names: list[MetricName] = list(METRICS) if metric == "all" else [metric]
        series: dict[str, tuple[list[float], list[float]]] = {}
        for m in names:
            series[m] = (
                [float(v) for r in ra if (v := getattr(r, m)) is not None],
                [float(v) for r in rb if (v := getattr(r, m)) is not None],
            )
        res = compare_many(
            series, True, threshold_pct, n_resamples=resamples_for(len(ra), len(rb))
        )
        return CompareOut(
            scenario=scenario,
            a=a,
            b=b,
            threshold_pct=threshold_pct,
            include_warmup=include_warmup,
            include_failed_gates=include_failed_gates,
            excluded_failed_gates=ExcludedCounts(a=excluded["a"], b=excluded["b"]),
            runs_a=len(ra),
            runs_b=len(rb),
            metrics={m: ComparisonOut.from_stats(res[m]) for m in names},
            per_process=_per_process(db, ra, rb, threshold_pct),
        )

    @app.get("/trend", response_model=TrendOut, responses=ERRORS)
    def trend(
        db: Db,
        scenario: ScenarioQ,
        metric: MetricName,
        condition: Literal["A", "B"] | None = None,
        include_warmup: bool = False,
        include_failed_gates: bool = False,
    ) -> TrendOut:
        if db.get(Scenario, scenario) is None:
            raise ApiError(404, "scenario_not_found", f"unknown scenario '{scenario}'")
        col = getattr(Run, metric)
        q = select(Run.started_utc, Run.run_id, Run.condition, Run.label, col).where(
            Run.scenario == scenario, col.is_not(None)
        )
        if condition is not None:
            q = q.where(Run.condition == condition)
        if not include_warmup:
            q = q.where(_not_warmup())
        n_failed = 0
        if not include_failed_gates:
            fq = (
                select(func.count())
                .select_from(Run)
                .where(
                    Run.scenario == scenario,
                    col.is_not(None),
                    Run.gates_passed.is_(False),
                )
            )
            if condition is not None:
                fq = fq.where(Run.condition == condition)
            if not include_warmup:
                fq = fq.where(_not_warmup())
            n_failed = db.scalar(fq) or 0
            q = q.where(Run.gates_passed.is_(True))
        q = q.order_by(Run.started_utc, Run.run_id)
        return TrendOut(
            scenario=scenario,
            metric=metric,
            excluded_failed_gates=n_failed,
            points=[
                TrendPoint(
                    started_utc=t, run_id=i, condition=c, label=lb, value=float(v)
                )
                for t, i, c, lb, v in db.execute(q)
            ],
        )

    return app


def _rank_processes(
    vals: dict[str, dict[uuid.UUID, float]],
    ids_a: list[uuid.UUID],
    ids_b: list[uuid.UUID],
    threshold_pct: float,
) -> tuple[list[ProcessComparison], bool]:
    """Compare processes present in >= 50% of runs of both groups.

    Runs lacking the process count as 0. At most process_cap() candidates (most
    present first) are evaluated; returns (top results, truncated).
    """
    cands: list[tuple[int, str]] = []
    for proc, per_run in vals.items():
        pa = sum(1 for i in ids_a if i in per_run)
        pb = sum(1 for i in ids_b if i in per_run)
        if pa / len(ids_a) < MIN_PRESENCE or pb / len(ids_b) < MIN_PRESENCE:
            continue
        cands.append((pa + pb, proc))
    cands.sort(key=lambda c: (-c[0], c[1]))
    cap = process_cap(len(ids_a), len(ids_b))
    truncated = len(cands) > cap
    metrics: dict[str, tuple[list[float], list[float]]] = {}
    for _, proc in cands[:cap]:
        per_run = vals[proc]
        metrics[proc] = (
            [per_run.get(i, 0.0) for i in ids_a],
            [per_run.get(i, 0.0) for i in ids_b],
        )
    if not metrics:
        return [], truncated
    res = compare_many(
        metrics,
        True,
        threshold_pct,
        n_resamples=min(resamples_for(len(ids_a), len(ids_b)), 2000),
    )
    ranked = sorted(
        res.items(),
        key=lambda kv: (-abs(kv[1].cliffs_delta), -abs(kv[1].hl_shift), kv[0]),
    )
    return [
        ProcessComparison(process=p, comparison=ComparisonOut.from_stats(r))
        for p, r in ranked[:TOP_PROCESSES]
    ], truncated


def _per_process(
    db: Session, ra: list[Run], rb: list[Run], threshold_pct: float
) -> PerProcess:
    ids_a, ids_b = [r.run_id for r in ra], [r.run_id for r in rb]
    out: dict[str, list[ProcessComparison]] = {}
    trunc = False
    for key, model, col in (
        ("cpu_ms", ProcessCpu, ProcessCpu.cpu_ms),
        ("wakeups", Wakeup, Wakeup.wakeups),
    ):
        vals: dict[str, dict[uuid.UUID, float]] = {}
        for rid, proc, v in db.execute(
            select(model.run_id, model.process, col).where(
                model.run_id.in_(ids_a + ids_b)
            )
        ):
            vals.setdefault(proc, {})[rid] = float(v)
        out[key], t = _rank_processes(vals, ids_a, ids_b, threshold_pct)
        trunc = trunc or t
    return PerProcess(cpu_ms=out["cpu_ms"], wakeups=out["wakeups"], truncated=trunc)


__all__ = ["create_app", "make_engine"]
