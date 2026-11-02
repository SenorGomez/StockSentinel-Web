#!/usr/bin/env bash
set -euo pipefail

# Run from your StockSentinel-Web project folder on macOS.
# Example: PI_SSH=pi@raspberrypi.local ./deploy-pi.sh
PI_SSH="${PI_SSH:-pi@raspberrypi.local}"
APP_DIR="/opt/stocksentinel"
APP_PORT="5070"
SITE="stocksentinel.home"

if [[ ! -f StockSentinel.Web.csproj ]]; then
  echo "Run this script from the directory containing StockSentinel.Web.csproj" >&2
  exit 1
fi

echo "==> Running automated tests before any SSH/deployment work"
dotnet test tests/StockSentinel.Tests/StockSentinel.Tests.csproj --configuration Release
echo "==> Tests passed"

ssh "$PI_SSH" "test ! -e /opt/stocksentinel/StockSentinel.Web || test -f /opt/stocksentinel/StockSentinel.Web" >/dev/null
if ! ssh "$PI_SSH" "! ss -ltn | grep -Eq '(^|[[:space:]])(127[.]0[.]0[.]1:|0[.]0[.]0[.]0:|\\*:|\\[::\\]:)${APP_PORT}[[:space:]]' || systemctl is-active --quiet stocksentinel"; then
  echo "Port $APP_PORT is occupied and StockSentinel is not running; refusing to deploy." >&2
  exit 1
fi

echo "==> Publishing .NET 10 for 32-bit Raspberry Pi OS (linux-arm)"
dotnet publish -c Release -r linux-arm --self-contained true -p:PublishSingleFile=true -o ./publish

TMPDIR_LOCAL="$(mktemp -d)"
trap 'rm -rf "$TMPDIR_LOCAL"' EXIT
tar -C publish -czf "$TMPDIR_LOCAL/stocksentinel.tgz" .

REMOTE_DIR="$(ssh "$PI_SSH" 'mktemp -d "$HOME/stocksentinel-deploy.XXXXXX"')"
scp "$TMPDIR_LOCAL/stocksentinel.tgz" "$PI_SSH:$REMOTE_DIR/stocksentinel.tgz"

# No interpolation of untrusted remote content into the privileged script.
cat > "$TMPDIR_LOCAL/install.sh" <<'REMOTE'
#!/usr/bin/env bash
set -euo pipefail
ARCHIVE="$1"
APP=/opt/stocksentinel
ENVFILE=/etc/stocksentinel.env
CADDY=/etc/caddy/Caddyfile
SERVICE=/etc/systemd/system/stocksentinel.service

if ! id stocksentinel >/dev/null 2>&1; then
  useradd --system --no-create-home --shell /usr/sbin/nologin stocksentinel
fi
if ! test -e "$ENVFILE"; then
  umask 077
  API_KEY="$(openssl rand -hex 32)"
  printf 'STOCKSENTINEL_API_KEY=%s\n' "$API_KEY" > "$ENVFILE"
  echo ""
  echo "*** INITIAL LOGIN API KEY (save this securely): $API_KEY ***"
fi
# A VAPID contact is public metadata, not an authentication secret.
# Preserve any existing value and VAPID keys across deployments.
if ! grep -q '^Push__VapidSubject=' "$ENVFILE"; then
  echo "StockSentinel Web Push needs a VAPID contact identity."
  echo "Enter an address you control (only used in VAPID JWT metadata, not for sending email)."
  read -r -p 'VAPID contact email: ' VAPID_EMAIL
  if [[ ! "$VAPID_EMAIL" =~ ^[^[:space:]@]+@[^[:space:]@]+\.[^[:space:]@]+$ ]]; then
    echo 'Invalid email address; no deployment applied.' >&2
    exit 1
  fi
  printf 'Push__VapidSubject=mailto:%s\n' "$VAPID_EMAIL" >> "$ENVFILE"
fi
chmod 600 "$ENVFILE"
mkdir -p "$APP/data"
if ! test -e "$APP/appsettings.json"; then
  tar -xOf "$ARCHIVE" ./appsettings.json > "$APP/appsettings.json"
fi
# Keep persistent config and data; update app binaries/assets only.
tar --exclude='./appsettings.json' --exclude='./data' -xzf "$ARCHIVE" -C "$APP"
chown -R root:root "$APP"
chmod 755 "$APP"
chmod 644 "$APP/appsettings.json"
chown -R stocksentinel:stocksentinel "$APP/data"
chmod 750 "$APP/data"
chmod 755 "$APP/StockSentinel.Web"

cat > "$SERVICE" <<'UNIT'
[Unit]
Description=StockSentinel Monitoring Dashboard
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
User=stocksentinel
Group=stocksentinel
WorkingDirectory=/opt/stocksentinel
EnvironmentFile=/etc/stocksentinel.env
Environment=DOTNET_ENVIRONMENT=Production
ExecStart=/opt/stocksentinel/StockSentinel.Web --urls http://127.0.0.1:5070
Restart=always
RestartSec=10
NoNewPrivileges=true
ProtectSystem=strict
ReadWritePaths=/opt/stocksentinel/data
ProtectHome=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
UNIT

if ! grep -q '^stocksentinel\.home[[:space:]]*{' "$CADDY"; then
  cp "$CADDY" "${CADDY}.stocksentinel.bak"
  cat >> "$CADDY" <<'SITE'

stocksentinel.home {
    tls internal
    reverse_proxy 127.0.0.1:5070
}
SITE
  if ! caddy validate --config "$CADDY" --adapter caddyfile; then
    cp "${CADDY}.stocksentinel.bak" "$CADDY"
    echo 'Caddy validation failed; original config restored.' >&2
    exit 1
  fi
fi
caddy validate --config "$CADDY" --adapter caddyfile
systemctl daemon-reload
systemctl enable stocksentinel
systemctl restart stocksentinel
systemctl reload caddy
sleep 2
systemctl --no-pager --full status stocksentinel || true
curl --fail --silent --show-error http://127.0.0.1:5070/health && echo
REMOTE

scp "$TMPDIR_LOCAL/install.sh" "$PI_SSH:$REMOTE_DIR/install.sh"
# Force a tty so normal sudo password prompts work.
ssh -tt "$PI_SSH" "sudo bash '$REMOTE_DIR/install.sh' '$REMOTE_DIR/stocksentinel.tgz'; rc=\$?; rm -rf '$REMOTE_DIR'; exit \$rc"

echo
printf 'Deployment completed. Add a Pi-hole local DNS record: %s -> your Pi LAN IP.\n' "$SITE"
echo 'Then browse to https://stocksentinel.home (trust the local Caddy CA on your devices if needed).'
