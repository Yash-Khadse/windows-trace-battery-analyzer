"""Read-only FastAPI service for WTBA (SPEC section 10)."""

from .app import create_app
from .cli import main

__all__ = ["create_app", "main"]
