"""Database-backed tests (skipped unless WTBA_TEST_DATABASE_URL is set)."""

from __future__ import annotations

import datetime as dt
import uuid
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import func, select, text
from sqlalchemy.engine import Engine
from sqlalchemy.exc import DBAPIError
from sqlalchemy.orm import Session
from wtba.ingest import load_bundle
from wtba.load import bundle_to_rows, load_rows
from wtba.models import BatterySample, Run
from wtba.scrub import scrub_bundle

from .conftest import TEST_DB

pytestmark = pytest.mark.skipif(not TEST_DB, reason="WTBA_TEST_DATABASE_URL not set")

FIXTURES = Path(__file__).resolve().parents[2] / "fixtures"
BASE = "/compare?scenario=synth&a=A&b=B"


def test_scenarios(client: TestClient) -> None:
    items = {s["name"]: s for s in client.get("/scenarios").json()}
    assert items["synth"]["run_count"] == 21
    assert items["tiny"]["run_count"] == 6


def test_health_ok(client: TestClient) -> None:
    assert client.get("/health").json() == {"status": "ok", "database": "ok"}


def test_compare_flags_known_regression(client: TestClient) -> None:
    r = client.get(BASE)
    assert r.status_code == 200
    body = r.json()
    assert body["runs_a"] == 10 and body["runs_b"] == 10  # warmup excluded
    m = body["metrics"]
    dis = m["mean_discharge_mw"]
    assert dis["regression"] is True and dis["reason"] == "regression"
    assert dis["n_a"] == 10 and dis["n_b"] == 10
    assert dis["pct_change"] == pytest.approx(15.0, abs=1.5)
    assert dis["moses_ci_low"] > 0 and dis["boot_ci_low"] > 0
    assert dis["q_value"] < 0.05 and dis["cliffs_delta"] > 0.9
    for key in (
        "median_a",
        "median_b",
        "hl_shift",
        "p_value",
        "warnings",
        "new_activity",
    ):
        assert key in dis
    assert m["total_cpu_ms"]["regression"] is False
    assert m["total_wakeups"]["regression"] is False


def test_compare_single_metric_and_label_selectors(client: TestClient) -> None:
    r = client.get(
        "/compare?scenario=synth&a=baseline&b=candidate&metric=mean_discharge_mw"
    )
    assert r.status_code == 200
    body = r.json()
    assert list(body["metrics"]) == ["mean_discharge_mw"]
    assert body["metrics"]["mean_discharge_mw"]["regression"] is True


def test_compare_threshold_param(client: TestClient) -> None:
    r = client.get(BASE + "&metric=mean_discharge_mw&threshold_pct=50")
    dis = r.json()["metrics"]["mean_discharge_mw"]
    assert dis["regression"] is False and "below threshold" in dis["reason"]


def test_warmup_excluded_by_default_and_includable(client: TestClient) -> None:
    on = client.get(BASE + "&include_warmup=true").json()
    assert on["runs_a"] == 11
    assert client.get(BASE).json()["runs_a"] == 10


def test_per_process(client: TestClient) -> None:
    pp = client.get(BASE).json()["per_process"]
    cpu = {p["process"]: p["comparison"] for p in pp["cpu_ms"]}
    assert set(cpu) == {"hog.exe", "calm.exe"}  # rare.exe in 2/10 B runs only
    assert cpu["hog.exe"]["regression"] is True
    assert cpu["calm.exe"]["regression"] is False
    assert [p["process"] for p in pp["cpu_ms"]][0] == "hog.exe"  # sorted by |effect|
    assert {p["process"] for p in pp["wakeups"]} == {"hog.exe", "calm.exe"}
    assert cpu["hog.exe"]["q_value"] >= cpu["hog.exe"]["p_value"]


def test_small_n_returns_refusal_not_error(client: TestClient) -> None:
    r = client.get("/compare?scenario=tiny&a=A&b=B&metric=mean_discharge_mw")
    assert r.status_code == 200
    dis = r.json()["metrics"]["mean_discharge_mw"]
    assert dis["regression"] is False
    assert dis["reason"].startswith("refused")
    assert dis["warnings"]


def test_compare_404_for_empty_groups(client: TestClient) -> None:
    r = client.get("/compare?scenario=synth&a=A&b=nonexistent-label")
    assert r.status_code == 404
    err = r.json()["error"]
    assert err["code"] == "group_empty" and "nonexistent-label" in err["message"]
    r = client.get("/compare?scenario=nope&a=A&b=B")
    assert r.status_code == 404


