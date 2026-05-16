#!/usr/bin/env bash
# test-render.sh — Helm template render assertions for expertise-api chart
#
# Usage: bash helm/expertise-api/tests/test-render.sh
# Exit codes: 0 = all checks passed, 1 = one or more errors, 2 = missing prerequisite

set -euo pipefail

CHART="$(cd "$(dirname "$0")/.." && pwd)"
ERRORS=0
WARNINGS=0

ok()    { echo "OK    [$1] $2"; }
# shellcheck disable=SC2317  # part of canonical helper set
skip()  { echo "SKIP  [$1] $2"; }
warn()  { echo "WARN  [$1] $2"; WARNINGS=$((WARNINGS + 1)); }
err()   { echo "ERROR [$1] $2" >&2; ERRORS=$((ERRORS + 1)); }

if ! command -v helm &>/dev/null; then
    echo "ERROR [prereq] helm not found in PATH" >&2
    exit 2
fi

# 1. ServiceMonitor present when metrics.enabled=true
output=$(helm template test-release "$CHART" --set metrics.enabled=true 2>&1)
if echo "$output" | grep -q "kind: ServiceMonitor"; then
    ok "sm-enabled" "ServiceMonitor renders when metrics.enabled=true"
else
    err "sm-enabled" "ServiceMonitor missing when metrics.enabled=true"
fi

# 2. ServiceMonitor absent when metrics.enabled=false (default)
output=$(helm template test-release "$CHART" 2>&1)
if ! echo "$output" | grep -q "kind: ServiceMonitor"; then
    ok "sm-disabled" "ServiceMonitor absent when metrics.enabled=false"
else
    err "sm-disabled" "ServiceMonitor present when metrics.enabled=false"
fi

# 3. Service port has name 'http'
output=$(helm template test-release "$CHART" 2>&1)
if echo "$output" | grep -q "name: http"; then
    ok "port-name" "Service port name is 'http'"
else
    err "port-name" "Service port name 'http' not found"
fi

# 4. ServiceMonitor references port 'http'
output=$(helm template test-release "$CHART" --set metrics.enabled=true 2>&1)
if echo "$output" | grep -q "port: http"; then
    ok "sm-port-ref" "ServiceMonitor references port 'http'"
else
    err "sm-port-ref" "ServiceMonitor does not reference port 'http'"
fi

# 5. ServiceMonitor scrapes /metrics path
if echo "$output" | grep -q "path: /metrics"; then
    ok "sm-path" "ServiceMonitor scrapes /metrics path"
else
    err "sm-path" "ServiceMonitor /metrics path not found"
fi

# 6. auth.mode default in API container env is "Oidc"
output=$(helm template test-release "$CHART" 2>&1)
if echo "$output" | awk '/name: AUTH__MODE/{found=1; next} found && /value:/{print; exit}' | grep -q '"Oidc"'; then
    ok "auth-mode-default" "AUTH__MODE defaults to Oidc (startup guard requires it outside Development)"
else
    err "auth-mode-default" "AUTH__MODE default is not Oidc"
fi

# 7. OIDC issuer env vars render when auth.oidc.issuers is populated
output=$(helm template test-release "$CHART" \
    --set 'auth.oidc.issuers[0].name=Entra' \
    --set 'auth.oidc.issuers[0].issuer=https://login.microsoftonline.com/x/v2.0' \
    --set 'auth.oidc.issuers[0].audience=api' 2>&1)
if echo "$output" | grep -q "Auth__Oidc__Issuers__0__Name" \
   && echo "$output" | grep -q "Auth__Oidc__Issuers__0__Issuer" \
   && echo "$output" | grep -q "Auth__Oidc__Issuers__0__Audience"; then
    ok "oidc-issuer-env" "Auth__Oidc__Issuers__0__{Name,Issuer,Audience} env vars rendered"
else
    err "oidc-issuer-env" "OIDC issuer env vars not rendered when auth.oidc.issuers is populated"
fi

# 8. ForwardedHeaders env vars render when forwardedHeaders.trustedCidr is populated
output=$(helm template test-release "$CHART" \
    --set 'forwardedHeaders.trustedCidr[0]=10.42.0.0/16' 2>&1)
if echo "$output" | grep -q "ForwardedHeaders__KnownNetworks__0"; then
    ok "forwarded-headers" "ForwardedHeaders__KnownNetworks__0 env var rendered"
else
    err "forwarded-headers" "ForwardedHeaders env var not rendered when forwardedHeaders.trustedCidr is populated"
fi

