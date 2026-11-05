#!/usr/bin/env bash
# Installs the Tahakkum relay server as a service on a Debian-family Linux machine.
# Safe to run again: the certificate is kept, so programs already built keep trusting it.
#
#   bash tahakkum-relay-install.sh [port]        (default port: 443)
set -euo pipefail

PORT="${1:-443}"
KEY_X="__KEY_X__"
KEY_Y="__KEY_Y__"
SERVICE=tahakkum-relay
APP=/opt/tahakkum-relay
DOTNET=/opt/dotnet
CONF=/etc/tahakkum-relay
DATA=/var/lib/tahakkum-relay

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
apt-get install -y -qq curl openssl ca-certificates iproute2 tar gzip libstdc++6 zlib1g >/dev/null

systemctl stop "$SERVICE" 2>/dev/null || true
if ss -ltn "sport = :$PORT" | grep LISTEN >/dev/null; then
  echo "Port $PORT is used by another program:" >&2
  ss -ltnp "sport = :$PORT" >&2 || true
  echo "Stop that program, or choose another port: bash $0 8443" >&2
  exit 1
fi

echo "==> Installing .NET 8 (about 200 MB)"
export DOTNET_ROOT="$DOTNET" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 HOME="${HOME:-/root}"
if [ ! -x "$DOTNET/dotnet" ] || ! "$DOTNET/dotnet" --list-sdks | grep '^8\.' >/dev/null; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 8.0 --install-dir "$DOTNET" >/dev/null
  rm -f /tmp/dotnet-install.sh
fi

echo "==> Building the relay server"
SRC=$(mktemp -d)
trap 'rm -rf "$SRC"' EXIT
base64 -d > "$SRC/RelayServer.cs" <<'SOURCE'
__SERVER_B64__
SOURCE
base64 -d > "$SRC/Protocol.cs" <<'SOURCE'
__PROTOCOL_B64__
SOURCE
base64 -d > "$SRC/LicenseToken.cs" <<'SOURCE'
__TOKEN_B64__
SOURCE
cat > "$SRC/TahakkumRelay.csproj" <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <RootNamespace>Tahakkum</RootNamespace>
    <AssemblyName>TahakkumRelay</AssemblyName>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
PROJECT
rm -rf "$APP"
mkdir -p "$APP"
"$DOTNET/dotnet" publish "$SRC/TahakkumRelay.csproj" -c Release -o "$APP" --nologo -v quiet

echo "==> Preparing the service account and certificate"
id "$SERVICE" >/dev/null 2>&1 || useradd --system --home-dir "$DATA" --shell /usr/sbin/nologin "$SERVICE"
mkdir -p "$CONF" "$DATA"
chown "$SERVICE:$SERVICE" "$DATA"
chmod 750 "$DATA"
chmod 750 "$CONF"
chown "root:$SERVICE" "$CONF"

# The programs trust exactly this certificate, so it stays when the script runs again.
if [ ! -f "$CONF/relay.pfx" ] || [ ! -f "$CONF/relay.env" ]; then
  PFX_PASSWORD=$(openssl rand -hex 24)
  openssl req -x509 -newkey rsa:2048 -nodes -keyout "$CONF/relay.key" -out "$CONF/relay.crt" \
    -days 3650 -subj "/CN=tahakkum-relay" >/dev/null 2>&1
  openssl pkcs12 -export -inkey "$CONF/relay.key" -in "$CONF/relay.crt" -out "$CONF/relay.pfx" -passout "pass:$PFX_PASSWORD"
  rm -f "$CONF/relay.key"
  chown "root:$SERVICE" "$CONF/relay.pfx" "$CONF/relay.crt"
  chmod 640 "$CONF/relay.pfx"
  chmod 644 "$CONF/relay.crt"
  printf 'PFX_PASSWORD=%s\n' "$PFX_PASSWORD" > "$CONF/relay.env"
  chmod 600 "$CONF/relay.env"
fi
PFX_PASSWORD=$(grep '^PFX_PASSWORD=' "$CONF/relay.env" | cut -d= -f2)
printf 'PFX_PASSWORD=%s\nPORT=%s\nKEY_X=%s\nKEY_Y=%s\n' "$PFX_PASSWORD" "$PORT" "$KEY_X" "$KEY_Y" > "$CONF/relay.env"
chmod 600 "$CONF/relay.env"

cat > "/etc/systemd/system/$SERVICE.service" <<UNIT
[Unit]
Description=Tahakkum relay server
After=network-online.target
Wants=network-online.target

[Service]
User=$SERVICE
EnvironmentFile=$CONF/relay.env
Environment=HOME=$DATA
Environment=DOTNET_ROOT=$DOTNET
Environment=DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
ExecStart=$DOTNET/dotnet $APP/TahakkumRelay.dll \${PORT} $CONF/relay.pfx \${PFX_PASSWORD} $DATA \${KEY_X} \${KEY_Y}
Restart=always
RestartSec=3
LimitNOFILE=65536
AmbientCapabilities=CAP_NET_BIND_SERVICE
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
ReadWritePaths=$DATA

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

for _ in $(seq 1 20); do
  if ss -ltn "sport = :$PORT" | grep LISTEN >/dev/null; then break; fi
  sleep 1
done
if ! ss -ltn "sport = :$PORT" | grep LISTEN >/dev/null; then
  echo "The service did not start. Its log:" >&2
  journalctl -u "$SERVICE" -n 40 --no-pager >&2 || true
  exit 1
fi

# The same certificate must answer over the network, not only exist on disk.
PIN=$(openssl x509 -in "$CONF/relay.crt" -outform DER | sha256sum | cut -d' ' -f1 | tr 'a-f' 'A-F')
SEEN=$(echo | openssl s_client -connect "127.0.0.1:$PORT" 2>/dev/null | openssl x509 -outform DER 2>/dev/null | sha256sum | cut -d' ' -f1 | tr 'a-f' 'A-F' || true)
if [ "$PIN" != "$SEEN" ]; then
  echo "The service is listening but answers with a different certificate." >&2
  exit 1
fi

PUBLIC_IP=$(curl -4 -fsS --max-time 5 https://api.ipify.org 2>/dev/null || hostname -I | cut -d' ' -f1)
echo
echo "=============================================================="
echo " RELAY SERVER IS RUNNING"
echo
echo " Send these two lines to Claude. They are not secret:"
echo
echo "   SERVER=$PUBLIC_IP:$PORT"
echo "   PIN=$PIN"
echo
echo " Also open TCP port $PORT in your hosting provider's firewall."
echo "=============================================================="
