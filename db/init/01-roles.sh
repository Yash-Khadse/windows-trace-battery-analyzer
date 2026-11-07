#!/bin/sh
# Runs once, on first start of a fresh volume, as the bootstrap admin (POSTGRES_USER).
# Names/passwords reach SQL only as psql variables (:"ident" / :'literal'); no shell
# interpolation into SQL text.
set -eu

NL='
'
CR="$(printf '\r')"
for var in WTBA_DB_USER WTBA_DB_PASSWORD WTBA_DB_RO_USER WTBA_DB_RO_PASSWORD; do
  eval "val=\${$var:-}"
  if [ -z "$val" ]; then
    echo "01-roles.sh: $var is empty" >&2
    exit 1
  fi
  case "$val" in
    *"$NL"*|*"$CR"*)
      echo "01-roles.sh: $var must not contain a newline or carriage return" >&2
      exit 1
      ;;
  esac
done

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v db="$POSTGRES_DB" \
  -v rw_user="$WTBA_DB_USER" -v rw_pass="$WTBA_DB_PASSWORD" \
  -v ro_user="$WTBA_DB_RO_USER" -v ro_pass="$WTBA_DB_RO_PASSWORD" <<'EOSQL'
CREATE ROLE :"rw_user" LOGIN PASSWORD :'rw_pass' NOSUPERUSER NOCREATEDB NOCREATEROLE;
CREATE ROLE :"ro_user" LOGIN PASSWORD :'ro_pass' NOSUPERUSER NOCREATEDB NOCREATEROLE;
ALTER DATABASE :"db" OWNER TO :"rw_user";
REVOKE ALL ON DATABASE :"db" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"db" TO :"rw_user", :"ro_user";
GRANT USAGE ON SCHEMA public TO :"ro_user";
GRANT SELECT ON ALL TABLES IN SCHEMA public TO :"ro_user";
ALTER DEFAULT PRIVILEGES FOR ROLE :"rw_user" IN SCHEMA public GRANT SELECT ON TABLES TO :"ro_user";
EOSQL