# 9. API container drops ALL capabilities
output=$(helm template test-release "$CHART" 2>&1)
api_section=$(echo "$output" | awk '/^kind: Deployment$/,/^---$/')
if echo "$api_section" | grep -q 'drop: \["ALL"\]'; then
    ok "api-caps-drop" "API container drops ALL capabilities"
else
    err "api-caps-drop" "API container missing capabilities.drop: [ALL]"
fi

# 10. API pod sets seccompProfile RuntimeDefault
if echo "$api_section" | awk '/seccompProfile:/{found=1; next} found && /type:/{print; exit}' | grep -q "RuntimeDefault"; then
    ok "api-seccomp" "API pod seccompProfile set to RuntimeDefault"
else
    err "api-seccomp" "API pod missing seccompProfile RuntimeDefault"
fi

# 11. Postgres pod has runAsNonRoot: true
postgres_section=$(echo "$output" | awk '/^kind: StatefulSet$/,/^---$/')
if echo "$postgres_section" | grep -q "runAsNonRoot: true"; then
    ok "postgres-non-root" "Postgres pod runAsNonRoot: true"
else
    err "postgres-non-root" "Postgres pod missing runAsNonRoot: true"
fi

# 12. pgbouncer container has securityContext (drop ALL)
# Both postgres and pgbouncer containers drop ALL — verify at least 2 occurrences in the StatefulSet
caps_count=$(echo "$postgres_section" | grep -c 'drop: \["ALL"\]' || true)
if [ "$caps_count" -ge 2 ]; then
    ok "postgres-pgbouncer-caps" "Both postgres and pgbouncer containers drop ALL capabilities"
else
    err "postgres-pgbouncer-caps" "Expected 2+ 'drop: [ALL]' in StatefulSet, found $caps_count"
fi

# 13. Backup CronJob is gone (moved to sidecar)
if ! echo "$output" | grep -q "kind: CronJob"; then
    ok "no-backup-cronjob" "Backup CronJob removed (handled by sidecar in infra repo)"
else
    err "no-backup-cronjob" "Backup CronJob still present in chart — should be dropped"
fi

# 14. ingress.className is read from values (not hardcoded "nginx")
output=$(helm template test-release "$CHART" --set ingress.className=traefik 2>&1)
if echo "$output" | grep -q 'ingressClassName: traefik'; then
    ok "ingress-class-name" "Ingress className reflects values.ingress.className"
else
    err "ingress-class-name" "Ingress className did not honor values.ingress.className=traefik"
fi

# 15. NetworkPolicy renders when networkPolicy.enabled=true (default false)
output=$(helm template test-release "$CHART" --set networkPolicy.enabled=true 2>&1)
np_count=$(echo "$output" | grep -c '^kind: NetworkPolicy$' || true)
if [ "$np_count" -ge 2 ]; then
    ok "netpol-renders" "Both API and postgres NetworkPolicy render when networkPolicy.enabled=true"
else
    err "netpol-renders" "Expected 2 NetworkPolicy resources when enabled, found $np_count"
fi

# 16. NetworkPolicy absent when networkPolicy.enabled=false (default)
output=$(helm template test-release "$CHART" 2>&1)
if ! echo "$output" | grep -q '^kind: NetworkPolicy$'; then
    ok "netpol-default-off" "NetworkPolicy absent by default (networkPolicy.enabled=false)"
else
    err "netpol-default-off" "NetworkPolicy unexpectedly present when networkPolicy.enabled is unset"
fi

# 17. Migration Job renders by default (migrations.enabled=true)
output=$(helm template test-release "$CHART" 2>&1)
if echo "$output" | grep -q '^kind: Job$' && echo "$output" | grep -q 'helm.sh/hook: pre-install,pre-upgrade'; then
    ok "migrations-default-on" "Migration Job renders by default with pre-install,pre-upgrade hooks"
else
    err "migrations-default-on" "Migration Job missing or hook annotations not set"
fi

# 18. Migration Job omitted when migrations.enabled=false
output=$(helm template test-release "$CHART" --set migrations.enabled=false 2>&1)
if ! echo "$output" | grep -q '^kind: Job$'; then
    ok "migrations-opt-out" "Migration Job omitted when migrations.enabled=false"
else
    err "migrations-opt-out" "Migration Job still present when migrations.enabled=false"
fi

# 19. NOTES.txt source contains the ingress URL substring (parameterized on .Values.ingress.hostname)
# `helm template` does not render NOTES.txt — that's install-output only — so we verify the source
# template carries the substrings we expect post-render. Combined with #21 (helm lint --strict)
# below, this catches both missing content and template-syntax errors.
if grep -q '{{ .Values.ingress.hostname }}' "$CHART/templates/NOTES.txt"; then
    ok "notes-url-ingress" "NOTES.txt source references .Values.ingress.hostname"
