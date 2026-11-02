# StockSentinel — self-hosted .NET 10 stock monitoring dashboard

A complete **runnable source implementation** (not a retailer-ready scraper). ASP.NET Core 10 web app plus `BackgroundService`, SQLite-backed products/sources/history, CSS-selectors via AngleSharp, SMTP email alerts via MailKit, and a simple responsive browser interface.

**Critical:** All retailer adapters require manual validation of their actual public HTML. No live source is pre-enabled. This deliberately prevents false alerts. Some retailers render stock in JavaScript, use regional inventory, CAPTCHA or block bots. Such sites need a separate approved adapter/API; **never treat HTTP 200 or a matching generic “add to basket” string as proof of stock.** Do not circumvent bot protection or ignore retailer terms. The HTTP client does not follow redirects, avoids nonpublic targets, backs off on errors and has a response cap.

## Features

- Products (name, maximum GBP price, enabled), editable without redeploy.
- Sources per product (HTTPS listing, poll minutes, enabled, CSS selectors for available/unavailable and price, GBP price extraction regex), editable without redeploy.
- Manual **Check now**, automatic checks, dashboard and searchable-by-browser activity view, status + next check information stored in SQLite.
- Conservative availability: explicit available indicator + extracted price required; ambiguous indicators are `Unknown`; 403/429 is `Error`; unknown never resets notification latch.
- SMTP email on a verified `Available` transition at or under maximum price, deduplicated per source; re-alert after confirmed unavailable; retries notification on a later positive check when SMTP fails.
- Test email button and SMTP configuration display (no password disclosure).
- Password/key protected UI and API, data/logs retained through app upgrades.

## Local developer setup

Prerequisites: .NET **10 SDK**. From this directory:

```bash
export STOCKSENTINEL_API_KEY="$(openssl rand -hex 24)"
# print and store that key so you can log in
printf '%s\n' "$STOCKSENTINEL_API_KEY"
dotnet restore
dotnet run
```

Open http://127.0.0.1:8080 and sign in with the generated key. Data stored in `data/sentinel.db` (excluded from deployment/git). **Do not expose port 8080 to the public internet.** The sample binds only localhost until explicitly overridden.

The web interface uses an HttpOnly 12-hour session cookie. For remote LAN deployment, use an HTTPS reverse proxy or a VPN tunnel (such as Tailscale or WireGuard): the raw HTTP LAN service does **not** encrypt the session cookie. Avoid putting the app on an untrusted network. HTTPS termination is the recommended security improvement before relying on authenticated remote editing.

## Send alerts to Apple iCloud Mail (no new iPhone app)

The *recipient* should be your existing iCloud mailbox; you do not need Hide My Email. To send from the Pi, choose an SMTP provider/account you already control. Example using **iCloud SMTP as sender** with an Apple app-specific password:

```json
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
```

Create an **app-specific password** for Apple Account if required, and set `STOCKSENTINEL_SMTP_PASSWORD` as an environment variable. Do **not** put passwords in browser-editable config, source control or screenshots. For VIP notifications, best to use a distinct sender you control rather than mailing yourself from the same mailbox: self-sent messages and VIP alerts may behave differently. **Test locked-phone notification latency** before treating this as urgent alerting.

`appsettings.json` is *bootstrap SMTP configuration*, so editing SMTP host or recipient requires restarting; product and retailer config is edited via dashboard and works without restarting. The app shows SMTP settings read-only and includes **Send test email**.

## Deploy to Raspberry Pi (64-bit Raspberry Pi OS)

Build/publish on a machine with the .NET 10 SDK:

```bash
dotnet publish -c Release -r linux-arm64 --self-contained true -p:PublishSingleFile=true -o publish
```

Copy `publish` to `/opt/stocksentinel` (including `wwwroot`, runtime files and `appsettings.json`). Setup once:

```bash
sudo useradd --system --no-create-home --shell /usr/sbin/nologin stocksentinel
sudo mkdir -p /opt/stocksentinel/data
sudo chown stocksentinel:stocksentinel /opt/stocksentinel/data
sudo chmod 750 /opt/stocksentinel/data
sudo cp deploy/stocksentinel.service /etc/systemd/system/stocksentinel.service
sudo sh -c 'umask 077; touch /etc/stocksentinel.env'
# Put STOCKSENTINEL_API_KEY=<long random value> and STOCKSENTINEL_SMTP_PASSWORD=<app-specific password> into /etc/stocksentinel.env
sudo chmod 600 /etc/stocksentinel.env
sudo systemctl daemon-reload
sudo systemctl enable --now stocksentinel
journalctl -u stocksentinel -f
```

The systemd unit binds `0.0.0.0:8080`, so firewall to trusted LAN or use VPN; HTTPS proxy is recommended. To access, use `http://PI-IP:8080`. If you leave SMTP disabled, checks still work but emails will fail; look for errors in Activity. For upgrades, back up `/opt/stocksentinel/data` and copy new publish files while preserving the DB and `/etc/stocksentinel.env`.

## Configuration and safety notes

