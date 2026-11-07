"""Console entry point: uvicorn bound to 127.0.0.1 only."""

from __future__ import annotations

import os
import sys

import uvicorn

from .app import create_app
from .config import ConfigError, Settings

HOST = "127.0.0.1"


def main() -> None:
    requested = os.environ.get("WTBA_API_HOST")
    if requested is not None and requested.strip() != HOST:
        print(f"refusing to start: the API only binds to {HOST}", file=sys.stderr)
        raise SystemExit(2)
    try:
        port = int(os.environ.get("WTBA_API_PORT", "8000"))
        if not 1 <= port <= 65535:
            raise ValueError
    except ValueError:
        print("WTBA_API_PORT must be an integer 1-65535", file=sys.stderr)
        raise SystemExit(2) from None
    try:
        app = create_app(Settings.from_env())
    except ConfigError as exc:
        print(f"configuration error: {exc}", file=sys.stderr)
        raise SystemExit(2) from None
    uvicorn.run(app, host=HOST, port=port, log_level="info")