def test_trend_ordered_and_excludes_warmup(client: TestClient) -> None:
    r = client.get("/trend?scenario=synth&metric=mean_discharge_mw")
    pts = r.json()["points"]
    assert len(pts) == 20
    times = [dt.datetime.fromisoformat(p["started_utc"]) for p in pts]
    assert times == sorted(times)
    assert all(p["value"] < 10_000 for p in pts)
    assert set(pts[0]) == {"started_utc", "run_id", "condition", "label", "value"}
    allp = client.get("/trend?scenario=synth&metric=total_cpu_ms&include_warmup=true")
    assert len(allp.json()["points"]) == 21
    onlyb = client.get("/trend?scenario=synth&metric=total_wakeups&condition=B")
    assert {p["condition"] for p in onlyb.json()["points"]} == {"B"}


def test_trend_unknown_scenario_404(client: TestClient) -> None:
    r = client.get("/trend?scenario=nope&metric=total_cpu_ms")
    assert r.status_code == 404
    assert r.json()["error"]["code"] == "scenario_not_found"


def test_runs_filters_and_pagination(client: TestClient) -> None:
    page = client.get("/runs?scenario=synth&limit=3&offset=2").json()
    assert page["total"] == 21 and len(page["items"]) == 3
    assert page["limit"] == 3 and page["offset"] == 2
    times = [i["started_utc"] for i in page["items"]]
    assert times == sorted(times, reverse=True)
    assert client.get("/runs?scenario=synth&condition=B").json()["total"] == 10
    assert client.get("/runs?label=baseline").json()["total"] == 10
    assert len(client.get("/runs").json()["items"]) <= 100
    assert client.get("/runs?offset=100000").json()["items"] == []
    assert client.get("/runs?limit=500").status_code == 200
    assert client.get("/runs?limit=501").status_code == 422


def test_run_detail_and_404(client: TestClient) -> None:
    rid = client.get("/runs?scenario=synth&condition=B&limit=1").json()["items"][0][
        "run_id"
    ]
    r = client.get(f"/runs/{rid}")
    assert r.status_code == 200 and r.json()["run_id"] == rid
    miss = client.get(f"/runs/{uuid.uuid4()}")
    assert miss.status_code == 404 and miss.json()["error"]["code"] == "run_not_found"
    assert client.get(f"/runs/{uuid.uuid4()}/processes").status_code == 404


def test_run_processes_sorting_and_limit(client: TestClient) -> None:
    rid = client.get("/runs?scenario=synth&condition=B&limit=1").json()["items"][0][
        "run_id"
    ]
    rows = client.get(f"/runs/{rid}/processes?sort=cpu_ms").json()
    assert rows[0]["process"] == "hog.exe"
    cpus = [x["cpu_ms"] for x in rows]
    assert cpus == sorted(cpus, reverse=True)
    one = client.get(f"/runs/{rid}/processes?sort=wakeups&limit=1").json()
    assert len(one) == 1 and one[0]["process"] == "hog.exe"
    assert client.get(f"/runs/{rid}/processes?limit=501").status_code == 422


@pytest.mark.parametrize(
    "evil", ["x' OR '1'='1", "'; DROP TABLE runs; --", "%", "_", "\\"]
)
def test_injection_looking_params_are_harmless(
    client: TestClient, db_engine: Engine, evil: str
) -> None:
    for url in (
        "/runs",
        "/compare",
    ):
        params = {"scenario": evil, "label": evil} if url == "/runs" else {
            "scenario": evil, "a": evil, "b": "B"
        }  # fmt: skip
        r = client.get(url, params=params)
        assert r.status_code in (200, 404, 422), r.text
        assert "Traceback" not in r.text
    r = client.get("/trend", params={"scenario": evil, "metric": "total_cpu_ms"})
    assert r.status_code in (404, 422)
    with Session(db_engine) as s:
        assert s.scalar(select(func.count()).select_from(Run)) == 39
    assert client.get("/runs", params={"label": evil}).json()["total"] == 0


def test_api_connection_is_read_only(client: TestClient) -> None:
    engine: Engine = client.app.state.engine  # type: ignore[attr-defined]
    with engine.connect() as c, pytest.raises(DBAPIError):
        c.execute(text("DELETE FROM runs"))


def test_repo_fixture_bundles_served(client: TestClient, db_engine: Engine) -> None:
    for name in ("bundle_idle_A_01.json", "bundle_idle_B_01.json"):
        rows = bundle_to_rows(
            scrub_bundle(
                load_bundle(FIXTURES / name),
                salt="api-test-salt-0123456789abcdef0123456789abcdef",
            )
        )
        with Session(db_engine) as s, s.begin():
            load_rows(s, rows)
    rid = "00000000-0000-4000-8000-00000000a001"
    run = client.get(f"/runs/{rid}").json()
    assert run["mean_discharge_mw"] == pytest.approx(7500.0)
    procs = client.get(f"/runs/{rid}/processes").json()
    assert {p["process"] for p in procs} >= {"fakeapp.exe"}
    cmp = client.get(f"/compare?scenario={run['scenario']}&a=A&b=B").json()
    assert cmp["metrics"]["mean_discharge_mw"]["reason"].startswith("refused")


