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
TMPDIR_LOCAL="$(mktemp -d)"
trap 'rm -rf "$TMPDIR_LOCAL"' EXIT
dotnet publish StockSentinel.Web.csproj -c Release -r linux-arm --self-contained true -p:PublishSingleFile=true -o "$TMPDIR_LOCAL/publish"
tar -C "$TMPDIR_LOCAL/publish" -czf "$TMPDIR_LOCAL/stocksentinel.tgz" .

REMOTE_DIR="$(ssh "$PI_SSH" 'mktemp -d "$HOME/stocksentinel-deploy.XXXXXX"')"
scp "$TMPDIR_LOCAL/stocksentinel.tgz" "$PI_SSH:$REMOTE_DIR/stocksentinel.tgz"

# The installer and canonical unit are staged alongside the application archive.
scp deploy/install-pi.sh deploy/stocksentinel.service "$PI_SSH:$REMOTE_DIR/"
# Force a tty so normal sudo password prompts work.
ssh -tt "$PI_SSH" "sudo bash '$REMOTE_DIR/install-pi.sh' '$REMOTE_DIR/stocksentinel.tgz'; rc=\$?; rm -rf '$REMOTE_DIR'; exit \$rc"

echo
printf 'Deployment completed. Add a Pi-hole local DNS record: %s -> your Pi LAN IP.\n' "$SITE"
echo 'Then browse to https://stocksentinel.home (trust the local Caddy CA on your devices if needed).'
