# Porchlight infrastructure

Sign-in works the way it does in topspin: Keycloak sits in front of Google but the user never sees it. Everything runs on one small EC2 box.

```
browser ──https──> Caddy ──┬── /            static app (app/dist, built in CI)
                           ├── /auth/*      auth service (server/) ──back channel──┐
                           └── auth.<domain> Keycloak ── Postgres                  │
                                               ▲──────────────────────────────────┘
```

## How sign-in works

1. The page asks `GET /auth/me`. A 401 shows the sign-in screen.
2. **Sign in with Google** goes to `/auth/login`. The auth service sends the browser to Keycloak with `kc_idp_hint=google`. Keycloak's browser flow is also set to redirect to Google and its password form is disabled, so even a request without the hint never shows a Keycloak page.
3. Google sends the browser back through Keycloak to `/auth/callback`. The auth service then:
   - redeems the code on the back channel, with the client secret and PKCE;
   - verifies the id token against Keycloak's signing keys;
   - sets the session cookie.
4. **Tokens never reach page JavaScript.** Both cookies are `HttpOnly; SameSite=Lax`, and `Secure` over HTTPS:
   - `pl_signin` holds the in-flight state, nonce and PKCE verifier. It lasts 10 minutes and is scoped to `/auth`.
   - `pl_session` holds the user and their id token, HMAC-signed. It lasts as long as Keycloak's session: 14 days idle, 30 days at most.
5. **Sign out** POSTs to `/auth/logout`. The service clears the cookie and ends the Keycloak session using `id_token_hint`, so it goes straight back to the app with no "are you sure?" page.

The realm is provisioned by `keycloak/init-keycloak.sh`, an idempotent `kcadm` script that runs on every deploy. Like topspin's, it is used instead of a one-shot realm import.

## Development

```
./dev.sh
```

`dev.sh` starts three things:
- **Keycloak** (Docker) on http://localhost:8080, with the realm provisioned in `dev` mode.
- **The auth service** on :3017.
- **Vite** on http://localhost:5173, which proxies `/auth` to the auth service.

There are two dev accounts:

| What | Where | Login |
| --- | --- | --- |
| The app (porchlight realm) | http://localhost:5173 | **dev / dev** |
| Keycloak admin console (master realm) | http://localhost:8080/admin | admin / admin |

Without Docker, `dev.sh` turns sign-in off (`VITE_AUTH_ENABLED=false`).

## Production

`porchlight.joebad.com` hosts the app and `auth.porchlight.joebad.com` hosts Keycloak. Both names are set in `.github/workflows/deploy.yml` (`DOMAIN`) and `terraform/main.tf`.

**The box is a t4g.micro** (arm64, 2 burstable vCPUs, 1 GB RAM, 2 GB swap). That's the smallest instance that runs Keycloak:
- A t4g.nano (0.5 GB) is too small for the JVM.
- At rest, measured on arm64 with the production settings:

  | Service | Memory |
  | --- | --- |
  | Keycloak | ~490 MB |
  | Postgres | ~50 MB |
  | Auth service | ~45 MB |

  Caddy and the OS take the rest.
- Nothing is built from the app on the box. The only image builds there are the Keycloak and auth-service images.
- If Keycloak ever struggles, set `instance_type = "t4g.small"`. That replaces the box, but the data volume survives.

**Persistent data** lives on a separate 4 GB EBS volume mounted at `/data`: Keycloak's Postgres and Caddy's certificates. The volume has `prevent_destroy`, and AWS takes a daily snapshot and keeps 7.

### One-time setup

1. **AWS credentials** for the deploy. The IAM user needs rights over:
   - EC2/VPC, IAM roles and instance profiles, Secrets Manager, DLM;
   - S3, for the state bucket `porchlight-terraform-state-<account>`, which the workflow creates;
   - Route53 on the `joebad.com` zone, optionally.
2. **A Google OAuth client** (web application) in the Google Cloud console:
   - Authorized redirect URI: `https://auth.porchlight.joebad.com/realms/porchlight/broker/google/endpoint`
   - Authorized JavaScript origin: `https://auth.porchlight.joebad.com`
3. **GitHub repository secrets:**

   | Secret | What |
   | --- | --- |
   | `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY` | the deploy user |
   | `EC2_SSH_PRIVATE_KEY` | a private key; its public half is installed on the box |
   | `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET` | from step 2 |

4. **DNS.** The workflow upserts both A records if `joebad.com` is a Route53 zone in that account. Otherwise it prints them as a warning; add them where the zone lives:
   - `porchlight.joebad.com A <elastic ip>`
   - `auth.porchlight.joebad.com A <elastic ip>`

   Caddy keeps retrying certificates until the records resolve.

### Deploys

Every push to `main` that touches `app/`, `server/`, `infra/` or the workflow deploys. So does running the workflow by hand. Each run:
1. Tests and typechecks the app and the auth service, then builds the app.
2. Runs `terraform apply`. This is a no-op when nothing changed.
3. Copies `infra/`, `server/` and `app/dist` to `/opt/porchlight`.
4. Runs `infra/docker/deploy.sh` on the box. That script:
   - generates any missing secrets (database and admin passwords, client and session secrets) and stores them in Secrets Manager under `porchlight/prod`;
   - starts the stack;
   - waits for Keycloak, then runs `init-keycloak.sh prod`.

### On the box

```
ssh ec2-user@<elastic ip>
cd /opt/porchlight/infra/docker
docker compose ps
docker compose logs -f keycloak
```

The Keycloak admin console is at `https://auth.porchlight.joebad.com/admin`. The username is `admin`, and the password is `KEYCLOAK_ADMIN_PASSWORD` in the `porchlight/prod` secret.
