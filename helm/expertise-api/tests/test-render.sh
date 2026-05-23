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

# Helm 4 reports schema-violation paths as '/api/resources/requests/cpu' (slash-delimited,
# leading slash); Helm 3 (CI pin via azure/setup-helm @ v3.18.3) reports them as
# 'api.resources.requests.cpu:' (dot-delimited, no leading slash). The greps below use
# [./] character classes so the suite stays green across Helm 3.x and Helm 4.x.
# See #155 (issue) / #150 (Helm 4 upgrade deferral).

# 24. values.schema.json: rejects bogus Kubernetes quantity in api.resources.requests.cpu
schema_out=$(helm template test-release "$CHART" --set api.resources.requests.cpu=bogus 2>&1 || true)
if echo "$schema_out" | grep -qE 'api[./]resources[./]requests[./]cpu'; then
    ok "schema-resources-cpu-pattern" "values.schema.json rejects non-quantity cpu (bogus)"
else
    err "schema-resources-cpu-pattern" "values.schema.json did not reject api.resources.requests.cpu=bogus"
fi

# 25. values.schema.json: accepts valid Kubernetes quantity for api.resources.requests.cpu
schema_out=$(helm template test-release "$CHART" --set api.resources.requests.cpu=250m 2>&1 || true)
if echo "$schema_out" | grep -qE 'api[./]resources[./]requests[./]cpu|pattern'; then
    err "schema-resources-cpu-accept" "values.schema.json incorrectly rejected api.resources.requests.cpu=250m"
else
    ok "schema-resources-cpu-accept" "values.schema.json accepts api.resources.requests.cpu=250m"
fi

# 26. values.schema.json: rejects bogus memory quantity
schema_out=$(helm template test-release "$CHART" --set postgres.resources.limits.memory=notamemorystring 2>&1 || true)
if echo "$schema_out" | grep -qE 'postgres[./]resources[./]limits[./]memory'; then
    ok "schema-resources-memory-pattern" "values.schema.json rejects non-quantity memory"
else
    err "schema-resources-memory-pattern" "values.schema.json did not reject postgres.resources.limits.memory=notamemorystring"
fi

# 27. values.schema.json: rejects unknown resource field (additionalProperties:false)
schema_out=$(helm template test-release "$CHART" --set api.resources.requests.gpu=1 2>&1 || true)
if echo "$schema_out" | grep -qE 'api[./]resources[./]requests|additionalProperties|unevaluatedProperties'; then
    ok "schema-resources-no-extras" "values.schema.json rejects unknown resource field (gpu)"
else
    err "schema-resources-no-extras" "values.schema.json did not reject api.resources.requests.gpu=1"
fi

# 28. values.schema.json: accepts memory quantity with binary suffix (Mi/Gi)
schema_out=$(helm template test-release "$CHART" --set api.resources.requests.memory=256Mi 2>&1 || true)
if echo "$schema_out" | grep -qE 'api[./]resources[./]requests[./]memory|pattern'; then
    err "schema-memory-binary-suffix" "values.schema.json incorrectly rejected memory=256Mi"
else
    ok "schema-memory-binary-suffix" "values.schema.json accepts memory=256Mi"
fi

# 28b. values.schema.json: accepts uppercase binary kibi suffix (1Ki)
schema_out=$(helm template test-release "$CHART" --set api.resources.requests.memory=1Ki 2>&1 || true)
if echo "$schema_out" | grep -qE 'api[./]resources[./]requests[./]memory|pattern'; then
    err "schema-memory-Ki-accept" "values.schema.json incorrectly rejected memory=1Ki"
else
    ok "schema-memory-Ki-accept" "values.schema.json accepts memory=1Ki (uppercase canonical binary kibi)"
fi

# 28c. values.schema.json: rejects lowercase binary suffix (100mi is NOT a K8s quantity)
schema_out=$(helm template test-release "$CHART" --set api.resources.requests.memory=100mi 2>&1 || true)
if echo "$schema_out" | grep -qE 'api[./]resources[./]requests[./]memory'; then
    ok "schema-memory-mi-reject" "values.schema.json rejects memory=100mi (lowercase binary suffix invalid per K8s grammar)"
else
    err "schema-memory-mi-reject" "values.schema.json did not reject memory=100mi"
fi

