#!/usr/bin/env bash
# Provision the porchlight realm in Keycloak. Idempotent: run on every deploy
# (and by ./dev.sh); each object is looked up, then created or updated in place.
# A realm import file would only ever apply to the first boot, so every later
# change would have to be made by hand in the admin console. This file is the
# realm. (The same approach as topspin's infra/docker/init-keycloak.sh.)
#
#   init-keycloak.sh dev    seeded user dev/dev, Keycloak's own login form,
#                           redirects to http://localhost:5173
#   init-keycloak.sh prod   Google is the only way in and Keycloak never shows a
#                           page: the browser flow redirects straight to Google,
#                           the password form is off, no first-login prompts
#
# Environment:
#   COMPOSE                 how to reach the stack, e.g. "docker compose -f x.yml"
#   KEYCLOAK_ADMIN / KEYCLOAK_ADMIN_PASSWORD   bootstrap admin (dev: admin/admin)
#   APP_URL                 prod only, e.g. https://porchlight.joebad.com
#   KEYCLOAK_CLIENT_SECRET  the auth service's client secret (dev: porchlight-dev-secret)
#   GOOGLE_CLIENT_ID / GOOGLE_CLIENT_SECRET    prod only

set -euo pipefail

MODE="${1:?usage: init-keycloak.sh dev|prod}"
REALM="${REALM:-porchlight}"
CLIENT_ID=porchlight-web
COMPOSE="${COMPOSE:-docker compose}"
ADMIN="${KEYCLOAK_ADMIN:-admin}"
ADMIN_PASSWORD="${KEYCLOAK_ADMIN_PASSWORD:-admin}"

case "$MODE" in
  dev)
    APP_URL="${APP_URL:-http://localhost:5173}"
    CLIENT_SECRET="${KEYCLOAK_CLIENT_SECRET:-porchlight-dev-secret}"
    SSL_REQUIRED=none ;;
  prod)
    APP_URL="${APP_URL:?APP_URL is required in prod}"
    CLIENT_SECRET="${KEYCLOAK_CLIENT_SECRET:?KEYCLOAK_CLIENT_SECRET is required in prod}"
    SSL_REQUIRED=external ;;
  *) echo "unknown mode: $MODE" >&2; exit 2 ;;
esac

# kcadm inside the Keycloak container, so nothing needs installing on the host.
kc() { $COMPOSE exec -T keycloak /opt/keycloak/bin/kcadm.sh "$@"; }

# --- Log in (Keycloak may still be starting) ---------------------------------
for i in $(seq 1 60); do
  if kc config credentials --server http://localhost:8080 --realm master \
       --user "$ADMIN" --password "$ADMIN_PASSWORD" >/dev/null 2>&1; then
    break
  fi
  [ "$i" = 60 ] && { echo "Keycloak did not accept the admin login" >&2; exit 1; }
  sleep 3
done
echo "logged in to Keycloak"

# --- Realm -------------------------------------------------------------------
REALM_ARGS=(
  -s "displayName=Porchlight"
  -s "enabled=true"
  -s "sslRequired=$SSL_REQUIRED"
  -s "registrationAllowed=false"
  -s "resetPasswordAllowed=false"
  -s "rememberMe=false"
  # Stay signed in for a fortnight of inactivity, a month at most. The app keeps
  # its session for exactly as long (refresh_expires_in), then goes round again
  # silently.
  -s "ssoSessionIdleTimeout=1209600"
  -s "ssoSessionMaxLifespan=2592000"
)
if kc get "realms/$REALM" --fields realm >/dev/null 2>&1; then
  kc update "realms/$REALM" "${REALM_ARGS[@]}"
  echo "realm $REALM updated"
else
  kc create realms -s "realm=$REALM" "${REALM_ARGS[@]}" >/dev/null
  echo "realm $REALM created"
fi

# --- The web client ----------------------------------------------------------
# Confidential: the auth service (server/) redeems the code server-side with
# this secret, plus PKCE, and keeps the result in an HttpOnly cookie. The page
# never holds a token. The secret is ours (deploy.sh generates it and keeps it
# in Secrets Manager) rather than Keycloak's, so nothing has to be read back.
CLIENT_ARGS=(
  -s "clientId=$CLIENT_ID"
  -s "name=Porchlight"
  -s "enabled=true"
  -s "publicClient=false"
  -s "clientAuthenticatorType=client-secret"
  -s "secret=$CLIENT_SECRET"
  -s "standardFlowEnabled=true"
  -s "implicitFlowEnabled=false"
  -s "directAccessGrantsEnabled=false"
  -s "serviceAccountsEnabled=false"
  -s "frontchannelLogout=true"
  # Exact, not a wildcard: the one callback the auth service serves.
  -s "redirectUris=[\"$APP_URL/auth/callback\"]"
  -s "webOrigins=[]"
  -s "attributes.\"pkce.code.challenge.method\"=S256"
  -s "attributes.\"post.logout.redirect.uris\"=$APP_URL/*"
)
CLIENT_UUID=$(kc get clients -r "$REALM" -q "clientId=$CLIENT_ID" --fields id --format csv --noquotes | head -n1 | tr -d '\r')
if [ -n "$CLIENT_UUID" ]; then
  kc update "clients/$CLIENT_UUID" -r "$REALM" "${CLIENT_ARGS[@]}"
  echo "client $CLIENT_ID updated"
