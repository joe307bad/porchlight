#!/usr/bin/env bash
# Bring the stack up to date on the box. Run by the deploy workflow over SSH,
# after it has copied infra/, server/ and the built site to /opt/porchlight.
# Safe to run again by hand:
#
#   SECRET_ID=porchlight/prod AWS_REGION=us-east-1 DOMAIN=porchlight.joebad.com \
#     /opt/porchlight/infra/docker/deploy.sh
#
# Secrets live in Secrets Manager. The ones nobody needs to choose (database
# and admin passwords, the client and session secrets) are generated here the
# first time and written back, so they exist in exactly one place.

set -euo pipefail

: "${SECRET_ID:?}" "${AWS_REGION:?}" "${DOMAIN:?}"
cd "$(dirname "$0")"

SECRET=$(aws secretsmanager get-secret-value --secret-id "$SECRET_ID" --region "$AWS_REGION" \
  --query SecretString --output text)
get() { jq -r --arg k "$1" '.[$k] // empty' <<<"$SECRET"; }

generated=0
for key in POSTGRES_PASSWORD GAME_DB_PASSWORD KEYCLOAK_ADMIN_PASSWORD KEYCLOAK_CLIENT_SECRET SESSION_SECRET; do
  if [ -z "$(get "$key")" ]; then
    SECRET=$(jq --arg k "$key" --arg v "$(openssl rand -hex 32)" '.[$k] = $v' <<<"$SECRET")
    generated=1
    echo "generated $key"
  fi
done
if [ "$generated" = 1 ]; then
  aws secretsmanager put-secret-value --secret-id "$SECRET_ID" --region "$AWS_REGION" \
    --secret-string "$SECRET" >/dev/null
fi

umask 077
cat > .env <<EOF
DOMAIN=$DOMAIN
POSTGRES_PASSWORD=$(get POSTGRES_PASSWORD)
GAME_DB_PASSWORD=$(get GAME_DB_PASSWORD)
KEYCLOAK_ADMIN_PASSWORD=$(get KEYCLOAK_ADMIN_PASSWORD)
KEYCLOAK_CLIENT_SECRET=$(get KEYCLOAK_CLIENT_SECRET)
SESSION_SECRET=$(get SESSION_SECRET)
EOF
umask 022

mkdir -p /data/caddy/data /data/caddy/config /data/postgres

echo "building images..."
docker compose build --pull
docker compose up -d --remove-orphans

# Caddy's Caddyfile is bind-mounted as a single file, and rsync replaces the
# file rather than editing it, so a running Caddy keeps the old one (and
# `up -d` sees no change to recreate it for). Restart it when they differ.
running=$(docker exec porchlight-caddy cat /etc/caddy/Caddyfile 2>/dev/null | sha256sum || true)
if [ "$running" != "$(sha256sum < Caddyfile)" ]; then
  echo "Caddyfile changed: restarting Caddy"
  docker compose up -d --force-recreate caddy
fi

echo "waiting for Postgres..."
for i in $(seq 1 60); do
  status=$(docker inspect --format '{{.State.Health.Status}}' porchlight-postgres 2>/dev/null || echo starting)
  [ "$status" = healthy ] && break
  [ "$i" = 60 ] && { echo "Postgres did not become healthy" >&2; docker compose logs --tail 40 postgres >&2; exit 1; }
  sleep 2
done

# The game's database and role, beside Keycloak's. Idempotent; the password is
# reset every deploy so it always matches Secrets Manager. (The volume was
# initialised long ago, so the image's init scripts would never run.) The game
# server creates its own tables on start (Drizzle migrations, server/drizzle).
docker exec -i porchlight-postgres psql -v ON_ERROR_STOP=1 -U keycloak -d keycloak \
  -v pw="$(get GAME_DB_PASSWORD)" >/dev/null <<'SQL'
SELECT 'CREATE ROLE porchlight LOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'porchlight')\gexec
ALTER ROLE porchlight WITH LOGIN PASSWORD :'pw';
SELECT 'CREATE DATABASE porchlight OWNER porchlight' WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'porchlight')\gexec
SQL

echo "waiting for Keycloak..."
for i in $(seq 1 80); do
  status=$(docker inspect --format '{{.State.Health.Status}}' porchlight-keycloak 2>/dev/null || echo starting)
  [ "$status" = healthy ] && break
  [ "$i" = 80 ] && { echo "Keycloak did not become healthy" >&2; docker compose logs --tail 80 keycloak >&2; exit 1; }
  sleep 5
done

COMPOSE="docker compose" \
KEYCLOAK_ADMIN=admin \
KEYCLOAK_ADMIN_PASSWORD="$(get KEYCLOAK_ADMIN_PASSWORD)" \
KEYCLOAK_CLIENT_SECRET="$(get KEYCLOAK_CLIENT_SECRET)" \
APP_URL="https://$DOMAIN" \
GOOGLE_CLIENT_ID="$(get GOOGLE_CLIENT_ID)" \
GOOGLE_CLIENT_SECRET="$(get GOOGLE_CLIENT_SECRET)" \
  ../keycloak/init-keycloak.sh prod

# The box has 12 GB of disk; old image layers are the only thing that grows.
docker image prune -f >/dev/null

docker compose ps
echo "deployed https://$DOMAIN"
