# Contributing

Start with the [README](README.md#local-development) for .NET 10 setup and local
configuration. Never commit local settings, API keys, credentials or runtime data.

Create a focused branch and open pull requests against **develop**. Describe the
change and how you checked it. Run the relevant checks from the repository root:

```bash
dotnet build --configuration Release
dotnet test tests/StockSentinel.Tests/StockSentinel.Tests.csproj --configuration Release
node --check wwwroot/app.js
node --check wwwroot/sw.js
bash -n deploy-pi.sh
bash -n deploy/install-pi.sh
python3 tests/deployment-tests.py
python3 tests/verify-publish.py
gitleaks git --redact --log-opts=--all .
```

Script/publish checks require Python 3, Node.js and Bash; secret scanning requires
Gitleaks. CI runs these checks without deployment credentials. Do not run
`deploy-pi.sh` as a validation step: it deploys to a real host.

Dependency changes must update [third-party notices](THIRD-PARTY-NOTICES.txt) for
the resolved versions and preserve required notices/source availability in publish
output. Submit only code and assets you have the right to contribute under the
[MIT licence](LICENSE).

Report vulnerabilities privately using the [security policy](SECURITY.md).
