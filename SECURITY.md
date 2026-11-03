# Security policy

GitHub private vulnerability reporting was **disabled** when checked on
3 November 2026. Maintainer action is required: enable **Private vulnerability
reporting** in the repository's security settings. No alternative private contact
is currently documented. Do not disclose vulnerabilities in public issues or
pull requests.

Once enabled, use GitHub's
[Report a vulnerability](https://github.com/SenorGomez/StockSentinel-Web/security/advisories/new)
form under **Security → Advisories**. Maintainers should update this policy after
enabling the reporting route.

Include the affected commit/version, reproduction steps, expected impact and any
relevant logs with credentials, personal data and private URLs removed. Never send
API keys, SMTP passwords, VAPID private keys or your runtime database.

If the private reporting form is unavailable, do not post vulnerability details
publicly; wait until private reporting is available.

Security fixes target the current `main` branch. Keep deployments updated and
follow the [README security guidance](README.md#security-and-operational-notes),
including trusted-network access and HTTPS.
