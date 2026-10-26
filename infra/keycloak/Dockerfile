# Keycloak, built once with its build-time options resolved, as topspin does.
#
# `kc.sh build` decides the database vendor, cache and health endpoints and
# bakes them in, so the container that runs starts with `--optimized` instead
# of re-deciding all of that (a slow, memory-hungry step) on every boot of a
# 1 GB box.

FROM quay.io/keycloak/keycloak:26.7 AS builder

# /health/ready is what the compose healthcheck and deploy.sh wait on.
ENV KC_HEALTH_ENABLED=true
ENV KC_DB=postgres
# One node: a local cache instead of a clustered one saves memory and startup.
ENV KC_CACHE=local

RUN /opt/keycloak/bin/kc.sh build

FROM quay.io/keycloak/keycloak:26.7

COPY --from=builder /opt/keycloak/ /opt/keycloak/

# Behind Caddy, which terminates TLS and forwards the original scheme and host;
# without this every issuer and redirect would name http://keycloak:8080.
ENV KC_PROXY_HEADERS=xforwarded
ENV KC_HTTP_ENABLED=true

ENTRYPOINT ["/opt/keycloak/bin/kc.sh"]
CMD ["start", "--optimized"]
