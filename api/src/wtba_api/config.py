"""Environment configuration. Never logs or echoes the database URL."""

from __future__ import annotations

import os
from collections.abc import Mapping
from dataclasses import dataclass, field

from sqlalchemy.engine import make_url

from .security import DEFAULT_HOSTS

DEFAULT_CORS = "http://localhost:5173,http://127.0.0.1:5173"


class ConfigError(RuntimeError):
    """Configuration problem; messages never contain credentials."""


@dataclass(frozen=True)
class Settings:
    database_url: str = field(repr=False)
    cors_origins: tuple[str, ...] = (
        "http://localhost:5173",
        "http://127.0.0.1:5173",
    )

    allowed_hosts: tuple[str, ...] = DEFAULT_HOSTS

    def __repr__(self) -> str:
        return f"Settings(cors_origins={self.cors_origins!r})"

    @classmethod
    def from_env(cls, env: Mapping[str, str] | None = None) -> Settings:
        e = os.environ if env is None else env
        raw = e.get("WTBA_API_DATABASE_URL", "").strip()
        if not raw:
            raise ConfigError("WTBA_API_DATABASE_URL is not set")
        origins = tuple(
            o.strip().rstrip("/")
            for o in e.get("WTBA_API_CORS_ORIGINS", DEFAULT_CORS).split(",")
            if o.strip()
        )
        if any("*" in o for o in origins):
            raise ConfigError("WTBA_API_CORS_ORIGINS must not contain wildcards")
        extra = tuple(
            h.strip().lower()
            for h in e.get("WTBA_API_ALLOWED_HOSTS", "").split(",")
            if h.strip()
        )
        if any("*" in h for h in extra):
            raise ConfigError("WTBA_API_ALLOWED_HOSTS must not contain wildcards")
        return cls(
            database_url=normalize_url(raw),
            cors_origins=origins,
            allowed_hosts=DEFAULT_HOSTS + extra,
        )


def normalize_url(raw: str) -> str:
    """Validate the URL and force the psycopg (v3) driver, without echoing it."""
    try:
        url = make_url(raw)
    except Exception:
        raise ConfigError("WTBA_API_DATABASE_URL is not a valid database URL") from None
    if url.get_backend_name() != "postgresql":
        raise ConfigError("WTBA_API_DATABASE_URL must be a PostgreSQL URL")
    return url.set(drivername="postgresql+psycopg").render_as_string(
        hide_password=False
    )
