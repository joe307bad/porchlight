#!/bin/bash
# =============================================================================
# Porchlight EC2 bootstrap (first boot only; see ignore_changes on the instance)
# =============================================================================
# Docker, Compose, swap, the persistent data volume and a systemd unit. The app
# itself arrives with the first deploy (.github/workflows/deploy.yml), which
# copies files to /opt/porchlight and runs infra/docker/deploy.sh.

set -euo pipefail
exec > >(tee /var/log/user-data.log) 2>&1
echo "porchlight bootstrap starting at $(date)"

DATA_VOLUME_ID="${data_volume_id}"

dnf update -y
dnf install -y docker jq rsync

# --- Swap ---------------------------------------------------------------------
# 1 GB of RAM; Keycloak's start-up (and an image build) briefly wants more.
if [ ! -f /swapfile ]; then
  dd if=/dev/zero of=/swapfile bs=1M count=2048
  chmod 600 /swapfile
  mkswap /swapfile
fi
swapon /swapfile || true
grep -q '^/swapfile' /etc/fstab || echo '/swapfile none swap defaults 0 0' >> /etc/fstab
# Prefer RAM, but don't fight to keep idle JVM pages in it.
echo 'vm.swappiness=10' > /etc/sysctl.d/90-porchlight.conf
sysctl --system >/dev/null

# --- Persistent data volume -----------------------------------------------------
# On Nitro instances the EBS volume shows up as an NVMe device whose serial is
# the volume id without the dash.
SERIAL="$${DATA_VOLUME_ID/-/}"
DATA_DEVICE=""
for i in $(seq 1 60); do
  DATA_DEVICE=$(lsblk -dno NAME,SERIAL | awk -v s="$SERIAL" '$2 == s { print "/dev/" $1 }')
  [ -n "$DATA_DEVICE" ] && break
  [ -b /dev/xvdf ] && { DATA_DEVICE=/dev/xvdf; break; }
  echo "waiting for the data volume ($i/60)"
  sleep 2
done
[ -n "$DATA_DEVICE" ] || { echo "data volume never appeared"; exit 1; }

blkid "$DATA_DEVICE" >/dev/null 2>&1 || mkfs.xfs "$DATA_DEVICE"
mkdir -p /data
mountpoint -q /data || mount "$DATA_DEVICE" /data
UUID=$(blkid -s UUID -o value "$DATA_DEVICE")
grep -q "$UUID" /etc/fstab || echo "UUID=$UUID /data xfs defaults,nofail 0 2" >> /etc/fstab
mkdir -p /data/postgres /data/caddy/data /data/caddy/config

# --- Docker -------------------------------------------------------------------
systemctl enable --now docker
usermod -aG docker ec2-user

# Compose and Buildx plugins (not packaged for AL2023). Pinned, so a bootstrap
# never depends on GitHub's API rate limit.
mkdir -p /usr/local/lib/docker/cli-plugins
curl -fsSL "https://github.com/docker/compose/releases/download/v2.39.2/docker-compose-linux-aarch64" \
  -o /usr/local/lib/docker/cli-plugins/docker-compose
curl -fsSL "https://github.com/docker/buildx/releases/download/v0.26.1/buildx-v0.26.1.linux-arm64" \
  -o /usr/local/lib/docker/cli-plugins/docker-buildx
chmod +x /usr/local/lib/docker/cli-plugins/docker-compose /usr/local/lib/docker/cli-plugins/docker-buildx

# --- App directory and boot-time start ------------------------------------------
mkdir -p /opt/porchlight/site
chown -R ec2-user:ec2-user /opt/porchlight

cat > /etc/systemd/system/porchlight.service <<'UNIT'
[Unit]
Description=Porchlight (Docker Compose)
Requires=docker.service
After=docker.service
# Nothing to start until the first deploy has written .env.
ConditionPathExists=/opt/porchlight/infra/docker/.env

[Service]
Type=oneshot
RemainAfterExit=yes
WorkingDirectory=/opt/porchlight/infra/docker
ExecStart=/usr/bin/docker compose up -d
ExecStop=/usr/bin/docker compose down

[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
systemctl enable porchlight

echo "porchlight bootstrap done at $(date)"