def test_failed_gates_excluded_by_default(client: TestClient) -> None:
    url = "/compare?scenario=gated&a=A&b=B&metric=mean_discharge_mw"
    body = client.get(url).json()
    assert body["excluded_failed_gates"] == {"a": 1, "b": 1}
    assert body["runs_a"] == 5 and body["runs_b"] == 5
    assert body["include_failed_gates"] is False
    dis = body["metrics"]["mean_discharge_mw"]
    assert dis["n_a"] == 5 and dis["n_b"] == 5
    inc = client.get(url + "&include_failed_gates=true").json()
    assert inc["excluded_failed_gates"] == {"a": 0, "b": 0}
    assert inc["runs_a"] == 6 and inc["metrics"]["mean_discharge_mw"]["n_b"] == 6


def test_failed_gates_trend(client: TestClient) -> None:
    url = "/trend?scenario=gated&metric=total_cpu_ms"
    body = client.get(url).json()
    assert len(body["points"]) == 10 and body["excluded_failed_gates"] == 2
    inc = client.get(url + "&include_failed_gates=true").json()
    assert len(inc["points"]) == 12 and inc["excluded_failed_gates"] == 0
    clean = client.get("/trend?scenario=synth&metric=total_cpu_ms").json()
    assert clean["excluded_failed_gates"] == 0


def test_control_chars_are_422_not_503(client: TestClient) -> None:
    for bad in ("x%00y", "%01", "a%0Ab", "%7F"):
        for url in (
            f"/runs?scenario={bad}",
            f"/runs?label={bad}",
            f"/compare?scenario={bad}&a=A&b=B",
            f"/compare?scenario=synth&a={bad}&b=B",
            f"/trend?scenario={bad}&metric=total_cpu_ms",
        ):
            r = client.get(url)
            assert r.status_code == 422, url
            assert r.json()["error"]["code"] == "validation_error"


def test_run_samples(client: TestClient, db_engine: Engine) -> None:
    runs = client.get("/runs?scenario=tiny&limit=2").json()["items"]
    with_s, without = runs[0]["run_id"], runs[1]["run_id"]
    with Session(db_engine) as s, s.begin():
        for t in (30.0, 0.0, 20.0, 10.0):  # inserted out of order
            s.add(
                BatterySample(
                    run_id=uuid.UUID(with_s), t_s=t, discharge_mw=7000 + t,
                    remaining_mwh=50000 - t,
                )
            )  # fmt: skip
    r = client.get(f"/runs/{with_s}/samples")
    assert r.status_code == 200
    body = r.json()
    assert body["run_id"] == with_s
    assert [x["t_s"] for x in body["samples"]] == [0.0, 10.0, 20.0, 30.0]
    assert set(body["samples"][0]) == {"t_s", "discharge_mw", "remaining_mwh"}
    assert len(client.get(f"/runs/{with_s}/samples?limit=2").json()["samples"]) == 2
    empty = client.get(f"/runs/{without}/samples")
    assert empty.status_code == 200 and empty.json()["samples"] == []
    miss = client.get(f"/runs/{uuid.uuid4()}/samples")
    assert miss.status_code == 404 and miss.json()["error"]["code"] == "run_not_found"


def test_too_many_runs_is_422(client: TestClient, db_engine: Engine) -> None:
    import datetime as _dt

    from wtba.models import Scenario

    from .conftest import _run

    base = _dt.datetime(2025, 1, 1, tzinfo=_dt.UTC)
    with Session(db_engine) as s, s.begin():
        s.add(Scenario(name="huge"))
        s.flush()
        s.add_all(
            _run("huge", "A", "ha", base + _dt.timedelta(minutes=i), i, 1.0)
            for i in range(201)
        )
        s.add(_run("huge", "B", "hb", base, 0, 1.0))
    try:
        r = client.get("/compare?scenario=huge&a=A&b=B")
        assert r.status_code == 422
        err = r.json()["error"]
        assert err["code"] == "too_many_runs" and "201" in err["message"]
    finally:
        with Session(db_engine) as s, s.begin():
            s.execute(text("DELETE FROM runs WHERE scenario = 'huge'"))
            s.execute(text("DELETE FROM scenarios WHERE name = 'huge'"))
