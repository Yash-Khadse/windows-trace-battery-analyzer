import copy
import json
from pathlib import Path

import pytest
from hypothesis import given
from hypothesis import strategies as st

from wtba import ingest
from wtba.ingest import BundleError, load_bundle, validate_bundle
from wtba.scrub import (
    USER_PATH_RE,
    SaltError,
    check_salt,
    scrub_bundle,
    scrub_free_text,
    scrub_process_name,
    scrub_text,
)

FIXTURES = Path(__file__).resolve().parents[2] / "fixtures"

LEAKY = [
    "C:\\Users\\alice\\x.exe",
    "c:/users/alice/x",
    "C:\\Users/alice\\x",
    "\\\\PC01\\c$\\Users\\alice\\Desktop",
    "//PC01/C$/Users/alice/Desktop",
    "\\Device\\HarddiskVolume3\\Users\\alice\\x",
    "\\Device\\HarddiskVolume12\\USERS\\alice",
    "C:\\Documents and Settings\\alice\\Local Settings",
    "c:/documents and settings/alice",
    "C:\\Users\\ALICE~1\\AppData",
    "C:\\Users",
    "C:\\Users\\",
    "\\Users\\alice",
    "/home/alice/bin",
    "\\\\?\\C:\\Users\\alice\\x",
]


@pytest.mark.parametrize("text", LEAKY)
def test_table_driven_forms(text: str) -> None:
    out = scrub_text(f"prefix {text} suffix")
    assert "alice" not in out.lower()
    assert "alice~1" not in out.lower()
    assert USER_PATH_RE.search(out) is None
    assert "<USER>" in out


def test_env_style_and_misc() -> None:
    assert "USERPROFILE" not in scrub_text("%USERPROFILE%\\Desktop")
    assert "username" not in scrub_text("$env:USERNAME and %username%").lower()
    assert scrub_text("ALICE~1") == "<USER>"
    assert scrub_text("mail bob@example.com now") == "mail <EMAIL> now"
    assert "PC01" not in scrub_text("\\\\PC01\\share\\file")
    assert scrub_process_name("\\\\PC01\\c$\\Users\\alice\\app.exe") == "app.exe"


def test_legit_cpu_names_survive(monkeypatch: pytest.MonkeyPatch) -> None:
    cpu = "AMD Ryzen 7 7840HS w/ Radeon 780M Graphics"
    assert scrub_free_text(cpu) == cpu
    assert scrub_free_text("Intel(R) Core(TM) i7-1355U") == "Intel(R) Core(TM) i7-1355U"
    assert scrub_text("10.0.26200") == "10.0.26200"


def test_known_user_and_host_scrubbed(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("USERNAME", "zorbulon")
    monkeypatch.setenv("COMPUTERNAME", "DESKTOP-QQ1234")
    out = scrub_free_text("run by zorbulon on desktop-qq1234")
    assert "zorbulon" not in out and "QQ1234" not in out.upper()
    src = load_bundle(FIXTURES / "bundle_idle_A_01.json")
    src["label"] = "zorbulon test"
    src["machine"]["cpu"] = "cpu of DESKTOP-QQ1234"
    b = scrub_bundle(src, salt="ci-only-salt")
    assert "zorbulon" not in json.dumps(b) and "QQ1234" not in json.dumps(b)


name_chars = st.characters(
    blacklist_characters='\\/:*?"<>|', blacklist_categories=("Cs", "Cc")
)
names = st.text(alphabet=name_chars, min_size=1, max_size=10)
seps = st.sampled_from(["\\", "/", "\\\\", "/\\"])
roots = st.sampled_from(
    ["C:", "d:", "\\\\host\\c$", "//h/D$", "\\Device\\HarddiskVolume2", ""]
)
homes = st.sampled_from(
    ["Users", "users", "Documents and Settings", "DOCUMENTS AND SETTINGS"]
)


@given(st.text(max_size=15), roots, seps, homes, seps, names, st.text(max_size=15))
def test_property_all_forms(
    pre: str, root: str, s1: str, home: str, s2: str, n: str, post: str
) -> None:
    out = scrub_text(f"{pre}{root}{s1}{home}{s2}{n}{post}")
    assert USER_PATH_RE.search(out) is None


@given(st.text(max_size=40))
def test_scrub_is_idempotent(text: str) -> None:
    once = scrub_text(text)
    assert scrub_text(once) == once


@pytest.mark.parametrize("salt", ["", "change-me", "change-me-random", "CHANGE-ME-x"])
def test_placeholder_salts_rejected(salt: str) -> None:
    with pytest.raises(SaltError) as ei:
        check_salt(salt)
    assert str(ei.value).count(salt) <= 1 or not salt


def test_salt_rules(monkeypatch: pytest.MonkeyPatch) -> None:
    assert check_salt("ci-only-salt") == "ci-only-salt"
    monkeypatch.delenv("WTBA_ALLOW_TEST_SALT")
    with pytest.raises(SaltError, match="test salt"):
        check_salt("ci-only-salt")
    with pytest.raises(SaltError, match="at least 16"):
        check_salt("short")
    assert check_salt("a-sufficiently-long-random-salt")


# ---- bundle validation ----


def _valid() -> dict:  # type: ignore[type-arg]
    return load_bundle(FIXTURES / "bundle_idle_A_01.json")


@pytest.mark.parametrize(
    "ts", ["not a date", "2030-01-01", "2030-01-01T10:00:00", "2030-13-45T99:00:00Z"]
)
def test_bad_started_utc(ts: str) -> None:
    d = _valid()
    d["started_utc"] = ts
    with pytest.raises(BundleError):
        validate_bundle(d)


def test_started_utc_good_forms() -> None:
    for ts in ["2030-01-01T10:00:00Z", "2030-01-01T10:00:00+05:30"]:
        d = _valid()
        d["started_utc"] = ts
        validate_bundle(d)


def test_string_length_limits() -> None:
    d = _valid()
    d["label"] = "x" * 257
    with pytest.raises(BundleError):
        validate_bundle(d)
    d = _valid()
    d["processes"][0]["name"] = "y" * 257
    with pytest.raises(BundleError):
        validate_bundle(d)


def test_array_limits() -> None:
    d = _valid()
    p = copy.deepcopy(d["processes"][0])
    d["processes"] = [p] * 20001
    with pytest.raises(BundleError):
        validate_bundle(d)
    d = _valid()
    s = copy.deepcopy(d["battery_samples"][0])
    d["battery_samples"] = [s] * 20001
    with pytest.raises(BundleError):
        validate_bundle(d)


@pytest.mark.parametrize("literal", ["NaN", "Infinity", "-Infinity", "1e999"])
def test_non_finite_rejected(tmp_path: Path, literal: str) -> None:
    text = (
        (FIXTURES / "bundle_idle_A_01.json")
        .read_text("utf-8")
        .replace('"duration_s": 600.0', f'"duration_s": {literal}')
    )
    f = tmp_path / "b.json"
    f.write_text(text, "utf-8")
    with pytest.raises(BundleError):
        load_bundle(f)


def test_size_limit(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(ingest, "MAX_BUNDLE_BYTES", 100)
    with pytest.raises(BundleError, match="limit"):
        load_bundle(FIXTURES / "bundle_idle_A_01.json")


def test_bad_utf8(tmp_path: Path) -> None:
    f = tmp_path / "b.json"
    f.write_bytes(b"\xff\xfe{}")
    with pytest.raises(BundleError):
        load_bundle(f)
