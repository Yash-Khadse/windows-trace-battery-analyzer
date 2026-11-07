import json
from pathlib import Path

import pytest
from hypothesis import given
from hypothesis import strategies as st

from wtba.ingest import load_bundle
from wtba.scrub import (
    USER_PATH_RE,
    SaltError,
    hash_machine_id,
    scrub_bundle,
    scrub_process_name,
    scrub_text,
)

FIXTURES = Path(__file__).resolve().parents[2] / "fixtures"

names = st.text(
    alphabet=st.characters(
        blacklist_characters='\\/:*?"<>|', blacklist_categories=("Cs", "Cc")
    ),
    min_size=1,
    max_size=12,
)
seps = st.sampled_from(["\\", "/", "\\\\"])
drives = st.sampled_from(["C", "c", "D", "z"])
users_word = st.sampled_from(["Users", "users", "USERS"])
noise = st.text(max_size=20)


@given(noise, drives, seps, users_word, seps, names, noise)
def test_no_user_path_survives(
    pre: str, drive: str, s1: str, u: str, s2: str, name: str, post: str
) -> None:
    text = f"{pre}{drive}:{s1}{u}{s2}{name}{post}"
    assert USER_PATH_RE.search(scrub_text(text)) is None


@given(st.lists(st.one_of(noise, names), max_size=6), names)
def test_nested_and_repeated(parts: list[str], name: str) -> None:
    text = (
        "C:\\Users\\" + name + "\\" + "".join(parts) + "C:\\Users\\C:\\Users\\" + name
    )
    assert USER_PATH_RE.search(scrub_text(text)) is None


def test_known_examples() -> None:
    assert scrub_text(r"C:\Users\Bob\AppData\x.exe") == r"<USER>\AppData\x.exe"
    assert scrub_text("c:/users/Bob Smith/x") == "<USER>/x"
    assert scrub_text("no pii here") == "no pii here"


@given(names, names)
def test_process_name_has_no_path_or_user(user: str, exe: str) -> None:
    out = scrub_process_name(f"C:\\Users\\{user}\\app\\{exe}")
    assert "\\" not in out and "/" not in out
    assert USER_PATH_RE.search(out) is None
    assert scrub_process_name("msedge.exe") == "msedge.exe"


def test_hash_is_salted_and_stable() -> None:
    a = hash_machine_id("abc12345", "s1")
    assert a == hash_machine_id("abc12345", "s1")
    assert a != hash_machine_id("abc12345", "s2")
    assert a != "abc12345"
    assert len(a) == 64


def test_scrub_bundle_fixture() -> None:
    src = load_bundle(FIXTURES / "bundle_edge_A_01.json")
    out = scrub_bundle(src, salt="test-salt")
    blob = json.dumps(out)
    assert "FakePerson" not in blob
    assert "test-salt" not in blob
    assert out["machine"]["id_hash"] == hash_machine_id("FAKEMACHINE0002", "test-salt")
    assert out["processes"][0]["name"] == "fakebrowser.exe"
    assert out["processes"][0]["pid"] == 0
    assert "<USER>" in out["label"]
    assert "FakePerson" in json.dumps(src)  # original not mutated into output
    assert src["processes"][0]["pid"] == 4444


def test_salt_required(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("WTBA_HASH_SALT", raising=False)
    src = load_bundle(FIXTURES / "bundle_idle_A_01.json")
    with pytest.raises(SaltError):
        scrub_bundle(src)
    monkeypatch.setenv("WTBA_HASH_SALT", "from-env")
    assert scrub_bundle(src)["machine"]["id_hash"] == hash_machine_id(
        "FAKEMACHINE0001", "from-env"
    )


def test_salt_error_does_not_leak_value(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("WTBA_HASH_SALT", "")
    with pytest.raises(SaltError) as ei:
        scrub_bundle(load_bundle(FIXTURES / "bundle_idle_A_01.json"))
    assert "WTBA_HASH_SALT" in str(ei.value)
