"""Load and validate a run bundle against the bundle JSON schema."""

from __future__ import annotations

import datetime as dt
import json
import math
from functools import lru_cache
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator, FormatChecker

SCHEMA_PATH = Path(__file__).with_name("bundle.schema.json")
MAX_BUNDLE_BYTES = 20 * 1024 * 1024


class BundleError(ValueError):
    """Raised when a bundle cannot be read or fails validation."""

    def __init__(self, message: str, problems: list[str] | None = None) -> None:
        self.problems = problems or []
        detail = "".join(f"\n  - {p}" for p in self.problems)
        super().__init__(f"{message}{detail}")


@lru_cache(maxsize=1)
def _validator() -> Draft202012Validator:
    schema = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
    return Draft202012Validator(schema, format_checker=FormatChecker())


def parse_utc(value: str) -> dt.datetime:
    """Parse an RFC 3339 date-time (offset required); raise BundleError otherwise."""
    try:
        parsed = dt.datetime.fromisoformat(value)
    except (ValueError, TypeError) as exc:
        raise BundleError("started_utc is not a valid ISO-8601 date-time") from exc
    if parsed.tzinfo is None or parsed.utcoffset() is None:
        raise BundleError("started_utc must include a UTC offset or 'Z'")
    return parsed


def _find_non_finite(node: Any, path: str = "") -> str | None:
    if isinstance(node, float) and not math.isfinite(node):
        return path or "<root>"
    if isinstance(node, dict):
        for k, v in node.items():
            hit = _find_non_finite(v, f"{path}/{k}")
            if hit:
                return hit
    elif isinstance(node, list):
        for i, v in enumerate(node):
            hit = _find_non_finite(v, f"{path}/{i}")
            if hit:
                return hit
    return None


def _reject_constant(name: str) -> Any:
    raise ValueError(f"non-finite number {name} is not allowed")


def validate_bundle(data: Any) -> dict[str, Any]:
    """Validate an already-parsed bundle; return it, or raise BundleError."""
    if not isinstance(data, dict):
        raise BundleError("bundle must be a JSON object")
    problems = []
    for err in sorted(
        _validator().iter_errors(data), key=lambda e: [str(p) for p in e.path]
    ):
        where = "/".join(str(p) for p in err.path) or "<root>"
        problems.append(f"{where}: {err.message}")
    if problems:
        raise BundleError("bundle failed schema validation", problems)
    bad = _find_non_finite(data)
    if bad:
        raise BundleError("bundle contains a non-finite number", [bad])
    parse_utc(data["started_utc"])
    return data


def load_bundle(path: str | Path) -> dict[str, Any]:
    """Read a bundle JSON file and validate it."""
    p = Path(path)
    try:
        with p.open("rb") as fh:
            raw = fh.read(MAX_BUNDLE_BYTES + 1)
        text = raw.decode("utf-8")
    except OSError as exc:
        raise BundleError(f"cannot read bundle file {p.name}: {exc.strerror}") from exc
    except UnicodeDecodeError as exc:
        raise BundleError(f"{p.name} is not valid UTF-8") from exc
    if len(raw) > MAX_BUNDLE_BYTES:
        raise BundleError(
            f"{p.name} exceeds the {MAX_BUNDLE_BYTES // (1024 * 1024)} MB limit"
        )
    try:
        data = json.loads(text, parse_constant=_reject_constant)
    except ValueError as exc:
        if isinstance(exc, json.JSONDecodeError):
            raise BundleError(
                f"{p.name} is not valid JSON: {exc.msg} (line {exc.lineno})"
            ) from exc
        raise BundleError(f"{p.name}: {exc}") from exc
    return validate_bundle(data)