- A source is **disabled by default**. Create item → create retailer source → configure selectors → run a manual check against page HTML (and ideally test fixture snapshots for both availability states) → enable automatic checks.
- HTML source selectors must belong to the **specific product listing**. Ambiguous/missing indicators or missing price yield `Unknown`, not positive stock.
- Regex `PricePattern` must capture a numeric GBP price in group `(?<price>...)`, e.g. `£\\s*(?<price>[0-9,]+(?:\\.[0-9]{2})?)` in JSON.
- Minimum polling every **5 minutes**, default 15 minutes. More aggressive checks may violate site rules / result in blocks. 429/403 triggers increasing delay. Never bypass retailer rate limits.
- Source changes reset check schedule but not the sent-alert latch; a previously alerted listing will not send duplicates unless verified unavailable before the next availability.
- Database logs contain URLs and potentially retailer error text; store securely and avoid sensitive information in source URLs.
- `STOCKSENTINEL_API_KEY` is required and must be 24+ characters; don't commit it. SMTP password supplied only via environment variable. HTTP-to-private-IP requests rejected (not a full defence against DNS rebinding in hostile environments, therefore **admin-only** configuration and trusted network still essential).
- Scheduled processing is sequential by design (single worker); scale out only after introducing distributed coordination.
- No automated tests were executed here because the build environment does not contain the .NET SDK. Run `dotnet build` and validate via local test server before Pi deployment.

## Roadmap

1. Verify a compliant reliable Nintendo UK source or suitable stock tracker API and add a dedicated source adapter.
2. Add source diagnostics/fixture tests and alert delivery attempts UI.
3. Optional HTTPS reverse proxy, CSRF protections, role-based UI, CSRF-safe key login, webhook notification providers and health checks.
4. Optional health-email on prolonged parser failure and multiple retailer/site adapters.

## REST / JSON detector (v2)

Existing SQLite databases are migrated automatically with a nullable `json_config` column. Existing HTML sources remain unchanged. To configure Nintendo UK, edit its source in the UI, choose **REST / JSON (GET)**, and set the request URL to:

`https://store.nintendo.com/api/catalog/products?q=Pokemon%20GO%20Plus%20%2B&limit=6`

Use the following rules (the matching product ID is checked before trusting any stock status):

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

Save the source **disabled**, use **Check now**, verify that it reports `Unavailable` with a diagnostic reason (not `Unknown`), and only then enable it. The first live restock must still be used to validate the API's positive signal; search results may be cached or delayed. If search results begin to contain multiple products and the desired SKU moves away from index zero, the detector will return `Unknown` rather than trusting another product. Paths support `.property` and `[0]` traversal, not arbitrary JSONPath filters.

Retain the existing Pi SQLite database, configuration and environment file during deployment. Run `./deploy-pi.sh` from the project directory on your Mac after verifying `dotnet build`. The updated deployment script also fixes root-directory and appsettings read permissions encountered during initial deployment. SMTP credentials must be configured separately and **Send test email** must succeed before relying on alerts.


## Web Push (v3)

* Deploy using `./deploy-pi.sh` from the project root. The script preserves `data/` (SQLite, subscriptions and VAPID keys).
* On iPhone, join the home Wi-Fi, browse to `https://stocksentinel.home` in Safari, and ensure the Caddy root certificate is trusted by iOS. Then Share → Add to Home Screen. Open StockSentinel **from its Home Screen icon**; login and select Notifications → Enable push on this device. Grant permission when prompted, and tap Send test notification.
* For an iPhone running current iOS, Home Screen web apps support Web Push. Sending uses Apple's push infrastructure even outside the LAN. Notification taps open the retailer purchase URL, not the local admin dashboard.
* The dashboard's push test reports acceptance by the browser push endpoint; verify a real notification arrives on the locked iPhone. If iOS rejects `.home`/local trust, this setup will require a properly trusted HTTPS hostname. No Cloudflare integration is bundled.
* Notifications are sent only for a qualifying source transition to Available. The existing SQLite per-source alert latch prevents duplicates. If delivery fails, the latch is not marked and the next check retries.
* Keep `data/push-vapid.json` private and backed up: if replaced, registered phones need to subscribe again. Service worker and manifest are publicly accessible; push registration, status and test endpoints require login.
* SMTP is now optional, used only when `Smtp:Enabled=true`.
* **Security:** Local HTTPS and a strong UI API key are required; never port-forward the admin dashboard. Do not use public access without stronger authentication and CSRF protections.
* **Build validation required:** the JavaScript syntax was checked, but this environment lacks the .NET SDK. Run `dotnet restore && dotnet build` on your Mac before the Pi deployment.


### Web Push VAPID contact

On deployment, the installer prompts for a contact email **only if** `/etc/stocksentinel.env` does not already contain `Push__VapidSubject`. It writes `Push__VapidSubject=mailto:user@example.com`. This is a public contact identifier in signed VAPID messages, not an SMTP login; it is not hashed. The existing `data/push-vapid.json` signing keys and subscriptions are preserved. For unattended deployment, set `Push__VapidSubject` in `/etc/stocksentinel.env` before running the script.

If APNs still rejects the notification with `BadJwtToken`, investigate the VAPID JWT audience, expiry, key pair, and signing compatibility; the contact URI is only one possible cause.
