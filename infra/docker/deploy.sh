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
for key in POSTGRES_PASSWORD KEYCLOAK_ADMIN_PASSWORD KEYCLOAK_CLIENT_SECRET SESSION_SECRET; do
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
KEYCLOAK_ADMIN_PASSWORD=$(get KEYCLOAK_ADMIN_PASSWORD)
KEYCLOAK_CLIENT_SECRET=$(get KEYCLOAK_CLIENT_SECRET)
SESSION_SECRET=$(get SESSION_SECRET)
EOF
umask 022

mkdir -p /data/caddy/data /data/caddy/config /data/postgres

echo "building images..."
docker compose build --pull
docker compose up -d --remove-orphans

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
