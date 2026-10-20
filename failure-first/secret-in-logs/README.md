# Secret accidentally written to logs

**Attack:** a tool or model output contains a synthetic secret/PII sentinel and is passed to the logger.

**Expected block:** telemetry contains none of the sentinel. This case checks the redaction path using only test fixtures already covered by `.gitleaks.toml`.

**Reproduce:** `dotnet test tests\Telemetry.Tests\Telemetry.Tests.csproj --filter "FullyQualifiedName~Logs_never_contain_secrets_or_pii"`

**Fix:** do not log arguments, results, or free-text model output; send diagnostic fields through `Redactor` and keep the OpenTelemetry redacting processor installed.
