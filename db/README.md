# WTBA database

Isolated Postgres 16 in Docker, bound to 127.0.0.1:5433, named volume `wtba_pgdata`.

Roles: `wtba_admin` (bootstrap superuser, init only), `wtba_rw` (owns the database,
runs migrations and ingest), `wtba_ro` (SELECT only, used by the API).

## Recreating the container with the new role layout

Init scripts only run on a fresh volume. `docker compose down -v` DESTROYS the
volume: all loaded data is lost and must be re-ingested from the bundles
(`wtba ingest <bundle.json>`). Steps:

1. Add `WTBA_DB_ADMIN_USER` / `WTBA_DB_ADMIN_PASSWORD` to `.env` (see `.env.example`).
2. `docker compose down -v` (data loss, as above), then `docker compose up -d`.
3. Run `alembic upgrade head` as `wtba_rw`, then re-ingest.

Passwords must not contain newlines; the init script fails if they do.
