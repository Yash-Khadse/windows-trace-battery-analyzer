import os
import uuid
from pathlib import Path

import pytest
from alembic import command
from alembic.config import Config
from sqlalchemy import create_engine
from sqlalchemy.orm import Session
from typer.testing import CliRunner

from wtba.cli import app
from wtba.ingest import load_bundle
from wtba.load import bundle_to_rows, load_rows
from wtba.models import Base, Run
from wtba.scrub import scrub_bundle

PIPELINE = Path(__file__).resolve().parents[1]
FIXTURES = PIPELINE.parent / "fixtures"
TEST_DB = os.environ.get("WTBA_TEST_DATABASE_URL")


def _rows(name: str):  # type: ignore[no-untyped-def]
    return bundle_to_rows(scrub_bundle(load_bundle(FIXTURES / name), salt="s"))


def test_mapping_aggregates_and_computes() -> None:
    rows = _rows("bundle_idle_A_01.json")
    assert rows.run.run_id == uuid.UUID("00000000-0000-4000-8000-00000000a001")
    assert rows.counts() == {
        "scenarios": 1, "machines": 1, "runs": 1,
        "process_cpu": 2, "wakeups": 2, "battery_samples": 3,
    }  # fmt: skip
    app_row = next(p for p in rows.process_cpu if p.process == "fakeapp.exe")
    assert app_row.cpu_ms == 150.0 and app_row.context_switches == 600
    wk = next(w for w in rows.wakeups if w.process == "fakeapp.exe")
    assert wk.wakeups == 240 and wk.wakeups_per_s == pytest.approx(0.4)
    assert rows.run.total_cpu_ms == 160.0
    assert rows.run.total_wakeups == 270
    # first sample (t=0, 9999 mW) is excluded as settle time
    assert rows.run.mean_discharge_mw == pytest.approx(7500.0)
    assert rows.run.started_utc.tzinfo is not None


def test_mapping_no_samples_after_settle() -> None:
    rows = _rows("bundle_edge_A_01.json")
    assert rows.run.mean_discharge_mw == 12000  # t=30 is past settle
    rows = _rows("bundle_idle_B_01.json")
    assert rows.run.mean_discharge_mw == 6000  # offset +00:00 timestamp parsed


def test_mapped_rows_are_scrubbed() -> None:
    rows = _rows("bundle_edge_A_01.json")
    assert [p.process for p in rows.process_cpu] == ["fakebrowser.exe"]
    assert "FakePerson" not in rows.run.label
    assert rows.machine.id_hash != "FAKEMACHINE0002"


def test_cli_dry_run(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("WTBA_HASH_SALT", "cli-salt")
    res = CliRunner().invoke(
        app, ["ingest", str(FIXTURES / "bundle_idle_A_01.json"), "--dry-run"]
    )
    assert res.exit_code == 0, res.output
    assert "process_cpu=2" in res.output and "battery_samples=3" in res.output
    assert "cli-salt" not in res.output


def test_cli_rejects_invalid_and_missing_salt(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("WTBA_HASH_SALT", "x")
    res = CliRunner().invoke(
        app, ["ingest", str(FIXTURES / "invalid_bad_condition.json"), "--dry-run"]
    )
    assert res.exit_code == 2
    monkeypatch.delenv("WTBA_HASH_SALT")
    res = CliRunner().invoke(
        app, ["ingest", str(FIXTURES / "bundle_idle_A_01.json"), "--dry-run"]
    )
    assert res.exit_code == 2


def test_cli_needs_db_url_without_dry_run(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("WTBA_HASH_SALT", "x")
    monkeypatch.delenv("WTBA_DATABASE_URL", raising=False)
    res = CliRunner().invoke(app, ["ingest", str(FIXTURES / "bundle_idle_A_01.json")])
    assert res.exit_code == 2


def test_expected_tables_and_constraints_in_metadata() -> None:
    assert set(Base.metadata.tables) == {
        "scenarios", "machines", "runs", "process_cpu", "wakeups", "battery_samples",
    }  # fmt: skip
    runs = Base.metadata.tables["runs"]
    assert {i.name for i in runs.indexes} >= {"ix_runs_scenario_condition_started"}


def test_offline_migration_sql_matches_models() -> None:
    """Generate SQL offline (no connection) and check it covers every model constraint."""
    import io
    from contextlib import redirect_stdout

    cfg = Config(str(PIPELINE / "alembic.ini"))
    cfg.set_main_option("script_location", str(PIPELINE / "migrations"))
    cfg.set_main_option("sqlalchemy.url", "postgresql+psycopg://")
    buf = io.StringIO()
    with redirect_stdout(buf):
        command.upgrade(cfg, "head", sql=True)
    sql = buf.getvalue()
    for table in Base.metadata.tables.values():
        assert f"CREATE TABLE {table.name}" in sql
        for con in table.constraints:
            if isinstance(con.name, str):
                assert con.name in sql, con.name
        for idx in table.indexes:
            assert idx.name is not None and idx.name in sql
    assert "FOREIGN KEY(scenario) REFERENCES scenarios (name)" in sql
    assert "ON DELETE CASCADE" in sql


@pytest.mark.skipif(not TEST_DB, reason="WTBA_TEST_DATABASE_URL not set")
def test_db_load_is_idempotent_and_constraints_enforced() -> None:
    from sqlalchemy import func, select
    from sqlalchemy.exc import IntegrityError

    assert TEST_DB is not None
    engine = create_engine(TEST_DB)
    Base.metadata.drop_all(engine)
    Base.metadata.create_all(engine)
    try:
        with Session(engine) as s, s.begin():
            assert load_rows(s, _rows("bundle_idle_A_01.json")) is True
        with Session(engine) as s, s.begin():
            assert load_rows(s, _rows("bundle_idle_A_01.json")) is False
            assert load_rows(s, _rows("bundle_idle_B_01.json")) is True
        with Session(engine) as s:
            assert s.scalar(select(func.count()).select_from(Run)) == 2
        bad = _rows("bundle_edge_A_01.json")
        bad.run.condition = "Z"
        with pytest.raises(IntegrityError), Session(engine) as s, s.begin():
            load_rows(s, bad)
    finally:
        Base.metadata.drop_all(engine)


@pytest.mark.skipif(not TEST_DB, reason="WTBA_TEST_DATABASE_URL not set")
def test_cli_ingest_into_db_exits_cleanly(monkeypatch: pytest.MonkeyPatch) -> None:
    """Regression: the CLI used to crash after commit reading an expired ORM attribute."""
    assert TEST_DB is not None
    engine = create_engine(TEST_DB)
    Base.metadata.drop_all(engine)
    Base.metadata.create_all(engine)
    monkeypatch.setenv("WTBA_HASH_SALT", "cli-salt")
    monkeypatch.setenv("WTBA_DATABASE_URL", TEST_DB)
    try:
        path = str(FIXTURES / "bundle_idle_A_01.json")
        first = CliRunner().invoke(app, ["ingest", path])
        assert first.exit_code == 0, first.output
        assert "loaded run" in first.output
        second = CliRunner().invoke(app, ["ingest", path])
        assert second.exit_code == 0, second.output
        assert "already loaded" in second.output
    finally:
        Base.metadata.drop_all(engine)
