from __future__ import annotations

import datetime as dt
import os
import uuid
from collections.abc import Iterator

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import create_engine
from sqlalchemy.engine import Engine
from sqlalchemy.orm import Session
from wtba.models import Base, Machine, ProcessCpu, Run, Scenario, Wakeup

from wtba_api import create_app
from wtba_api.config import Settings

TEST_DB = os.environ.get("WTBA_TEST_DATABASE_URL")
ORIGIN = "http://localhost:5173"
DEAD_URL = "postgresql+psycopg://wtba_user:s3cretpw@127.0.0.1:1/nodb"


def make_settings(url: str) -> Settings:
    return Settings.from_env(
        {"WTBA_API_DATABASE_URL": url, "WTBA_API_CORS_ORIGINS": ORIGIN}
    )


@pytest.fixture
def offline_client() -> Iterator[TestClient]:
    """App wired to an unreachable DB: fine for validation/CORS/405 tests."""
    with TestClient(
        create_app(make_settings(DEAD_URL)), base_url="http://127.0.0.1"
    ) as c:
        yield c


def _noise(i: int) -> int:
    return ((i * 37) % 11 - 5) * 20


def _run(
    scenario: str, cond: str, label: str, when: dt.datetime, i: int, shift: float
) -> Run:
    return Run(
        run_id=uuid.uuid4(), scenario=scenario, machine_id_hash="m1",
        condition=cond, label=label, started_utc=when, duration_s=600.0,
        on_ac=False, battery_saver=False, plan_guid="g", start_pct=90, end_pct=88,
        dropped_events=0, sampler_gaps_s=0.0, gates_passed=True,
        mean_discharge_mw=(7000 + _noise(i)) * shift,
        total_cpu_ms=2000.0 + _noise(i), total_wakeups=5000 + _noise(i),
    )  # fmt: skip


def seed(engine: Engine) -> None:
    base = dt.datetime(2026, 1, 1, tzinfo=dt.UTC)
    with Session(engine) as s, s.begin():
        s.add_all(
            [Scenario(name="synth"), Scenario(name="tiny"), Scenario(name="gated")]
        )
        s.add(Machine(id_hash="m1", os_build="1", cpu="cpu", logical_cpus=8))
        s.flush()
        runs: list[Run] = []
        for i in range(10):
            day = base + dt.timedelta(days=2 * i + 1)
            runs.append(_run("synth", "A", "baseline", day, i, 1.0))
            runs.append(
                _run("synth", "B", "candidate", day + dt.timedelta(days=1), i, 1.15)
            )
        # warmup run with an absurd value, earliest in time
        runs.append(_run("synth", "A", "baseline-warmup", base, 0, 10.0))
        for i in range(3):
            runs.append(_run("tiny", "A", "t-a", base + dt.timedelta(days=i), i, 1.0))
            runs.append(_run("tiny", "B", "t-b", base + dt.timedelta(days=i), i, 1.5))
        for i in range(6):
            for cond, k in (("A", 1.0), ("B", 1.15)):
                g = _run("gated", cond, "g" + cond, base + dt.timedelta(days=i), i, k)
                g.gates_passed = i != 0  # first run of each group failed its gates
                runs.append(g)
        # insert in reverse time order so trend ordering is really tested
        runs.sort(key=lambda r: r.started_utc, reverse=True)
        s.add_all(runs)
        s.flush()
        a_i = b_i = 0
        for r in runs:
            if r.scenario != "synth" or r.label == "baseline-warmup":
                continue
            if r.condition == "A":
                i, k = a_i, 1.0
                a_i += 1
            else:
                i, k = b_i, 1.3
                b_i += 1
            s.add_all([
                ProcessCpu(run_id=r.run_id, process="hog.exe",
                           cpu_ms=(1000 + _noise(i)) * k, context_switches=100 + i),
                ProcessCpu(run_id=r.run_id, process="calm.exe",
                           cpu_ms=500.0 + _noise(i), context_switches=50),
                Wakeup(run_id=r.run_id, process="hog.exe",
                       wakeups=int((900 + _noise(i)) * k), wakeups_per_s=1.5),
                Wakeup(run_id=r.run_id, process="calm.exe",
                       wakeups=300 + _noise(i), wakeups_per_s=0.5),
            ])  # fmt: skip
            if r.condition == "B" and i < 2:
                s.add(ProcessCpu(run_id=r.run_id, process="rare.exe",
                                 cpu_ms=9.0, context_switches=1))  # fmt: skip


@pytest.fixture(scope="session")
def db_engine() -> Iterator[Engine]:
    if not TEST_DB:
        pytest.skip("WTBA_TEST_DATABASE_URL not set")
    engine = create_engine(TEST_DB)
    Base.metadata.drop_all(engine)
    Base.metadata.create_all(engine)
    try:
        seed(engine)
        yield engine
    finally:
        Base.metadata.drop_all(engine)
        engine.dispose()


@pytest.fixture
def client(db_engine: Engine) -> Iterator[TestClient]:
    assert TEST_DB is not None
    with TestClient(
        create_app(make_settings(TEST_DB)), base_url="http://127.0.0.1"
    ) as c:
        yield c
