"""`wtba` command line interface."""

from __future__ import annotations

import os
from pathlib import Path
from typing import Annotated

import typer
from sqlalchemy import create_engine
from sqlalchemy.orm import Session

from .ingest import BundleError, load_bundle
from .load import bundle_to_rows, load_rows
from .scrub import SaltError, scrub_bundle

app = typer.Typer(help="WTBA pipeline", no_args_is_help=True, add_completion=False)


@app.callback()
def _root() -> None:
    """WTBA pipeline."""


@app.command()
def ingest(
    bundle: Annotated[Path, typer.Argument(help="Path to bundle.json")],
    dry_run: Annotated[
        bool, typer.Option("--dry-run", help="No DB; print row counts")
    ] = False,
) -> None:
    """Validate, scrub and load a run bundle."""
    try:
        data = scrub_bundle(load_bundle(bundle))
    except (BundleError, SaltError) as exc:
        typer.echo(f"error: {exc}", err=True)
        raise typer.Exit(code=2) from exc
    rows = bundle_to_rows(data)
    summary = ", ".join(f"{k}={v}" for k, v in rows.counts().items())
    if dry_run:
        typer.echo(f"dry-run: run {rows.run.run_id} would write {summary}")
        return
    url = os.environ.get("WTBA_DATABASE_URL")
    if not url:
        typer.echo(
            "error: WTBA_DATABASE_URL is not set (use --dry-run for no DB)", err=True
        )
        raise typer.Exit(code=2)
    run_id = rows.run.run_id  # read before commit: ORM attributes expire afterwards
    engine = create_engine(url)
    with Session(engine) as session, session.begin():
        inserted = load_rows(session, rows)
    if inserted:
        typer.echo(f"loaded run {run_id}: {summary}")
    else:
        typer.echo(f"run {run_id} already loaded; nothing written")