# 28d. values.schema.json: accepts scientific notation (1e3)
schema_out=$(helm template test-release "$CHART" --set api.resources.requests.cpu=1e3 2>&1 || true)
if echo "$schema_out" | grep -qE 'api[./]resources[./]requests[./]cpu|pattern'; then
    err "schema-cpu-sci-accept" "values.schema.json incorrectly rejected cpu=1e3"
else
    ok "schema-cpu-sci-accept" "values.schema.json accepts cpu=1e3 (scientific notation)"
fi

# 28e. values.schema.json: rejects bogus suffix (KB is not a K8s quantity)
schema_out=$(helm template test-release "$CHART" --set api.resources.requests.memory=1KB 2>&1 || true)
if echo "$schema_out" | grep -qE 'api[./]resources[./]requests[./]memory'; then
    ok "schema-memory-KB-reject" "values.schema.json rejects memory=1KB (KB is not a K8s quantity suffix)"
else
    err "schema-memory-KB-reject" "values.schema.json did not reject memory=1KB"
fi

# 29. postgres.enabled=true (default): in-chart Postgres StatefulSet renders
output=$(helm template test-release "$CHART" 2>&1)
if echo "$output" | grep -qE 'kind: StatefulSet'; then
    ok "postgres-in-chart-default" "in-chart Postgres StatefulSet renders by default"
else
    err "postgres-in-chart-default" "in-chart Postgres StatefulSet missing from default render"
fi
if echo "$output" | grep -qE 'name: test-release-postgres'; then
    ok "postgres-in-chart-svc" "in-chart Postgres Service renders by default"
else
    err "postgres-in-chart-svc" "in-chart Postgres Service missing from default render"
fi

# 30. postgres.enabled=false: in-chart Postgres + PgBouncer resources skipped
output=$(helm template test-release "$CHART" --set postgres.enabled=false --set networkPolicy.enabled=false 2>&1)
if echo "$output" | grep -qE 'kind: StatefulSet'; then
    err "postgres-external-no-sts" "StatefulSet rendered with postgres.enabled=false"
else
    ok "postgres-external-no-sts" "StatefulSet skipped when postgres.enabled=false"
fi
if echo "$output" | grep -qE 'name: test-release-postgres$'; then
    err "postgres-external-no-svc" "postgres Service rendered with postgres.enabled=false"
else
    ok "postgres-external-no-svc" "postgres Service skipped when postgres.enabled=false"
fi
if echo "$output" | grep -qE 'name: test-release-pgbouncer$'; then
    err "postgres-external-no-pgb-cm" "pgbouncer ConfigMap rendered with postgres.enabled=false"
else
    ok "postgres-external-no-pgb-cm" "pgbouncer ConfigMap skipped when postgres.enabled=false"
fi
# Deployment must still render (the API itself is unaffected by external-postgres mode)
if echo "$output" | grep -qE 'kind: Deployment'; then
    ok "postgres-external-api-still" "API Deployment still renders with postgres.enabled=false"
else
    err "postgres-external-api-still" "API Deployment missing with postgres.enabled=false"
fi

# 31. postgres.enabled=false: schema allows postgres without image/secretName
schema_out=$(helm template test-release "$CHART" --set postgres.enabled=false --set postgres.image=null --set postgres.secretName=null --set networkPolicy.enabled=false 2>&1 || true)
if echo "$schema_out" | grep -qE "postgres.*missing property|postgres:.*is required"; then
    err "postgres-external-relax-required" "schema incorrectly required postgres.image/secretName when enabled=false"
else
    ok "postgres-external-relax-required" "schema relaxes postgres.image/secretName when enabled=false"
fi

# 32. postgres.enabled=true (default): schema still requires postgres.image
schema_out=$(helm template test-release "$CHART" --set postgres.image=null 2>&1 || true)
if echo "$schema_out" | grep -qE "missing property 'image'|postgres: image is required"; then
    ok "postgres-default-still-requires" "schema still requires postgres.image when enabled=true"
else
    err "postgres-default-still-requires" "schema did not require postgres.image when enabled=true"
fi

# 33. NetworkPolicy: -postgres policy skipped when postgres.enabled=false
output=$(helm template test-release "$CHART" --set postgres.enabled=false --set networkPolicy.enabled=true --set 'postgres.external.cidr=10.20.0.0/16' 2>&1)
if echo "$output" | grep -qE 'name: test-release-postgres$'; then
    err "netpol-postgres-skipped" "-postgres NetworkPolicy rendered with postgres.enabled=false"
