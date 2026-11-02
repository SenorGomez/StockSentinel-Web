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
- **Notifications:** SMTP email (MailKit) and browser Web Push, with transition-based deduplication and retries when delivery fails.
- **Authenticated UI:** API-key login and an HttpOnly session cookie. SMTP passwords are not exposed in the dashboard.
- **Tests and CI:** xUnit tests for JSON detection and a GitHub Actions build/test workflow.

## Requirements

- .NET 10 SDK for local development and publishing.
- A writable directory for SQLite data.
- For the supplied Pi deployment script: a **32-bit ARM Raspberry Pi OS** host, SSH access with `sudo`, `openssl`, `systemd` and Caddy configured on the Pi.
- A modern browser; HTTPS is required for Web Push on supported devices.

## Local development

Clone the repository and work from its root:

```bash
# Create your local configuration from the supplied sample.
cp appsettings.example.json appsettings.json

# Generate a local login key. Keep it private.
export STOCKSENTINEL_API_KEY="$(openssl rand -hex 32)"
printf 'Local login key: %s\n' "$STOCKSENTINEL_API_KEY"

dotnet restore
dotnet run
```

The example configuration uses `http://127.0.0.1:8080`; if you change `Urls` in `appsettings.json`, use the configured address instead. Log in with the generated key.

The SQLite database is stored under `data/` by default. Both `data/` and the real `appsettings.json` are ignored by Git. **Do not expose the HTTP development endpoint to the internet.**

If `appsettings.example.json` is not present in your checkout, it must be created from a sanitized configuration before publishing the repository. Never commit a copy containing real addresses, credentials or private paths.

### Build and test

```bash
dotnet build --configuration Release
dotnet test tests/StockSentinel.Tests/StockSentinel.Tests.csproj --configuration Release
```

GitHub Actions runs restore, build and tests on pushes and pull requests. CI does not need your Pi SSH configuration, SMTP credentials or runtime database.

## Configuration and credentials

| Setting | Purpose |
| --- | --- |
| `appsettings.json` | Local bootstrap settings, including database path and optional SMTP settings; **not committed** |
| `STOCKSENTINEL_API_KEY` | Required UI/API authentication key; use a strong randomly generated value |
| `STOCKSENTINEL_SMTP_PASSWORD` | SMTP password, provided via environment rather than source-controlled JSON |
| `.env` | Optional local **deployment** settings, notably `PI_SSH`; **not committed** |
| `.env.example` | Safe deployment example that can be committed |
| `/etc/stocksentinel.env` | Private environment file on the Pi, used by the systemd service |

Product and source configuration is edited through the dashboard. Bootstrap SMTP configuration changes require an application restart.

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

This is a **configuration fragment**, not a complete `appsettings.json`. Replace placeholder addresses locally. Do not commit passwords, actual email addresses or notification credentials. Use **Send test email** before relying on alerts; a successful SMTP send does not guarantee immediate phone notification.

## Raspberry Pi deployment (32-bit ARM)

The included `deploy-pi.sh` publishes a **self-contained `linux-arm` .NET 10 executable**, copies it over SSH and installs/updates the systemd service and local Caddy reverse proxy. This script is designed for the deployment layout below; adapt it before using it on a different host.

1. Ensure `appsettings.json` exists locally and contains the intended non-secret settings. Configure your Pi with SSH, `sudo`, Caddy and `openssl`.
2. Copy the deployment example to a private `.env` file:

   ```bash
   cp .env.example .env
   ```

   Set `PI_SSH` in `.env` to your own SSH destination, for example `user@raspberrypi.local`. Alternatively export `PI_SSH` in your shell.
3. From the repository root, run:

   ```bash
   ./deploy-pi.sh
   ```

The script runs the xUnit tests **before** connecting to the Pi. It then publishes with the equivalent of:

```bash
dotnet publish -c Release -r linux-arm --self-contained true \
  -p:PublishSingleFile=true -o ./publish
```

On the Pi, the installer:

- Installs into `/opt/stocksentinel` and runs as the `stocksentinel` system user.
- Creates `/etc/stocksentinel.env` on first installation and generates a random API key with `openssl rand -hex 32`. **Save the key shown during first deployment securely.**
- Prompts for a VAPID contact email if `Push__VapidSubject` is not already configured.
- Preserves `/opt/stocksentinel/data/`, the existing `appsettings.json`, and `/etc/stocksentinel.env` on subsequent deployments.
- Runs the service on **`127.0.0.1:5070`** and configures Caddy to proxy **`https://stocksentinel.home`** using `tls internal`.
- Validates the Caddy configuration, restarts the service and checks the local `/health` endpoint.

Add a local DNS record mapping `stocksentinel.home` to the Pi's LAN address. Trust the Caddy local certificate authority on client devices as appropriate. The `.home` hostname and service port are currently specific to the supplied deployment script, not configurable through `.env`.

**Do not port-forward the admin interface.** Use a trusted LAN or a properly secured VPN. Back up the SQLite database and Web Push signing material before upgrades.

## Configuring stock sources

Sources should start **disabled**. Configure the detector, run **Check now**, inspect the diagnostic result and enable polling only when the observed behaviour is reliable.

For HTML sources, use selectors tied to the *specific product*, not a generic page-wide basket button. A missing or ambiguous indicator or price should produce `Unknown`. Price extraction uses a named regex group `price`.

The minimum supported polling interval is **5 minutes**; the default is 15 minutes. Increasing frequency can trigger retailer blocks. HTTP 403/429 responses should not be interpreted as stock status.

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

The JSON detector also supports collection-style responses and can report individually checked items using configured labels (for example, pickup locations). Consult the source and tests for the exact supported collection configuration fields; do not assume full JSONPath filter syntax.

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

1. Deploy StockSentinel behind trusted HTTPS and open `https://stocksentinel.home` in Safari while connected to the home network.
2. Ensure the device trusts the Caddy local CA certificate.
3. Choose **Share → Add to Home Screen**.
4. Launch StockSentinel **from its Home Screen icon**, log in and use **Notifications → Enable push on this device**.
5. Allow notifications and use **Send test notification**. Confirm a notification actually arrives while the phone is locked.

On supported iOS versions, Home Screen web apps can receive Web Push through Apple's push infrastructure even when the phone is away from home. Notification taps open the configured retailer purchase URL.

A successful push endpoint response means the push service accepted the request; it does **not** guarantee the notification was displayed. Local CA trust and `.home` hostnames can cause compatibility issues on some devices.

Web Push subscriptions and signing keys are stored under `data/`. Keep `data/push-vapid.json` private and backed up: replacing signing keys can require devices to subscribe again. SMTP is optional and used only when enabled.

### VAPID contact

On first deployment, the installer asks for a contact email if `/etc/stocksentinel.env` has no `Push__VapidSubject`. It writes a value of the form:

```text
Push__VapidSubject=mailto:user@example.com
```

This contact is public VAPID metadata, **not** an SMTP password. To deploy unattended, configure `Push__VapidSubject` in the private Pi environment file ahead of time. If Apple rejects a push with `BadJwtToken`, investigate the JWT audience, expiry, signing keys and compatibility rather than assuming the contact field is the only cause.

## Security and operational notes

- Keep the dashboard on a trusted network behind HTTPS; do not expose the administration API publicly without further authentication and CSRF review.
- The API key must be strong and private. Never put it in source control, screenshots, logs or issue reports.
- The application restricts nonpublic HTTP targets, but this is not a complete defence against DNS rebinding or malicious administrator-supplied URLs.
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

No licence is specified in this repository documentation. Add a `LICENSE` file before inviting external reuse or contributions.
