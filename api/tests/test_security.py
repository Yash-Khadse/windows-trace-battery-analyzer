"""Host/Origin guard and /compare cost caps (no database needed)."""

from __future__ import annotations

import time
import uuid

import numpy as np
import pytest
from fastapi.testclient import TestClient

from wtba_api import create_app
from wtba_api.app import (
    MAX_PROCESSES,
    PROCESS_BUDGET_S,
    _rank_processes,
    process_cap,
    resamples_for,
)
from wtba_api.config import ConfigError, Settings

from .conftest import DEAD_URL, ORIGIN, make_settings

URL = "/runs?limit=0"  # always 422 without touching the DB


@pytest.mark.parametrize(
    "host",
    [
        "evil.example",
        "evil.example:8000",
        "127.0.0.1.evil.example",
        "localhost.evil.example:8000",
        "0.0.0.0:8000",
        "192.168.1.5",
        "[::2]",
    ],
)
def test_foreign_host_is_400(offline_client: TestClient, host: str) -> None:
    r = offline_client.get(URL, headers={"Host": host})
    assert r.status_code == 400
    assert r.json()["error"]["code"] == "invalid_host"


@pytest.mark.parametrize(
    "host", ["127.0.0.1", "127.0.0.1:8000", "localhost:5173", "LOCALHOST", "[::1]:8000"]
)
def test_loopback_hosts_ok(offline_client: TestClient, host: str) -> None:
    assert offline_client.get(URL, headers={"Host": host}).status_code == 422


def test_rebinding_attack_shape_is_rejected(offline_client: TestClient) -> None:
    # attacker page rebinds evil.example to 127.0.0.1: Host stays evil.example
    r = offline_client.get(
        URL, headers={"Host": "evil.example:8000", "Origin": "http://evil.example:8000"}
    )
    assert r.status_code == 400
    assert "access-control-allow-origin" not in r.headers


def test_unknown_origin_is_403_even_for_simple_get(offline_client: TestClient) -> None:
    for origin in ("http://evil.example", "null", "http://localhost:5174"):
        r = offline_client.get(URL, headers={"Origin": origin})
        assert r.status_code == 403, origin
        assert r.json()["error"]["code"] == "forbidden_origin"
    assert offline_client.get(URL, headers={"Origin": ORIGIN}).status_code == 422
    assert offline_client.get(URL).status_code == 422  # no Origin header: fine


def test_allowed_hosts_env_extends_defaults() -> None:
    s = Settings.from_env(
        {"WTBA_API_DATABASE_URL": DEAD_URL, "WTBA_API_ALLOWED_HOSTS": "wtba.local"}
    )
    with TestClient(create_app(s), base_url="http://wtba.local:8000") as c:
        assert c.get(URL).status_code == 422
        assert c.get(URL, headers={"Host": "other.local"}).status_code == 400
    with TestClient(
        create_app(make_settings(DEAD_URL)), base_url="http://wtba.local"
    ) as c:
        assert c.get(URL).status_code == 400
    with pytest.raises(ConfigError):
        Settings.from_env(
            {"WTBA_API_DATABASE_URL": DEAD_URL, "WTBA_API_ALLOWED_HOSTS": "*"}
        )


def test_resample_and_process_caps_scale_with_size() -> None:
    assert resamples_for(10, 10) == 10_000
    assert resamples_for(100, 100) == 2_000
    assert resamples_for(200, 200) == 200
    assert process_cap(10, 10) <= MAX_PROCESSES
    assert process_cap(200, 200) < process_cap(10, 10)
    assert process_cap(200, 200) >= 1


@pytest.mark.slow
def test_200_runs_x_200_processes_finishes_quickly() -> None:
    rng = np.random.default_rng(1)
    n = 200
    ids_a = [uuid.uuid4() for _ in range(n)]
    ids_b = [uuid.uuid4() for _ in range(n)]
    vals = {
        f"proc{p}.exe": {
            i: float(rng.normal(100, 10) * (1.2 if i in set(ids_b) else 1.0))
            for i in ids_a + ids_b
        }
        for p in range(MAX_PROCESSES)
    }
    t0 = time.perf_counter()
    out, truncated = _rank_processes(vals, ids_a, ids_b, 5.0)
    elapsed = time.perf_counter() - t0
    assert len(out) == min(25, process_cap(n, n)) and truncated is True
    assert elapsed < PROCESS_BUDGET_S * 3, elapsed