else
    ok "netpol-postgres-skipped" "-postgres NetworkPolicy skipped when postgres.enabled=false"
fi

# 34. NetworkPolicy egress: external mode emits ipBlock egress to postgres.external.cidr
if echo "$output" | grep -q '10.20.0.0/16'; then
    ok "netpol-external-egress" "API NetworkPolicy egress includes postgres.external.cidr ipBlock"
else
    err "netpol-external-egress" "API NetworkPolicy egress missing postgres.external.cidr ipBlock"
fi

# 35. NetworkPolicy egress: in-chart mode emits podSelector egress (not ipBlock)
output=$(helm template test-release "$CHART" --set networkPolicy.enabled=true 2>&1)
if echo "$output" | grep -q 'port: 6432'; then
    ok "netpol-inchart-egress" "API NetworkPolicy egress targets pgbouncer port 6432 in in-chart mode"
else
    err "netpol-inchart-egress" "API NetworkPolicy egress missing port 6432 in in-chart mode"
fi

# 36. external.cidr schema: rejects bogus CIDR
schema_out=$(helm template test-release "$CHART" --set postgres.enabled=false --set 'postgres.external.cidr=notacidr' 2>&1 || true)
if echo "$schema_out" | grep -qE 'postgres[./]external[./]cidr'; then
    ok "schema-external-cidr-pattern" "schema rejects bogus postgres.external.cidr"
else
    err "schema-external-cidr-pattern" "schema did not reject postgres.external.cidr=notacidr"
fi

# 37. NetworkPolicy egress: external mode without external.cidr renders cleanly (no nil-deref)
# Regression test for round-2 finding: 'else if .Values.postgres.external.cidr' nil-derefs
# when external is unset. Combined postgres.enabled=false + networkPolicy.enabled=true +
# postgres.external unset must render (with no postgres egress rule — operator must add their own).
output=$(helm template test-release "$CHART" --set postgres.enabled=false --set networkPolicy.enabled=true 2>&1)
if echo "$output" | grep -qE '^Error:'; then
    err "netpol-external-nil-deref" "NetworkPolicy template nil-derefs when external is unset: $(echo "$output" | grep Error: | head -1)"
else
    ok "netpol-external-nil-deref" "NetworkPolicy renders cleanly when external is unset (no nil-deref)"
fi

# 38. Deployment probes: liveness uses /health/live (issue #143, T4).
output=$(helm template test-release "$CHART" 2>&1)
if echo "$output" | awk '/livenessProbe:/,/readinessProbe:/' | grep -qE 'path:[[:space:]]*"?/health/live"?'; then
    ok "probe-liveness-path" "livenessProbe.httpGet.path = /health/live"
else
    err "probe-liveness-path" "livenessProbe.httpGet.path did not render as /health/live"
fi

# 39. Deployment probes: readiness uses /health/ready (issue #143, T4).
if echo "$output" | awk '/readinessProbe:/,/^      volumes:/' | grep -qE 'path:[[:space:]]*"?/health/ready"?'; then
    ok "probe-readiness-path" "readinessProbe.httpGet.path = /health/ready"
else
    err "probe-readiness-path" "readinessProbe.httpGet.path did not render as /health/ready"
fi

# 40. Deployment probes: startup uses /health/ready so the pod isn't declared
# started until DB, ONNX, and migration checks all pass (issue #143, T4).
if echo "$output" | awk '/startupProbe:/,/livenessProbe:/' | grep -qE 'path:[[:space:]]*"?/health/ready"?'; then
    ok "probe-startup-path" "startupProbe.httpGet.path = /health/ready"
else
    err "probe-startup-path" "startupProbe.httpGet.path did not render as /health/ready"
fi

# 41. Image tag falls back to .Chart.AppVersion when image.tag is empty/unset (issue #214).
appver=$(awk '/^appVersion:/ {gsub(/"/,"",$2); print $2}' "$CHART/Chart.yaml")
if echo "$output" | grep -qE "image: \"ghcr.io/thesemicolon/agent-expertise-api:${appver}\""; then
    ok "image-tag-default-fallback" "image.tag empty falls back to chart appVersion ${appver}"
