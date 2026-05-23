# Security scanning stack

- Status: accepted
- Date: 2026-04-30

## Context and Problem Statement

This repository is a public personal project on GitHub. Without automated scanning, security regressions (vulnerable dependencies, hardcoded secrets, dangerous Dockerfile patterns, insecure code) reach `dev` and `main` undetected. A scanning stack must be picked that fits the constraints: free for a solo developer, integrates with existing GitHub Actions CI/CD, and emits findings into a unified dashboard rather than scattered tool-specific UIs.

## Considered Options

- **Commercial SAST/SCA platform (e.g., Checkmarx One, Snyk, SonarQube Cloud).** Strong coverage including reachability-aware SCA and API security spec analysis, but no meaningful free tier for personal use; pricing is enterprise-contract.
- **Pure FOSS stack (Semgrep + OSV-Scanner + Gitleaks + Trivy + Hadolint + kube-linter).** Fully free, broad coverage. Each tool runs as a separate GitHub Action and emits SARIF to the Code Scanning tab. Loses the depth of CodeQL's inter-procedural dataflow for C#.
- **GitHub-native (GHAS) + minimal FOSS supplement.** GHAS features — CodeQL, secret scanning, push protection, Dependabot, dependency review — are free for public repos. FOSS tools fill the gaps that GHAS doesn't cover (container layer CVEs since GHCR scanning is enterprise-only; Dockerfile shell-level lints; .NET first-party analyzers).

## Decision Outcome

Chosen option: **GHAS + minimal FOSS supplement**, because it delivers near-Checkmarx coverage at no cost for a public personal repo, and because CodeQL is genuinely competitive with commercial SAST for C# dataflow analysis.

The stack:

| Layer | Tool | Where it runs |
| --- | --- | --- |
| SAST | CodeQL (advanced setup, explicit `dotnet build`) | `.github/workflows/codeql.yml` |
| SCA | Dependabot alerts + `dotnet list package --vulnerable` | Repo settings + `ci.yml` |
| Secrets | GitHub secret scanning + push protection | Repo settings |
| Container/IaC | Trivy filesystem scan (NuGet, Helm, Dockerfile) | `.github/workflows/security.yml` |
| Dockerfile lint | Hadolint | `.github/workflows/security.yml` |
| .NET analyzers | `<AnalysisMode>All</AnalysisMode>` + `<AnalysisLevel>10.0</AnalysisLevel>` | `Directory.Build.props` |
| Dependency PRs | Dependabot version updates | `.github/dependabot.yml` |

CodeQL uses **advanced setup** rather than default setup because the repository's `.slnx` solution file and Docker-entangled build path make autobuild unreliable. The workflow restores and builds explicitly, mirroring `ci.yml`.

The .NET analyzer rollout is **two-step**: this ADR adopts analyzers as warnings (not errors) so the build remains green while the existing 201 main-project warnings are triaged. A follow-up ADR will record the decision to flip `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` once the baseline is clean.

The test project overrides `<AnalysisMode>Minimum</AnalysisMode>` because xUnit conventions (underscores in method names, `Random` for non-cryptographic test data) trigger false positives that obscure real findings in production code.

### Consequences

- Good, because the stack is free, all findings flow into the GitHub Security tab as one dashboard, and CodeQL provides credible C# dataflow coverage.
- Good, because Dependabot version updates + security alerts give async dependency hygiene without a paid SCA platform.
- Good, because Trivy fills the GHCR container-scanning gap that personal GitHub plans don't include.
- Bad, because reachability-aware SCA (verifying whether your code calls vulnerable functions) is not available in the FOSS layer — every transitive CVE surfaces, requiring manual triage.
- Bad, because API security spec analysis (OpenAPI-aware OWASP API Top 10 detection) has no FOSS equivalent. The OpenAPI spec must be reviewed manually.
- Bad, because if this repository ever turns private without a paid GHAS license, CodeQL and secret scanning go dark immediately. Mitigation: replace with Semgrep + Gitleaks at that point.
- Neutral, because the 201 deferred .NET analyzer warnings are tracked as follow-up work rather than blocking the security-scanning rollout.
