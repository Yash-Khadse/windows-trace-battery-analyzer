import json
from pathlib import Path

import pytest

from wtba.ingest import SCHEMA_PATH, BundleError, load_bundle, validate_bundle

FIXTURES = Path(__file__).resolve().parents[2] / "fixtures"
VALID = sorted(FIXTURES.glob("bundle_*.json"))
INVALID = sorted(FIXTURES.glob("invalid_*.json"))


def test_fixture_counts() -> None:
    assert len(VALID) >= 3
    assert len(INVALID) >= 3


def test_packaged_schema_matches_docs() -> None:
    docs = SCHEMA_PATH.parents[3].parent / "docs" / "bundle.schema.json"
    if docs.exists():
        assert json.loads(docs.read_text("utf-8")) == json.loads(
            SCHEMA_PATH.read_text("utf-8")
        )


@pytest.mark.parametrize("path", VALID, ids=lambda p: p.name)
def test_valid_bundles_load(path: Path) -> None:
    assert load_bundle(path)["schema"] == 1


@pytest.mark.parametrize("path", INVALID, ids=lambda p: p.name)
def test_invalid_bundles_rejected(path: Path) -> None:
    with pytest.raises(BundleError) as ei:
        load_bundle(path)
    assert ei.value.problems


def test_error_messages_name_the_field() -> None:
    with pytest.raises(BundleError) as ei:
        load_bundle(FIXTURES / "invalid_bad_condition.json")
    assert any(p.startswith("condition") for p in ei.value.problems)
    with pytest.raises(BundleError) as ei:
        load_bundle(FIXTURES / "invalid_negative_values.json")
    text = str(ei.value)
    assert "processes/0/cpu_ms" in text
    assert "run_id" in text


def test_not_json_and_missing_file(tmp_path: Path) -> None:
    bad = tmp_path / "x.json"
    bad.write_text("{nope", encoding="utf-8")
    with pytest.raises(BundleError, match="not valid JSON"):
        load_bundle(bad)
    with pytest.raises(BundleError, match="cannot read"):
        load_bundle(tmp_path / "absent.json")


def test_non_object_rejected() -> None:
    with pytest.raises(BundleError):
        validate_bundle([1, 2])


def test_extra_property_rejected() -> None:
    data = load_bundle(VALID[0])
    data["command_line"] = "secret"
    with pytest.raises(BundleError):
        validate_bundle(data)