else
    err "image-tag-default-fallback" "default render did not produce :${appver} image tag"
fi

# 42. api.probes.*.path overrides flow through to the deployment (issue #216).
out_override=$(helm template test-release "$CHART" \
    --set api.probes.liveness.path=/healthz \
    --set api.probes.readiness.path=/readyz \
    --set api.probes.startup.path=/readyz 2>&1)
if echo "$out_override" | awk '/livenessProbe:/,/readinessProbe:/' | grep -qE 'path:[[:space:]]*"?/healthz"?' \
    && echo "$out_override" | awk '/readinessProbe:/,/^      volumes:/' | grep -qE 'path:[[:space:]]*"?/readyz"?' \
    && echo "$out_override" | awk '/startupProbe:/,/livenessProbe:/' | grep -qE 'path:[[:space:]]*"?/readyz"?'; then
    ok "probe-path-override" "api.probes.*.path values flow through to deployment"
else
    err "probe-path-override" "probe path overrides not honoured by deployment template"
fi

# 43. Schema rejects probe paths without a leading / (issue #216).
out_bad_path=$(helm template test-release "$CHART" --set api.probes.liveness.path=health/live 2>&1 || true)
# Match both helm 3.13+ ("'health/live' does not match pattern '^/.*'") and
# older helm renderings ("api.probes.liveness.path: Does not match pattern '^/.*'")
# which differ only in casing of "does" and the value-quoting prefix.
if echo "$out_bad_path" | grep -qiE "does not match pattern '\^/"; then
    ok "schema-probe-path-leading-slash" "schema rejects probe path without leading /"
else
    err "schema-probe-path-leading-slash" "schema accepted invalid probe path: $(echo "$out_bad_path" | tail -2 | tr '\n' ' ')"
fi

# 44. Migrations Job inherits the same image-tag default-to-appVersion fallback
#     as the API Deployment (issue #214 — prevents migration/runtime image skew).
appver=$(awk '/^appVersion:/ {gsub(/"/, "", $2); print $2}' "$CHART/Chart.yaml")
out_jobimg=$(helm template test-release "$CHART" 2>&1)
if echo "$out_jobimg" | awk '/kind: Job/,/^---/' | grep -qF "image: \"ghcr.io/thesemicolon/agent-expertise-api:${appver}\""; then
    ok "migrations-image-tag-default-fallback" "migrations Job image falls back to chart appVersion ${appver}"
else
    err "migrations-image-tag-default-fallback" "migrations Job image did not fall back to chart appVersion ${appver}"
fi

# 45. The `api` parent block in values.schema.json must remain permissive — the
#     PR-232 review surfaced that an over-broad `additionalProperties: false`
#     at this level would silently reject operator-side overlay keys. Guard
#     against accidental re-tightening by asserting an unknown sibling key
#     under `api` is accepted at install-time (specifically: an unknown key
#     that is NOT under `api.probes`, which is intentionally closed).
out_apiextra=$(helm template test-release "$CHART" --set api.unknownOverlayKey=somevalue 2>&1 || true)
if echo "$out_apiextra" | grep -qiE "additional properties.*not allowed|does not match"; then
    err "schema-api-block-permissive" "schema unexpectedly rejected an unknown sibling key under api (api block has been over-tightened)"
else
    ok "schema-api-block-permissive" "api block remains permissive for operator overlay keys (api.probes still closed)"
fi

# 46. Schema accepts SemVer prerelease tags on image.tag (issue #214 — release
#     workflow may pin rc/beta tags during release-candidate cycles).
out_pretag=$(helm template test-release "$CHART" --set image.tag=0.1.4-rc.1 2>&1 || true)
if echo "$out_pretag" | grep -qF "image: \"ghcr.io/thesemicolon/agent-expertise-api:0.1.4-rc.1\""; then
    ok "schema-image-tag-prerelease" "schema accepts SemVer prerelease tag (0.1.4-rc.1)"
else
    err "schema-image-tag-prerelease" "prerelease tag rejected or rendered wrong: $(echo "$out_pretag" | tail -2 | tr '\n' ' ')"
fi

echo "=================================="
if [ "$ERRORS" -eq 0 ]; then
    echo "PASS — 0 errors, $WARNINGS warning(s)"
    exit 0
else
    echo "FAIL — $ERRORS error(s), $WARNINGS warning(s)"
    exit 1
fi
