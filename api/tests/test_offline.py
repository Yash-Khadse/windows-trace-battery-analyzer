"""Tests that need no database."""

from __future__ import annotations

import pytest
from fastapi.testclient import TestClient

from wtba_api import main
from wtba_api.config import ConfigError, Settings

from .conftest import DEAD_URL, ORIGIN


def test_config_requires_url_and_hides_it() -> None:
    with pytest.raises(ConfigError, match="WTBA_API_DATABASE_URL"):
        Settings.from_env({})
    s = Settings.from_env({"WTBA_API_DATABASE_URL": DEAD_URL})
    assert "s3cretpw" not in repr(s)
    assert s.cors_origins == ("http://localhost:5173", "http://127.0.0.1:5173")


def test_config_errors_do_not_echo_credentials() -> None:
    with pytest.raises(ConfigError) as ei:
        Settings.from_env({"WTBA_API_DATABASE_URL": "mysql://u:s3cretpw@h/db"})
    assert "s3cretpw" not in str(ei.value)
    with pytest.raises(ConfigError) as ei:
        Settings.from_env({"WTBA_API_DATABASE_URL": "not a url s3cretpw"})
    assert "s3cretpw" not in str(ei.value)


def test_config_rejects_wildcard_cors() -> None:
    with pytest.raises(ConfigError, match="wildcard"):
        Settings.from_env(
            {"WTBA_API_DATABASE_URL": DEAD_URL, "WTBA_API_CORS_ORIGINS": "*"}
        )


def test_main_refuses_non_loopback_host(
    monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]
) -> None:
    monkeypatch.setenv("WTBA_API_HOST", "0.0.0.0")
    monkeypatch.setenv("WTBA_API_DATABASE_URL", DEAD_URL)
    with pytest.raises(SystemExit) as ei:
        main()
    assert ei.value.code == 2
    assert "127.0.0.1" in capsys.readouterr().err


def test_main_binds_loopback_only(monkeypatch: pytest.MonkeyPatch) -> None:
    import uvicorn

    seen: dict[str, object] = {}
    monkeypatch.delenv("WTBA_API_HOST", raising=False)
    monkeypatch.setenv("WTBA_API_DATABASE_URL", DEAD_URL)
    monkeypatch.setattr(uvicorn, "run", lambda app, **kw: seen.update(kw))
    main()
    assert seen["host"] == "127.0.0.1"


def test_main_missing_url_is_clean_exit(
    monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]
) -> None:
    monkeypatch.delenv("WTBA_API_HOST", raising=False)
    monkeypatch.delenv("WTBA_API_DATABASE_URL", raising=False)
    with pytest.raises(SystemExit) as ei:
        main()
    assert ei.value.code == 2
    assert "WTBA_API_DATABASE_URL" in capsys.readouterr().err


@pytest.mark.parametrize(
    "url",
    [
        "/runs?limit=0",
        "/runs?limit=501",
        "/runs?offset=-1",
        "/runs?condition=C",
        "/runs/not-a-uuid",
        "/runs/not-a-uuid/processes",
        "/runs/not-a-uuid/samples",
        "/runs/00000000-0000-4000-8000-00000000a001/samples?limit=0",
        "/runs/00000000-0000-4000-8000-00000000a001/samples?limit=5001",
        "/runs/00000000-0000-4000-8000-00000000a001/processes?sort=bogus",
        "/compare?scenario=s&a=A",
        "/compare?scenario=s&a=A&b=B&metric=bogus",
        "/compare?scenario=s&a=A&b=B&threshold_pct=-1",
        "/compare?scenario=s&a=A&b=B&include_warmup=maybe",
        "/trend?scenario=s",
        "/trend?scenario=s&metric=bogus",
        "/runs?scenario=a%00b",
        "/runs?label=a%1Fb",
        "/compare?scenario=s&a=A%00&b=B",
        "/trend?scenario=s%00&metric=total_cpu_ms",
    ],
)
def test_validation_errors_use_error_body(offline_client: TestClient, url: str) -> None:
    r = offline_client.get(url)
    assert r.status_code == 422, url
    err = r.json()["error"]
    assert err["code"] == "validation_error" and err["message"]
    assert "Traceback" not in r.text


def test_compare_same_group_rejected(offline_client: TestClient) -> None:
    r = offline_client.get("/compare?scenario=s&a=A&b=A")
    assert r.status_code == 422 and r.json()["error"]["code"] == "validation_error"


@pytest.mark.parametrize("method", ["post", "put", "patch", "delete"])
@pytest.mark.parametrize(
    "path", ["/runs", "/scenarios", "/compare", "/trend", "/health"]
)
def test_write_verbs_are_405(
    offline_client: TestClient, method: str, path: str
) -> None:
    r = getattr(offline_client, method)(path)
    assert r.status_code == 405
    assert r.json()["error"]["code"] == "method_not_allowed"


def test_unknown_path_404_error_body(offline_client: TestClient) -> None:
    r = offline_client.get("/nope")
    assert r.status_code == 404 and r.json()["error"]["code"] == "not_found"


def test_cors_allowed_origin_only(offline_client: TestClient) -> None:
    ok = offline_client.get("/runs?limit=0", headers={"Origin": ORIGIN})
    assert ok.headers["access-control-allow-origin"] == ORIGIN
    bad = offline_client.get("/runs?limit=0", headers={"Origin": "http://evil.example"})
    assert "access-control-allow-origin" not in bad.headers


def test_cors_preflight_get_only(offline_client: TestClient) -> None:
    h = {"Origin": ORIGIN, "Access-Control-Request-Method": "GET"}
    r = offline_client.options("/runs", headers=h)
    assert r.status_code == 200
    allowed = r.headers["access-control-allow-methods"]
    assert "GET" in allowed and "POST" not in allowed and "DELETE" not in allowed
    r = offline_client.options(
        "/runs", headers={**h, "Access-Control-Request-Method": "DELETE"}
    )
    assert r.status_code == 400
    r = offline_client.options(
        "/runs",
        headers={
            "Origin": "http://evil.example",
            "Access-Control-Request-Method": "GET",
        },
    )
    assert "access-control-allow-origin" not in r.headers


def test_db_down_is_clean_and_leaks_nothing(offline_client: TestClient) -> None:
    h = offline_client.get("/health")
    assert h.status_code == 200 and h.json()["database"] == "unavailable"
    r = offline_client.get("/scenarios")
    assert r.status_code == 503
    assert r.json()["error"]["code"] == "database_unavailable"
    for text in (h.text, r.text):
        assert "s3cretpw" not in text and "wtba_user" not in text


def test_openapi_documents_get_only(offline_client: TestClient) -> None:
    paths = offline_client.get("/openapi.json").json()["paths"]
    expected = {
        "/health", "/scenarios", "/runs", "/runs/{run_id}", "/compare", "/trend",
        "/runs/{run_id}/processes", "/runs/{run_id}/samples",
    }  # fmt: skip
    assert expected <= set(paths)
    assert all(set(v) == {"get"} for v in paths.values())
