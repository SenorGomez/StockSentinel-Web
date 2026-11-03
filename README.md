# StockSentinel

**Self-hosted stock monitoring dashboard built with ASP.NET Core 10, SQLite and a responsive web interface.**

StockSentinel monitors configured product listings and JSON endpoints, records availability history, and sends notifications when a qualifying item becomes available. It is intended for personal, trusted-network deployments—not as a ready-made scraper for every retailer.

> **Important:** Stock signals must be validated against the retailer's actual purchase flow. An HTTP 200 response, an API `orderable` flag, or generic “Add to basket” text does not guarantee that checkout is possible. Respect retailer terms, rate limits and bot protections; do not bypass CAPTCHAs or access controls.

## Features

- **Products and sources:** Configure products, maximum GBP prices, enabled status, HTTPS sources and polling intervals from the dashboard.
- **Source templates:** Export reusable source settings as local JSON files and preview imports into an existing or new product. Imported sources start disabled.
- **HTML detection:** AngleSharp CSS selectors for available/unavailable indicators and price extraction.
- **REST / JSON detection:** Configurable paths for identity, availability, price, currency and purchase URL; supports collection matching for JSON responses containing multiple items.
- **Monitoring:** Background checks, manual **Check now**, recent activity, status and next-check information, with SQLite persistence.
- **Conservative results:** Ambiguous or incomplete responses are reported as `Unknown`, not assumed to be in stock. HTTP 403/429 and other failures are handled as errors with backoff.
- **Notifications:** SMTP email (MailKit) and browser Web Push, with per-source deduplication and conditional reminder pushes. See [notification behaviour](#notification-behaviour) for retry limits.
- **Authenticated UI:** API-key login and an HttpOnly session cookie. SMTP passwords are not exposed in the dashboard.
- **Tests and CI:** xUnit tests for JSON detection and a GitHub Actions build/test workflow.

## Requirements

- Docker Engine/Desktop with Compose v2+ for containers; .NET 10 SDK for local development/native publishing. OpenSSL for key generation.
- A writable directory for SQLite data.
- For the supplied Pi deployment script: a **32-bit ARM Raspberry Pi OS** host, SSH access with `sudo`, `openssl`, `curl`, `ss` (from `iproute2`), `systemd` and Caddy configured on the Pi, including `/etc/caddy/Caddyfile` and a running `caddy` service. The publishing computer needs Bash, SSH/SCP and .NET 10.
- A modern browser; HTTPS is required for Web Push on supported devices.

## Local development

Clone the repository and work from its root:

```bash
# Create your local configuration from the supplied sample.
cp appsettings.example.json appsettings.json

# Generate a local login key. The next command displays a secret: use a private terminal.
export STOCKSENTINEL_API_KEY="$(openssl rand -hex 32)"
printf 'Local login key: %s\n' "$STOCKSENTINEL_API_KEY"

dotnet restore
ASPNETCORE_ENVIRONMENT=Development dotnet run
```

The example configuration uses `http://127.0.0.1:8080`; if you change `Urls` in `appsettings.json`, use the configured address instead. Open it, enter the generated key in the **API key** field and choose **Sign in**. Authentication is mandatory and requires a key of at least 24 characters.

Development mode permits local HTTP login. Outside Development, authentication cookies require HTTPS. By default the app trusts only `X-Forwarded-Proto` from `127.0.0.1`, with one forwarding hop. `ReverseProxy:KnownProxy` can select one explicit proxy IP (including a container-network address); never set it to an untrusted client address. Host/client forwarding headers and whole networks are not trusted.

The SQLite database is stored under `data/` by default. Both `data/` and the real `appsettings.json` are ignored by Git. **Do not expose the HTTP development endpoint to the internet.**

### Build and test

```bash
dotnet build --configuration Release
dotnet test tests/StockSentinel.Tests/StockSentinel.Tests.csproj --configuration Release
```

GitHub Actions runs restore, build, .NET tests, script validation, isolated deployment/rollback tests and two `linux-arm` publishes on pushes and pull requests. Publish checks use synthetic secrets and reject runtime data, recursion and unexpected files. CI uses read-only repository permissions and needs no Pi SSH configuration, SMTP credentials or runtime database.

Additional local checks (Python 3 and Node.js are needed for these checks, not by the deployed app):

```bash
python3 tests/deployment-tests.py
python3 tests/verify-publish.py
gitleaks git --redact --log-opts=--all .
dotnet list StockSentinel.Web.csproj package --vulnerable --include-transitive --no-restore
```

Repository administrators must protect `main` and `develop`: require pull requests, successful **build-and-test** and **Secret scan** checks, and at least one approval; block force pushes/deletions and apply the rules to administrators. Ensure dependency alerts are enabled. Workflow files alone cannot enforce these GitHub settings.

## Configuration and credentials

| Setting | Purpose |
| --- | --- |
| `appsettings.json` | Local bootstrap settings, including database path and optional SMTP settings; **not committed** |
| `STOCKSENTINEL_API_KEY` | Required UI/API authentication key; use a strong randomly generated value |
| `STOCKSENTINEL_SMTP_PASSWORD` | SMTP password, provided via environment rather than source-controlled JSON |
| `.env` | Private Compose runtime settings, or optional native deployment settings such as `PI_SSH`; **not committed** |
| `.env.example` | Safe native deployment example containing `PI_SSH` |
| `/etc/stocksentinel.env` | Private environment file on the Pi, used by the systemd service |

Product and source configuration is edited through the dashboard. Bootstrap SMTP configuration changes require an application restart.

### Login, key recovery and rotation

When `/etc/stocksentinel.env` does not exist, the Pi installer generates a 64-character key using `openssl rand -hex 32`, writes it to that root-only file and displays it once. Open `https://stocksentinel.home`, enter it in **API key** and choose **Sign in**. The HttpOnly, SameSite=Strict authentication cookie lasts 12 hours and can persist across browser restarts; sign in again after it expires or is cleared. Production requires HTTPS; Development permits loopback HTTP.

To recover the key, SSH to your Pi and run this only in a private terminal:

```bash
sudo grep '^STOCKSENTINEL_API_KEY=' /etc/stocksentinel.env
```

**Displaying or copying the key exposes a secret.** Avoid terminal recordings, screenshots, shared clipboards and logs; never paste it into an issue report. The local development example also displays its key.

For manual rotation, generate a new value with `openssl rand -hex 32`, use `sudoedit /etc/stocksentinel.env` to replace only `STOCKSENTINEL_API_KEY`, preserve the other settings and keep the file root-owned with mode `600`. Run `sudo systemctl restart stocksentinel`, then sign in with the new key on each device. Old cookies stop authenticating after restart. There is no rotation control in the UI. Locally, export the replacement key and restart `dotnet run`.

### Optional SMTP alerts (iCloud example)

Use an SMTP account you control. For iCloud Mail, an Apple app-specific password may be required. Set `STOCKSENTINEL_SMTP_PASSWORD` in the environment, and use settings equivalent to:

```json
{
  "Smtp": {
    "Enabled": true,
    "Host": "smtp.mail.me.com",
    "Port": 587,
    "Security": "StartTls",
    "Username": "user@example.com",
    "FromAddress": "user@example.com",
    "FromName": "StockSentinel",
    "ToAddress": "user@example.com"
  }
}
```

This is a **configuration fragment**, not a complete `appsettings.json`. Replace placeholder addresses locally. Do not commit passwords, actual email addresses or notification credentials. On an installed Pi, edit `/opt/stocksentinel/appsettings.json` for SMTP settings and use `sudoedit /etc/stocksentinel.env` to add `STOCKSENTINEL_SMTP_PASSWORD`; an exported password on your publishing computer is not transferred to the service. Restart the service after changes.

There is no email-test button. The existing authenticated `POST /api/notifications/test` endpoint sends a test email. From the browser developer console on a signed-in StockSentinel page, run:

```javascript
const response = await fetch('/api/notifications/test', { method: 'POST' });
console.log(response.status, await response.text());
```

The browser supplies the login cookie. A `200` response confirms SMTP accepted the send, not delivery to the inbox or phone; `401` requires signing in again. Check **Activity log** and service logs for configuration or SMTP failures.

## Docker Compose — recommended for general self-hosting

Use Linux **amd64 or arm64** containers with Docker Engine and Compose v2+, or Docker Desktop in Linux-container mode. No host .NET SDK is needed. The Dockerfile builds from source; it does not publish an image. The native 32-bit Pi setup below remains a separate option.

### First installation

From a clone of the repository, create a private `.env` file (this refuses to overwrite an existing file):

```bash
(umask 077; set -C; printf 'STOCKSENTINEL_API_KEY=%s\nPush__VapidSubject=mailto:you@example.com\n' "$(openssl rand -hex 32)" > .env)
```

Edit `.env` to set your real public VAPID contact URI. If `.env` already exists, add these settings with an editor instead, using a fresh key of at least 24 characters. Keep it private (`chmod 600 .env`); displaying/copying the key exposes a secret. It is ignored by Git and excluded from the image build. Compose passes it to the running container; Docker administrators and `docker compose config` can inspect runtime secrets.

Optional `.env` settings use the existing environment conventions, for example:

```text
STOCKSENTINEL_PORT=8080
STOCKSENTINEL_PROXY_IP=172.18.0.1
Smtp__Enabled=false
```

`STOCKSENTINEL_PORT` changes the host and container listening port. `STOCKSENTINEL_PROXY_IP` is the **actual immediate proxy peer IP seen by the container**, not a hostname or your phone's address; the address above is illustrative. The default trusts only `127.0.0.1`. The single HTTP port binds to host loopback, for a reverse proxy on that host; it is not a public HTTP admin endpoint.

Configure your own trusted HTTPS reverse proxy and hostname, forwarding to `http://127.0.0.1:8080` (or your chosen port). Have it preserve the request host and overwrite `X-Forwarded-Proto` with the actual scheme; set `STOCKSENTINEL_PROXY_IP` to its observed peer address. On Linux with a host proxy, this is commonly the Compose bridge gateway, available after startup with:

```bash
docker inspect --format '{{range .NetworkSettings.Networks}}{{.Gateway}}{{end}}' "$(docker compose ps -q stocksentinel)"
```

Docker Desktop/proxy networking may use another peer IP; confirm it with your network configuration. Recheck it if the Docker network is recreated. A proxy in another container requires your own shared-network/Compose override and a stable, explicitly trusted proxy IP. Do not trust arbitrary forwarded headers or expose the backend to bypass the proxy. No Caddy, certificate or DNS configuration is supplied for containers.

```bash
docker compose up -d --build
docker compose ps
docker compose logs --tail=100 stocksentinel
```

Open **your HTTPS hostname**, enter the key from your private `.env` in **API key**, and choose **Sign in**. The loopback HTTP port is for the proxy; Production keeps authentication cookies Secure. Do not switch to Development to allow HTTP login. HTTPS trusted by the iPhone and a Home Screen installation are required for [Web Push](#web-push-on-iphone); replace the native `stocksentinel.home` examples with your own hostname, and use your proxy's CA instructions if it uses a private CA.

The built-in health check calls `/health` from inside the container. Outbound DNS/HTTPS to public retailers and push providers, and SMTP when enabled, must be allowed by the host/firewall. Existing direct-connection and public-destination restrictions still apply; outbound HTTP proxies are not used. Set SMTP fields such as `Smtp__Host`, `Smtp__Username` and `STOCKSENTINEL_SMTP_PASSWORD` in `.env` if needed. Web Push test notifications still link to the native `stocksentinel.home` hostname; use receipt of the notification to test delivery. Stock alerts use their configured retailer URL.

### Data, upgrades and backups

The non-root `app` user (UID/GID 1654) writes to `/app/data`, mounted from Compose's `stocksentinel-data` named volume. This holds SQLite (including subscriptions/history) and `push-vapid.json`. Docker manages its host location, usually under its volume directory or Docker Desktop's VM. Locate it with `docker volume inspect <project>_stocksentinel-data`; keep the same Compose project name/directory for upgrades. Do not move the database outside `/app/data` or mount over `/app`.

Use `docker compose stop` to stop gracefully, `docker compose start` to resume, or `docker compose restart` to restart. `docker compose down` removes containers/network but retains the data volume; **do not use `down -v` or prune the volume** unless deliberately deleting the instance. The process receives SIGTERM with a 45-second grace period. After changing `.env`, use `docker compose up -d --force-recreate`, not just restart. Rotate the API key there and recreate; existing login cookies then fail.

Back up before upgrading. Stop the service so SQLite and its WAL are consistent, then archive the entire volume (backup files contain secrets):

```bash
mkdir -p data/backups
docker compose stop
(umask 077; docker compose run --rm --no-deps -T --entrypoint tar stocksentinel -C /app/data -czf - . > data/backups/stocksentinel.tgz)
docker compose start
```

Keep an additional secure copy off-host, along with private `.env` settings and the source commit used. To upgrade a clean checkout, run `git pull --ff-only`, then `docker compose build --pull` and `docker compose up -d`; the volume survives recreation. There is no automatic container/database rollback; restore the backed-up data with the matching source version if needed.

To restore on a new host/project, recreate your private `.env` and build the image, keep the app stopped, and restore into an **empty** data volume. This command refuses a nonempty volume; back up an existing instance before arranging an empty replacement:

```bash
docker compose build --pull
docker compose run --rm --no-deps -T --entrypoint /bin/sh stocksentinel -c 'test -z "$(ls -A /app/data)" && tar -xzf - -C /app/data' < data/backups/stocksentinel.tgz
docker compose up -d
```

Preserve volume ownership for UID/GID 1654; do not make secret files world-readable. The runtime image supplies .NET; self-contained ARM32 Pi artifacts are not used by Docker. Multi-platform builds require a suitable Buildx builder; normal Compose builds use the host's architecture. Container build/runtime and cross-architecture behaviour must be validated on your Docker host.

## Raspberry Pi — native deployment (32-bit ARM)

This option uses the existing `deploy-pi.sh` workflow over SSH with systemd and Caddy on the Pi. It is independent of Docker and does not use Compose volumes or `.env` container settings.

The included `deploy-pi.sh` publishes a **self-contained `linux-arm` .NET 10 executable**, copies it over SSH and installs/updates the systemd service and local Caddy reverse proxy. This script is designed for the deployment layout below; adapt it before using it on a different host.

1. Ensure `appsettings.json` exists locally and contains the intended non-secret settings. Configure your Pi with SSH, `sudo`, Caddy and `openssl`.
2. Copy the deployment example to a private `.env` file:

   ```bash
   cp .env.example .env
   ```

   Set `PI_SSH` in `.env` to your own SSH destination, for example `user@raspberrypi.local`, then explicitly load your own trusted file:

   ```bash
   set -a
   . ./.env
   set +a
   ```

   The script reads the exported environment; it does not automatically load `.env`. Alternatively run `PI_SSH=user@raspberrypi.local ./deploy-pi.sh`.
3. From the repository root, run:

   ```bash
   ./deploy-pi.sh
   ```

The script runs the xUnit tests **before** connecting to the Pi. It creates a fresh temporary directory and publishes with the equivalent of:

```bash
dotnet publish -c Release -r linux-arm --self-contained true \
  -p:PublishSingleFile=true -o "$TMPDIR_LOCAL/publish"
```

On the Pi, the installer:

- Stages application files under `/opt/stocksentinel/releases/` and atomically switches `/opt/stocksentinel/current`; runs as the `stocksentinel` system user. The working directory remains `/opt/stocksentinel`, preserving relative database paths. The release links to the existing root `appsettings.json`.
- Creates `/etc/stocksentinel.env` on first installation and generates a random API key with `openssl rand -hex 32`. **Save the key shown during first deployment securely.**
- Prompts for a VAPID contact email if `Push__VapidSubject` is not already configured.
- Preserves `/opt/stocksentinel/data/`, the existing `appsettings.json`, and `/etc/stocksentinel.env` on subsequent deployments.
- Runs the service on **`127.0.0.1:5070`** and configures Caddy to proxy **`https://stocksentinel.home`** using `tls internal`.
- Validates Caddy before activation, restarts the service and requires `/health` to succeed within a bounded readiness check. Restart, readiness or Caddy reload failures restore the previous release pointer, systemd unit and Caddy configuration. A legacy root installation is preserved for rollback during its first staged upgrade.

SQLite remains a native sidecar (`libe_sqlite3.so`) next to the self-contained executable; the publish regression check verifies that incremental publishes retain it.

The installer is `deploy/install-pi.sh`, with `deploy/stocksentinel.service` as the canonical loopback-only unit. Old releases remain available for inspection/rollback and require occasional manual cleanup; do not remove the active target of `current`. Rollback restores application/configuration files, not SQLite contents: monitoring history, subscriptions, VAPID keys and the environment file remain in place. Keep backups, and inspect service health if the installer reports incomplete rollback. Power loss or a failed disk can require manual recovery. No remote changes occur until you explicitly run the deployment script.

Add a local DNS record mapping `stocksentinel.home` to the Pi's LAN address. Trust the Caddy local certificate authority on client devices as appropriate. The `.home` hostname and service port are currently specific to the supplied deployment script, not configurable through `.env`.

**Do not port-forward the admin interface.** Use a trusted LAN or a properly secured VPN. Back up the SQLite database and Web Push signing material before upgrades.

### Changing settings and troubleshooting on the Pi

Upgrades preserve `/opt/stocksentinel/appsettings.json` and `/etc/stocksentinel.env`; changing a local file and redeploying does **not** update them. Edit the Pi files directly, retaining existing values and secret-file permissions. Run these commands on the Pi:

```bash
sudo systemctl restart stocksentinel
sudo systemctl status stocksentinel --no-pager
sudo journalctl -u stocksentinel -n 100 --no-pager
sudo journalctl -u caddy -n 100 --no-pager
curl --fail http://127.0.0.1:5070/health
```

Use service logs for startup/push failures and Caddy logs for HTTPS/proxy failures. If localhost health succeeds but the site will not open, check LAN DNS for `stocksentinel.home`, Caddy and certificate trust. Review logs for private URLs or other sensitive data before sharing them.

## Configuring stock sources

1. In **Products → + Add product**, set a name and maximum GBP price; keep the product enabled and save.
2. In **Sources → + Add source**, select the product, enter a public HTTPS request URL and choose **HTML / CSS** or **REST / JSON (GET)**. Configure the rules below and leave **Enable automatic checks** unchecked; save.
3. Choose **Check now** and inspect the status/detail and retailer purchase flow. Manual checks can send real alerts even while automatic checks are disabled.
4. Once reliable, choose **Edit**, check **Enable automatic checks** and save. Automatic polling requires both product and source to be enabled.

For HTML sources, configure available/unavailable CSS selectors and a price selector/regex. Optional available/unavailable text is matched case-insensitively within the selected element. Use selectors tied to the *specific product*, not a generic page-wide basket button. Both indicators matching, or neither matching, yields `Unknown`; an explicit unavailable match yields `Unavailable`. A positive match requires a valid price extracted with named regex group `price`, for example `£\s*(?<price>[0-9,]+(?:\.[0-9]{2})?)`; otherwise it yields `Unknown`. Only server-returned HTML is parsed; JavaScript is not executed.

The minimum supported polling interval is **3 minutes** (or a higher configured `Sentinel:MinimumPollMinutes`); the source editor defaults to 15 minutes. Increasing frequency can trigger retailer blocks. HTTP 403/429 responses should not be interpreted as stock status.

The default `Sentinel:HttpTimeoutSeconds` is 20 seconds through DNS, headers and response-body reads. `Sentinel:MaxResponseBytes` defaults to 750,000 bytes; oversized responses yield `Unknown`. From the second consecutive `Error`/`Unknown`, the interval is multiplied by 2, then 4, up to 32, with the resulting backoff capped at 360 minutes. A confirmed result resets the failure count. Checks run sequentially; scheduling is approximate, and **Check now** may return `Busy` while another check runs.

### REST / JSON detector

The REST / JSON adapter supports configured paths for product identity, availability, price and currency. Existing HTML sources are retained, and the SQLite schema is migrated for JSON configuration.

For illustration, a Nintendo UK source may use this request URL:

```text
https://store.nintendo.com/api/catalog/products?q=Pokemon%20GO%20Plus%20%2B&limit=6
```

Example detector configuration:

```json
{
  "idPath": "$.data.products[0].id",
  "expectedId": "000000000010004546",
  "availablePath": "$.data.products[0].inventory.orderable",
  "confirmationPath": "$.data.products[0].c_availabilityModel.orderable",
  "preorderPath": "$.data.products[0].inventory.preorderable",
  "pricePath": "$.data.products[0].price.value",
  "currencyPath": "$.data.products[0].price.currencyCode",
  "currency": "GBP",
  "purchaseUrl": "https://store.nintendo.com/en-gb/pokemon-go-plus-000000000010004546"
}
```

These paths are **examples, not a guaranteed working retailer integration**. Validate them against current responses. In particular, a result at index `[0]` may not be the intended product; `expectedId` helps prevent false matches. A positive API response must still be checked against the retailer's actual ordering flow.

JSON rules require `idPath`, a string `expectedId` and `availablePath`. Paths start with `$.` and support properties, numeric indexes and quoted keys, such as `$.products[0].id` or `$.products["SKU/A"].id`; filters, recursive searches and general wildcards are unsupported. Availability is boolean unless `availableValue` selects a case-insensitive string match. Optional `confirmationPath` must agree with orderability; `preorderPath` selects a boolean preorder flag, which can also qualify an item as available when the required flags are valid and consistent.

For collections, `itemsPath` selects a nonempty array (an optional trailing `[*]` is supported), and `labelPath` identifies each entry. All other paths, including `labelPath`, are relative to each entry. `matchMode` is `any` (default) or `all`; an any-match positive can succeed despite unknown entries, while all-match requires every entry to be verified available. For example, `itemsPath: "$.locations"`, `labelPath: "$.name"`, `idPath: "$.product.id"` and `availablePath: "$.orderable"` inspect each location separately.

`pricePath` and `currencyPath` must be supplied together or both omitted; only GBP is supported. **Stock-only JSON checks without price paths can alert without enforcing the product's maximum price.** A configured price must be a positive JSON number with matching currency. Optional HTTPS `purchaseUrl` chooses the retailer page opened by stock notifications; otherwise the request URL is used.

## Sharing source templates

Choose **Export Template** on a source to download a versioned `.json` file. On the receiving installation, choose **Sources → Import Template**, select the local file, review/edit the settings and choose an existing product or create a new one with a local maximum price. Confirm with **Import template**. Matching source names or URLs produce a duplicate warning; importing always creates another source and never overwrites one. All imported sources start disabled, including incomplete HTML configurations. Run **Check now**, finish any missing rules and verify results before enabling polling.

Templates share source configuration only: names, request/purchase URLs, polling intervals and HTML or JSON detection rules. They exclude database/product IDs, enabled state, history, runtime status, product prices, credentials and notification settings. Files are limited to 64 KiB and version 1; unknown fields and unsupported detector rules are rejected. Validation uses the receiving installation's minimum polling interval.

Example version 1 template (a public example endpoint, not a working retailer integration):

```json
{
  "format": "stocksentinel-source",
  "version": 1,
  "source": {
    "name": "Example retailer",
    "url": "https://example.com/api/products?id=sample-sku",
    "intervalMinutes": 15,
    "adapter": "rest-json",
    "jsonConfig": {
      "idPath": "$.product.id",
      "expectedId": "sample-sku",
      "availablePath": "$.product.available",
      "purchaseUrl": "https://example.com/products/sample-sku"
    }
  }
}
```

Share the downloaded file directly with someone you trust; templates are not uploaded to a marketplace or fetched from remote URLs. The selected file is sent to your own StockSentinel installation for validation. Review the JSON before sharing or importing: source names, URLs and rules can contain private information, and automated checks cannot recognise every secret. Export/import refuse URL credentials/fragments, recognisable credential strings, unknown JSON rule fields and query parameters outside a conservative public allowlist (for example `q`, `limit`, `id`, `sku`, `parts` and `locale`). If blocked, remove private values from a separate copy of the source; do not share authenticated or signed URLs. Import validation does not contact the retailer. A template does not prove that its detector is correct or its URLs are trustworthy.

## Web Push on iPhone

Use **iOS 16.4 or later**. Web Push requires the installed Home Screen web app, not an ordinary Safari tab; see [WebKit's requirements](https://webkit.org/blog/13878/).

The steps below use the native Pi hostname/Caddy CA. For Docker, use your own HTTPS hostname and your reverse proxy's certificate instructions instead.

1. Deploy StockSentinel and connect the iPhone to the LAN/VPN that resolves `stocksentinel.home` to the Pi.
2. For the supplied Caddy `tls internal` setup, obtain **only the public root CA certificate** from your own Pi. With Caddy's standard systemd service it is `/var/lib/caddy/.local/share/caddy/pki/authorities/local/root.crt`; custom installations may use another data directory. Transfer this certificate securely to the iPhone and install its profile through Settings (normally **Profile Downloaded**, or **General → VPN & Device Management**). Never transfer `root.key`, any other private key or the whole Caddy data directory. See [Caddy's local HTTPS guidance](https://caddyserver.com/docs/running#local-https-with-systemd).
3. Under **Settings → General → About → Certificate Trust Settings**, enable full trust for that root. Merely installing the profile is insufficient; see [Apple's instructions](https://support.apple.com/en-us/102390). Trust only the CA you verified as belonging to your Pi.
4. Open `https://stocksentinel.home` in Safari and confirm there is no certificate warning. Choose **Share → Add to Home Screen**, then **Add**.
5. Close Safari and launch **StockSentinel from its Home Screen icon**. Sign in there; do not assume Safari's login carries over. Open **Notifications → Enable push on this device** and allow the iOS notification prompt.
6. Confirm **Push subscription saved**. The page should show `Permission: granted` and `This browser: subscribed`; `Stored subscriptions` counts all server registrations, not just this device. The browser subscription alone does not prove its registration still exists on the server.
7. Choose **Send test notification**. The toast reports how many push services accepted the test across registered devices; verify an actual notification on this iPhone, including while locked.

On supported iOS versions, Home Screen web apps can receive Web Push through Apple's push infrastructure even when the phone is away from home. Notification taps open the configured retailer purchase URL.

A successful push endpoint response means the push service accepted the request; it does **not** guarantee the notification was displayed. Local CA trust and `.home` hostnames can cause compatibility issues on some devices.

Web Push accepts HTTPS endpoints on port 443 from Apple (`*.push.apple.com`), Google (`fcm.googleapis.com`, legacy `android.googleapis.com`), Mozilla (`updates.push.services.mozilla.com`, legacy `push.services.mozilla.com`) and Windows (`*.notify.windows.com`). Provider paths and query strings remain opaque. Unsupported stored subscriptions are retained but skipped during delivery; re-subscribe from a supported browser if necessary. Adding another provider requires a reviewed allowlist change.

Web Push subscriptions and signing keys are stored under `data/`. Keep `data/push-vapid.json` private and backed up: replacing signing keys can require devices to subscribe again. SMTP is optional and used only when enabled.

### Missing notifications or subscription failures

- **Unsupported browser / no permission prompt:** check iOS 16.4+, launch the installed Home Screen app and tap **Enable push on this device**. Permission must follow that tap; Safari installation alone does not subscribe the device.
- **Permission denied / alerts hidden:** check StockSentinel under iPhone **Settings → Notifications**, including Allow Notifications and Lock Screen alerts. Review Focus/Do Not Disturb and notification summaries; granting permission does not guarantee immediate display.
- **HTTPS or registration failure:** fix DNS and full certificate trust first; bypassing a Safari warning is insufficient. Sign in again if needed, retry **Enable push on this device**, and confirm **Push subscription saved**. Check **Activity log** and `journalctl -u stocksentinel` for test failures, VAPID configuration or outbound push-service errors. Avoid sharing subscription endpoints/keys in reports.
- **Expired registration / replaced VAPID keys:** the server removes subscriptions after push-service HTTP 404/410 responses, while the browser may still say subscribed. In the installed app, choose **Disable this device**, then **Enable push on this device** and test again. Preserve the existing signing keys and database during upgrades. Do not delete them as a routine troubleshooting step.
- **Accepted test but nothing displayed:** check the particular iPhone's permission and Focus settings, network access and subscription. A nonzero test count may refer to another device. The Pi needs outbound access to the push provider; the iPhone need not reach the Pi to receive an already registered push, but does need LAN/VPN access to use the private dashboard.

### VAPID contact

On first deployment, the installer asks for a contact email if `/etc/stocksentinel.env` has no `Push__VapidSubject`. It writes a value of the form:

```text
Push__VapidSubject=mailto:user@example.com
```

This contact is public VAPID metadata, **not** an SMTP password. To deploy unattended, configure `Push__VapidSubject` in the private Pi environment file ahead of time. If creating that file yourself, also supply a strong `STOCKSENTINEL_API_KEY` of at least 24 characters: the installer generates a key only when the file is absent. Keep it root-owned with mode `600`. If Apple rejects a push with `BadJwtToken`, investigate the JWT audience, expiry, signing keys and compatibility rather than assuming the contact field is the only cause.

### Notification behaviour

An `Available` result qualifies for an initial alert when its supplied price is at or below the product maximum, or when a JSON stock-only check supplies no price. Web Push is attempted and SMTP is also attempted when enabled. If **any push service or SMTP accepts the send**, that source is marked notified; failed channels/devices are not retried independently. If all attempts fail, another qualifying check can retry. A confirmed `Unavailable` result resets this per-source latch; `Unknown`/`Error` does not. Acceptance is not proof of device display or inbox delivery.

Up to two additional **Web Push** reminders become due about 2 and 5 minutes after the initial accepted notification. They use cached stock status rather than fetching the retailer, require enabled product/source, `Available` status, a check within the past 6 minutes and a recorded price within the maximum. Stock-only results with no price do not qualify. Reminder timing is approximate; each attempt consumes its slot even if sending fails. Disabling polling does not prevent a manual **Check now** from sending an initial alert.

## Metrics

Open **Metrics** and select **All products** or a specific product, with a period of 24 hours, 7, 30 or 90 days. **Confirmed checks** is the percentage of all detailed checks reporting `Available` or `Unavailable`; unknown/error results remain in the denominator. Detailed checks are retained for 90 days; older event-based observations can supplement restock history while retained (the event log keeps up to 5,000 entries).

Restock charts use UK time and count observed availability windows, not guaranteed retailer restocks. **Confirmed span** is a minimum observed duration, not exact availability; Unknown/Error does not establish that stock ended. Product metrics follow current source assignments; deleted/unidentifiable source history appears only in **All products**. **Push alerts accepted** counts accepted initial/reminder send events, not devices reached or delivery confirmations.

## Security and operational notes

- Keep the dashboard on a trusted network behind HTTPS; do not expose the administration API publicly without further authentication and CSRF review.
- The API key must be strong and private. Never put it in source control, screenshots, logs or issue reports.
- Monitoring and Web Push connect directly to validated public IP addresses, with normal hostname/TLS validation. Every new connection checks DNS results and pins the selected address; redirects and outbound HTTP proxies are disabled. Private, loopback, link-local and special-purpose destinations are rejected. Templates and retailer URLs still need manual review.
- Database activity can include URLs and retailer error details. Avoid putting credentials or personal data in source URLs.
- Preserve `data/` during deployment and back up the database, subscriptions and VAPID keys.
- Monitoring is sequential by design; running multiple workers against one database requires additional coordination.
- Retailer signals may be stale, region-specific, JavaScript-dependent or blocked by bot protections. StockSentinel does not guarantee successful checkout.

## Roadmap

Potential future improvements, subject to validation and need:

- More robust retailer-specific diagnostics and fixture coverage for changing APIs and page layouts.
- Clearer visibility into notification delivery attempts and failures.
- Additional authenticated access controls and CSRF hardening before any broader exposure.
- More portable deployment configuration for alternative hostnames, architectures and reverse proxies.

## Licence

StockSentinel's original code and assets are licensed under the [MIT licence](LICENSE).
Dependencies retain their own licences; see [third-party notices](THIRD-PARTY-NOTICES.txt),
including WebPush source availability. Self-contained publishes include these notices
and the bundled .NET runtime's legal files under `legal/`; preserve them when redistributing.

See [CONTRIBUTING.md](CONTRIBUTING.md) for development checks and pull requests targeting
`develop`, and [SECURITY.md](SECURITY.md) for private vulnerability reporting.