else
    err "notes-url-ingress" "NOTES.txt does not reference .Values.ingress.hostname"
fi

# 20. NOTES.txt source contains the trustedCidr-empty warning text
if grep -q 'forwardedHeaders.trustedCidr is empty' "$CHART/templates/NOTES.txt"; then
    ok "notes-trusted-cidr-warn" "NOTES.txt source contains trustedCidr empty-list warning"
else
    err "notes-trusted-cidr-warn" "NOTES.txt missing trustedCidr warning"
fi

# 21. helm lint --strict catches NOTES.txt template-syntax errors (Go template parse failures)
lint_out=$(helm lint --strict "$CHART" 2>&1 || true)
if echo "$lint_out" | grep -qE '0 chart\(s\) failed'; then
    ok "lint-strict" "helm lint --strict passes (validates NOTES.txt template syntax)"
else
    err "lint-strict" "helm lint --strict failed: $lint_out"
fi

# 22. values.schema.json: rejects auth.mode outside the enum (helm template exits non-zero on schema failure)
schema_out=$(helm template test-release "$CHART" --set auth.mode=Bogus 2>&1 || true)
if echo "$schema_out" | grep -q 'auth.mode'; then
    ok "schema-auth-mode-enum" "values.schema.json rejects invalid auth.mode"
else
    err "schema-auth-mode-enum" "values.schema.json did not reject auth.mode=Bogus"
fi

# 23. values.schema.json: rejects replicaCount=0
schema_out=$(helm template test-release "$CHART" --set replicaCount=0 2>&1 || true)
if echo "$schema_out" | grep -qE 'replicaCount|minimum'; then
    ok "schema-replicacount-min" "values.schema.json rejects replicaCount=0"
else
    err "schema-replicacount-min" "values.schema.json did not reject replicaCount=0"
fi

# 24. values.schema.json: rejects bogus Kubernetes quantity in api.resources.requests.cpu
schema_out=$(helm template test-release "$CHART" --set api.resources.requests.cpu=bogus 2>&1 || true)
if echo "$schema_out" | grep -q 'api/resources/requests/cpu'; then
    ok "schema-resources-cpu-pattern" "values.schema.json rejects non-quantity cpu (bogus)"
else
    err "schema-resources-cpu-pattern" "values.schema.json did not reject api.resources.requests.cpu=bogus"
fi

# 25. values.schema.json: accepts valid Kubernetes quantity for api.resources.requests.cpu
schema_out=$(helm template test-release "$CHART" --set api.resources.requests.cpu=250m 2>&1 || true)
if echo "$schema_out" | grep -qE 'api/resources/requests/cpu|pattern'; then
    err "schema-resources-cpu-accept" "values.schema.json incorrectly rejected api.resources.requests.cpu=250m"
else
    ok "schema-resources-cpu-accept" "values.schema.json accepts api.resources.requests.cpu=250m"
fi

# 26. values.schema.json: rejects bogus memory quantity
schema_out=$(helm template test-release "$CHART" --set postgres.resources.limits.memory=notamemorystring 2>&1 || true)
if echo "$schema_out" | grep -q 'postgres/resources/limits/memory'; then
    ok "schema-resources-memory-pattern" "values.schema.json rejects non-quantity memory"
else
    err "schema-resources-memory-pattern" "values.schema.json did not reject postgres.resources.limits.memory=notamemorystring"
fi

# 27. values.schema.json: rejects unknown resource field (additionalProperties:false)
schema_out=$(helm template test-release "$CHART" --set api.resources.requests.gpu=1 2>&1 || true)
if echo "$schema_out" | grep -qE 'api/resources/requests|additionalProperties|unevaluatedProperties'; then
    ok "schema-resources-no-extras" "values.schema.json rejects unknown resource field (gpu)"
else
    err "schema-resources-no-extras" "values.schema.json did not reject api.resources.requests.gpu=1"
fi

# 28. values.schema.json: accepts memory quantity with binary suffix (Mi/Gi)
schema_out=$(helm template test-release "$CHART" --set api.resources.requests.memory=256Mi 2>&1 || true)
if echo "$schema_out" | grep -qE 'api/resources/requests/memory|pattern'; then
    err "schema-memory-binary-suffix" "values.schema.json incorrectly rejected memory=256Mi"
else
    ok "schema-memory-binary-suffix" "values.schema.json accepts memory=256Mi"
fi

echo "=================================="
if [ "$ERRORS" -eq 0 ]; then
    echo "PASS — 0 errors, $WARNINGS warning(s)"
    exit 0
else
    echo "FAIL — $ERRORS error(s), $WARNINGS warning(s)"
    exit 1
fi
