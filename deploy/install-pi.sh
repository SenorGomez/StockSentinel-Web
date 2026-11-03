#!/usr/bin/env bash
set -Eeuo pipefail
ARCHIVE="$1"
APP=/opt/stocksentinel
ENVFILE=/etc/stocksentinel.env
CADDY=/etc/caddy/Caddyfile
SERVICE=/etc/systemd/system/stocksentinel.service
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

if ! id stocksentinel >/dev/null 2>&1; then
  useradd --system --no-create-home --shell /usr/sbin/nologin stocksentinel
fi
if ! test -e "$ENVFILE"; then
  umask 077
  API_KEY="$(openssl rand -hex 32)"
  printf 'STOCKSENTINEL_API_KEY=%s\n' "$API_KEY" > "$ENVFILE"
  echo "*** INITIAL LOGIN API KEY (save this securely): $API_KEY ***"
fi
if ! grep -q '^Push__VapidSubject=' "$ENVFILE"; then
  echo 'Enter a contact email for VAPID metadata (not for sending email).'
  read -r -p 'VAPID contact email: ' VAPID_EMAIL
  if [[ ! "$VAPID_EMAIL" =~ ^[^[:space:]@]+@[^[:space:]@]+\.[^[:space:]@]+$ ]]; then
    echo 'Invalid email address; no release activated.' >&2
    exit 1
  fi
  printf 'Push__VapidSubject=mailto:%s\n' "$VAPID_EMAIL" >> "$ENVFILE"
fi
chmod 600 "$ENVFILE"
mkdir -p "$APP/data" "$APP/releases"
chmod 755 "$APP" "$APP/releases"
chown -R stocksentinel:stocksentinel "$APP/data"
chmod 750 "$APP/data"
if [[ -e "$APP/current" && ! -L "$APP/current" ]]; then
  echo 'Refusing to replace a current release path that is not a symlink.' >&2
  exit 1
fi

BACKUP="$(mktemp -d)"
trap 'rm -rf "$BACKUP"' EXIT
RELEASE="$(mktemp -d "$APP/releases/release.XXXXXXXX")"
PREVIOUS_TARGET="$(readlink "$APP/current" || true)"
HAD_SERVICE=0
WAS_ACTIVE=0
APPLIED=0
CADDY_CHANGED=0
if test -e "$SERVICE"; then cp -p "$SERVICE" "$BACKUP/service"; HAD_SERVICE=1; fi
if systemctl is-active --quiet stocksentinel; then WAS_ACTIVE=1; fi
cp -p "$CADDY" "$BACKUP/Caddyfile"

replace_file() {
  install -m 644 "$1" "$2.stocksentinel-new" || return $?
  mv -f "$2.stocksentinel-new" "$2"
}
switch_release() {
  rm -f "$APP/.current-new" || return $?
  ln -s "$1" "$APP/.current-new" || return $?
  mv -Tf "$APP/.current-new" "$APP/current"
}
rollback() {
  local rc="$1"
  trap - ERR INT TERM
  set +e
  if [[ "$APPLIED" == 1 ]]; then
    local restored=1
    systemctl stop stocksentinel || restored=0
    if [[ -n "$PREVIOUS_TARGET" ]]; then switch_release "$PREVIOUS_TARGET" || restored=0; else rm -f "$APP/current" || restored=0; fi
    if [[ "$HAD_SERVICE" == 1 ]]; then replace_file "$BACKUP/service" "$SERVICE" || restored=0; else rm -f "$SERVICE" || restored=0; fi
    if [[ "$CADDY_CHANGED" == 1 ]]; then
      replace_file "$BACKUP/Caddyfile" "$CADDY" || restored=0
      systemctl reload caddy || restored=0
    fi
    systemctl daemon-reload || restored=0
    if [[ "$WAS_ACTIVE" == 1 ]]; then systemctl restart stocksentinel || restored=0; fi
    if [[ "$restored" == 1 ]]; then
      echo 'Deployment failed; restored the previous release and service configuration. Check service health.' >&2
    else
      echo 'Deployment failed and rollback is incomplete; inspect systemd, current and Caddy immediately.' >&2
    fi
  else
    echo 'Deployment failed before activation; the previous release was not changed.' >&2
  fi
  exit "$rc"
}
trap 'rollback $?' ERR
trap 'rollback 130' INT
trap 'rollback 143' TERM

# Stage only application files. Persistent state is shared, never extracted or rolled back.
tar --exclude='./data' --exclude='./appsettings.json' -xzf "$ARCHIVE" -C "$RELEASE"
test -f "$RELEASE/StockSentinel.Web"
chown -R root:root "$RELEASE"
find "$RELEASE" -type d -exec chmod 755 {} +
find "$RELEASE" -type f -exec chmod 644 {} +
chmod 755 "$RELEASE/StockSentinel.Web"
if ! test -e "$APP/appsettings.json"; then
  tar -xOf "$ARCHIVE" ./appsettings.json > "$BACKUP/appsettings.json"
  replace_file "$BACKUP/appsettings.json" "$APP/appsettings.json"
fi
ln -s "$APP/appsettings.json" "$RELEASE/appsettings.json"
cp "$CADDY" "$BACKUP/candidate"
if ! grep -q '^stocksentinel\.home[[:space:]]*{' "$CADDY"; then
  cat >> "$BACKUP/candidate" <<'SITE'

stocksentinel.home {
    tls internal
    reverse_proxy 127.0.0.1:5070
}
SITE
fi
caddy validate --config "$BACKUP/candidate" --adapter caddyfile

# Preserve the legacy root executable for rollback when migrating an old installation.
APPLIED=1
replace_file "$SCRIPT_DIR/stocksentinel.service" "$SERVICE"
switch_release "$RELEASE"
systemctl daemon-reload
systemctl restart stocksentinel
READY=0
for attempt in {1..10}; do
  if systemctl is-active --quiet stocksentinel && curl --fail --silent --show-error --max-time 2 http://127.0.0.1:5070/health >/dev/null; then
    READY=1
    break
  fi
  sleep 1
done
if [[ "$READY" != 1 ]]; then echo 'New release failed its health check.' >&2; false; fi
CADDY_CHANGED=1
replace_file "$BACKUP/candidate" "$CADDY"
systemctl reload caddy
systemctl enable stocksentinel
trap - ERR INT TERM
echo "Activated release: $RELEASE"
