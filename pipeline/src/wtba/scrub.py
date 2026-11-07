"""PII scrubbing (SPEC S9): user paths removed, machine id salted-hashed."""

from __future__ import annotations

import copy
import hashlib
import os
import re
from typing import Any

SALT_ENV = "WTBA_HASH_SALT"
ALLOW_TEST_SALT_ENV = "WTBA_ALLOW_TEST_SALT"
MIN_SALT_LEN = 16
TEST_SALT = "ci-only-salt"

_SEP = r"[\\/]"
_NAME = r'[^\\/:*?"<>|\r\n]+'
# Optional roots: drive letter, UNC admin share (\\host\c$), NT device path.
_ROOT = (
    r"(?:[A-Za-z]:"
    rf"|{_SEP}{{2}}[^\\/\s]+{_SEP}+[A-Za-z]\$"
    rf"|{_SEP}+Device{_SEP}+HarddiskVolume\d+)?"
)
_HOME = "(?:Users|Documents and Settings)"
USER_PATH_RE = re.compile(
    rf"{_ROOT}{_SEP}+{_HOME}(?:{_SEP}+{_NAME})?|{_SEP}+home{_SEP}+{_NAME}",
    re.IGNORECASE,
)
_ENV_VARS = (
    "USERPROFILE|USERNAME|USERDOMAIN|HOMEPATH|HOMEDRIVE|HOME|APPDATA|LOCALAPPDATA|"
    "COMPUTERNAME|LOGONSERVER|USER"
)
ENV_REF_RE = re.compile(
    rf"%(?:{_ENV_VARS})%|\$env:(?:{_ENV_VARS})\b|\$\{{?env:(?:{_ENV_VARS})\}}?",
    re.IGNORECASE,
)
UNC_HOST_RE = re.compile(r"(?<![\w:\\])\\\\(?![?.])[^\\/\s]+(?=\\)")
EMAIL_RE = re.compile(r"[\w.+-]+@[\w-]+(?:\.[\w-]+)+")
SHORT_NAME_RE = re.compile(r"\b[A-Za-z0-9_]{1,8}~\d+\b")
_SEP_RE = re.compile(r"[\\/]")


class SaltError(RuntimeError):
    """Raised when the hash salt is missing or weak."""


def check_salt(salt: str) -> str:
    """Reject empty, placeholder and short salts. Never includes the salt in messages."""
    testing = os.environ.get(ALLOW_TEST_SALT_ENV) == "1"
    if not salt:
        raise SaltError(f"{SALT_ENV} must be set to a non-empty secret")
    if salt.lower().startswith("change-me"):
        raise SaltError(
            f"{SALT_ENV} is still a 'change-me' placeholder; set a random secret"
        )
    if salt == TEST_SALT:
        if not testing:
            raise SaltError(
                f"the test salt is only allowed when {ALLOW_TEST_SALT_ENV}=1"
            )
        return salt
    if len(salt) < MIN_SALT_LEN and not testing:
        raise SaltError(f"{SALT_ENV} must be at least {MIN_SALT_LEN} characters")
    return salt


def get_salt() -> str:
    return check_salt(os.environ.get(SALT_ENV, ""))


def _known_identifiers() -> list[tuple[re.Pattern[str], str]]:
    """Patterns for the local user and host names (sound: exact, whole-token matches)."""
    out: list[tuple[re.Pattern[str], str]] = []
    for var, repl in (
        ("USERNAME", "<USER>"),
        ("USER", "<USER>"),
        ("COMPUTERNAME", "<HOST>"),
        ("HOSTNAME", "<HOST>"),
    ):
        val = os.environ.get(var, "").strip()
        if len(val) >= 4:
            out.append(
                (re.compile(rf"(?<![\w]){re.escape(val)}(?![\w])", re.IGNORECASE), repl)
            )
    return out


def scrub_text(text: str) -> str:
    """Remove user paths, env-var references, UNC hosts and e-mail addresses."""
    prev = None
    while prev != text:
        prev = text
        text = USER_PATH_RE.sub("<USER>", text)
        text = ENV_REF_RE.sub("<USER>", text)
        text = UNC_HOST_RE.sub(lambda _m: "\\\\<HOST>", text)
        text = EMAIL_RE.sub("<EMAIL>", text)
        text = SHORT_NAME_RE.sub("<USER>", text)
    return text


def scrub_free_text(text: str) -> str:
    """scrub_text plus exact local user/host names (for labels, cpu, os strings)."""
    text = scrub_text(text)
    for pat, repl in _known_identifiers():
        text = pat.sub(repl, text)
    return text


def scrub_process_name(name: str) -> str:
    """Process identity is the image name: drop any directory part and user paths."""
    cleaned = scrub_text(name)
    if _SEP_RE.search(cleaned):
        cleaned = _SEP_RE.split(cleaned)[-1]
    return cleaned or "<unknown>"


def hash_machine_id(id_hash: str, salt: str) -> str:
    return hashlib.sha256(f"{salt}\0{id_hash}".encode()).hexdigest()


def scrub_bundle(bundle: dict[str, Any], salt: str | None = None) -> dict[str, Any]:
    """Return a scrubbed deep copy of a validated bundle. Never logs the salt."""
    salt = get_salt() if salt is None else check_salt(salt)
    out = copy.deepcopy(bundle)
    out["label"] = scrub_free_text(out["label"])
    out["scenario"] = scrub_free_text(out["scenario"])
    machine = out["machine"]
    machine["id_hash"] = hash_machine_id(machine["id_hash"], salt)
    machine["cpu"] = scrub_free_text(machine["cpu"])
    machine["os_build"] = scrub_free_text(machine["os_build"])
    out["power"]["plan_guid"] = scrub_free_text(out["power"]["plan_guid"])
    for proc in out["processes"]:
        proc["name"] = scrub_process_name(proc["name"])
        proc["pid"] = 0  # pids are dropped at ingest
    return out
