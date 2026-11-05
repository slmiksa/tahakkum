#!/usr/bin/env bash
# Installs the license dashboard as a service on a Debian-family Linux machine, open at
# https://<server IP>:<port>. It tries a publicly trusted Let's Encrypt certificate for
# the IP address, and falls back to its own certificate, which browsers warn about.
# Safe to run again.
#
#   bash tahakkum-dashboard-install.sh [port]        (default port: 8443)
set -euo pipefail

PORT="${1:-8443}"
SERVICE=tahakkum-dashboard
APP=/opt/tahakkum-dashboard
CONF=/etc/tahakkum-dashboard

if [ "$(id -u)" -ne 0 ]; then
  echo "Run this as root." >&2
  exit 1
fi
if ! [[ "$PORT" =~ ^[0-9]+$ ]] || [ "$PORT" -lt 1 ] || [ "$PORT" -gt 65535 ]; then
  echo "Bad port: $PORT" >&2
  exit 1
fi

echo "==> Installing required packages"
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq nodejs curl iproute2 openssl certbot >/dev/null
NODE=$(command -v node)
if ! "$NODE" -e 'process.exit(Number(process.versions.node.split(".")[0]) >= 18 ? 0 : 1)'; then
  echo "Node.js 18 or newer is required; this machine has $("$NODE" --version)." >&2
  exit 1
fi

systemctl stop "$SERVICE" 2>/dev/null || true
if ss -ltn "sport = :$PORT" | grep LISTEN >/dev/null; then
  echo "Port $PORT is used by another program. Choose another: bash $0 9443" >&2
  exit 1
fi

SERVER_IP=$(curl -4 -fsS --max-time 8 https://api.ipify.org 2>/dev/null || hostname -I | cut -d' ' -f1)
if [ -z "$SERVER_IP" ]; then
  echo "Could not find this server's address." >&2
  exit 1
fi

echo "==> Writing the dashboard files"
rm -rf "$APP"
mkdir -p "$APP/vendor" "$CONF"
chmod 700 "$CONF"
__FILES__
chmod -R a+rX "$APP"

echo "==> Getting a certificate for $SERVER_IP"
CERT="$CONF/selfsigned.crt"
KEY="$CONF/selfsigned.key"
TRUSTED=no
LIVE="/etc/letsencrypt/live/$SERVER_IP"
if ss -ltn "sport = :80" | grep LISTEN >/dev/null; then
  echo "    Port 80 is in use, so Let's Encrypt cannot check this server."
elif [ -d "$LIVE" ] || certbot certonly --standalone --non-interactive --agree-tos \
    --register-unsafely-without-email --preferred-profile shortlived \
    --ip-address "$SERVER_IP" --cert-name "$SERVER_IP" \
    --deploy-hook "systemctl restart $SERVICE" >/tmp/tahakkum-certbot.log 2>&1; then
  CERT="$LIVE/fullchain.pem"
  KEY="$LIVE/privkey.pem"
  TRUSTED=yes
else
  echo "    Let's Encrypt did not issue one (the reason is in /tmp/tahakkum-certbot.log)."
fi
if [ "$TRUSTED" = no ] && [ ! -f "$CONF/selfsigned.crt" ]; then
  openssl req -x509 -newkey rsa:2048 -nodes -keyout "$CONF/selfsigned.key" -out "$CONF/selfsigned.crt" \
    -days 825 -subj "/CN=$SERVER_IP" -addext "subjectAltName=IP:$SERVER_IP" >/dev/null 2>&1
  chmod 600 "$CONF/selfsigned.key"
fi

# The service runs as an unprivileged user, and systemd hands it private copies of the certificate.
cat > "/etc/systemd/system/$SERVICE.service" <<UNIT
[Unit]
Description=Tahakkum license dashboard
After=network-online.target
Wants=network-online.target

[Service]
DynamicUser=true
WorkingDirectory=$APP
Environment=PORT=$PORT
Environment=HOST=0.0.0.0
LoadCredential=cert.pem:$CERT
LoadCredential=key.pem:$KEY
Environment=TLS_CERT=%d/cert.pem
Environment=TLS_KEY=%d/key.pem
ExecStart=$NODE $APP/server.mjs
Restart=always
RestartSec=3
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
UNIT

if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep 'Status: active' >/dev/null; then
  ufw allow "$PORT/tcp" >/dev/null
fi

echo "==> Starting the service"
systemctl daemon-reload
systemctl enable "$SERVICE" >/dev/null 2>&1
systemctl restart "$SERVICE"

STATUS=""
for _ in $(seq 1 15); do
  STATUS=$(curl -sk -o /dev/null -w '%{http_code}' "https://127.0.0.1:$PORT/" || true)
  if [ "$STATUS" = "200" ]; then break; fi
  sleep 1
done
if [ "$STATUS" != "200" ]; then
  echo "The dashboard did not start. Its log:" >&2
  journalctl -u "$SERVICE" -n 40 --no-pager >&2 || true
  exit 1
fi

echo
echo "=============================================================="
echo " DASHBOARD IS RUNNING"
echo
echo "   https://$SERVER_IP:$PORT"
echo
if [ "$TRUSTED" = yes ]; then
  echo " Certificate: Let's Encrypt, renewed automatically."
else
  echo " Certificate: its own, so browsers show a warning the first time."
  echo " Its fingerprint, to compare with what the browser shows:"
  echo "   $(openssl x509 -in "$CONF/selfsigned.crt" -noout -fingerprint -sha256 | cut -d= -f2)"
fi
echo
echo " Also open TCP port $PORT in your hosting provider's firewall."
echo " Opening port 80 too lets Let's Encrypt work: then run this script again."
echo "=============================================================="