else
  kc create clients -r "$REALM" "${CLIENT_ARGS[@]}" >/dev/null
  echo "client $CLIENT_ID created"
fi

# --- Required actions --------------------------------------------------------
# Nothing should interrupt a sign-in to ask for something. Google supplies the
# email and name and there are no passwords in prod, so every prompt Keycloak
# could insert (VERIFY_PROFILE is the one that actually fires, asking for a
# first and last name) would have nothing behind it.
for action in VERIFY_PROFILE UPDATE_PROFILE VERIFY_EMAIL UPDATE_PASSWORD; do
  kc update "authentication/required-actions/$action" -r "$REALM" \
    -s "enabled=false" -s "defaultAction=false" >/dev/null 2>&1 || true
done
echo "first-login prompts off"

if [ "$MODE" = dev ]; then
  # --- Seeded user -----------------------------------------------------------
  USER_ID=$(kc get users -r "$REALM" -q username=dev -q exact=true --fields id --format csv --noquotes | head -n1 | tr -d '\r')
  USER_ARGS=(-s "username=dev" -s "enabled=true" -s "email=dev@porchlight.local" -s "emailVerified=true"
    -s "firstName=Dev" -s "lastName=Player")
  if [ -n "$USER_ID" ]; then
    kc update "users/$USER_ID" -r "$REALM" "${USER_ARGS[@]}"
  else
    kc create users -r "$REALM" "${USER_ARGS[@]}" >/dev/null
  fi
  kc set-password -r "$REALM" --username dev --new-password dev
  echo "seeded user: dev / dev"
  exit 0
fi

# --- prod: Google -----------------------------------------------------------
if [ -z "${GOOGLE_CLIENT_ID:-}" ] || [ -z "${GOOGLE_CLIENT_SECRET:-}" ]; then
  echo "WARNING: GOOGLE_CLIENT_ID/SECRET not set; Google sign-in not provisioned" >&2
  exit 0
fi

IDP_ARGS=(
  -s "alias=google"
  -s "providerId=google"
  -s "displayName=Google"
  -s "enabled=true"
  # Google has verified the address; don't send a verification email.
  -s "trustEmail=true"
  -s "storeToken=false"
  -s "config.clientId=$GOOGLE_CLIENT_ID"
  -s "config.clientSecret=$GOOGLE_CLIENT_SECRET"
  -s "config.defaultScope=openid email profile"
  # Google is the source of truth for the profile: refresh it on every sign-in,
  # so Keycloak never finds a field missing and asks the user to fill it in.
  -s "config.syncMode=FORCE"
)
if kc get "identity-provider/instances/google" -r "$REALM" --fields alias >/dev/null 2>&1; then
  kc update "identity-provider/instances/google" -r "$REALM" "${IDP_ARGS[@]}"
  echo "identity provider google updated"
else
  kc create identity-provider/instances -r "$REALM" "${IDP_ARGS[@]}" >/dev/null
  echo "identity provider google created"
fi

# --- prod: straight to Google, for every request -----------------------------
# The app sends kc_idp_hint=google, but a reload of a login-actions URL or an
# expired session re-enters the browser flow without it. Giving the flow's
# identity-provider-redirector a default makes the redirect unconditional, and
# disabling the forms subflow means the username/password page (which could not
# log anyone in: there are no passwords) is never shown. Both are one field to
# put back; the master realm's admin console is unaffected.
EXECS=$(kc get "authentication/flows/browser/executions" -r "$REALM")
read -r REDIRECTOR_ID REDIRECTOR_CFG FORMS_ID < <(python3 -c '
import json, sys
execs = json.loads(sys.stdin.read())
red = next((e for e in execs if e.get("providerId") == "identity-provider-redirector"), {})
forms = next((e for e in execs if (e.get("displayName") or "").lower() == "forms" and e.get("level") == 0), {})
print(red.get("id", "-"), red.get("authenticationConfig") or "-", forms.get("id", "-"))
' <<<"$EXECS")

if [ "$REDIRECTOR_ID" = "-" ]; then
  echo "WARNING: no identity-provider-redirector in the browser flow" >&2
elif [ "$REDIRECTOR_CFG" != "-" ]; then
  kc update "authentication/config/$REDIRECTOR_CFG" -r "$REALM" -s "alias=google-only" -s "config.defaultProvider=google"
  echo "browser flow goes straight to Google"
else
  kc create "authentication/executions/$REDIRECTOR_ID/config" -r "$REALM" \
    -s "alias=google-only" -s "config.defaultProvider=google" >/dev/null
  echo "browser flow goes straight to Google"
fi

if [ "$FORMS_ID" != "-" ]; then
  # -n -f -: send exactly these fields (kcadm's -s does a read-modify-write
  # against the collection, which this endpoint rejects).
  echo "{\"id\":\"$FORMS_ID\",\"requirement\":\"DISABLED\"}" \
    | kc update "authentication/flows/browser/executions" -r "$REALM" -n -f - >/dev/null
  echo "username/password form disabled"
fi

echo "realm $REALM ready ($MODE)"
