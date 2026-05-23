# Internal types by default; tests via `InternalsVisibleTo`

- Status: accepted
- Date: 2026-05-04

## Context and Problem Statement

The .NET analyzer baseline (issue #101) carries 108 occurrences of CA1515 ("consider making types internal") in `src/ExpertiseApi/`. The baseline blocks any future tightening of the build gate — `dotnet format --verify-no-changes` cannot be enforced in CI until the warning count is in single digits.

The project is a single-assembly ASP.NET Core service. The only cross-assembly consumer of types from `src/ExpertiseApi/` is the test assembly (`ExpertiseApi.Tests`). There is no SDK NuGet, no shared-contract package, and no second non-test project. The `public` access modifier on application types therefore conveys no meaningful API contract — it is the C# default, not an intentional surface.

## Considered Options

1. **Global suppression of CA1515 in `Directory.Build.props`** with a rationale comment ("application code, no library consumers")
2. **Move types to `internal`** and grant the test project access via `[InternalsVisibleTo]`
3. **Status quo** — leave types `public` and accept the ongoing analyzer noise

## Decision Outcome

Chosen option: **2 — move types to `internal` + `InternalsVisibleTo`**.

The accessibility modifier should reflect intent. `internal` accurately describes 99% of types in this codebase (everything except `Program`, which `WebApplicationFactory<Program>` requires `public`). Suppressing the analyzer would silence the signal but leave the code carrying false claims about its own API surface; flipping the types puts the modifier where it belongs and recovers the analyzer signal for any genuinely-public API we might add later.

### Carve-outs (stay `public`)

Three categories are explicitly excluded from the sweep, with inline `[SuppressMessage]` annotations explaining each:

1. **`Program` (`src/ExpertiseApi/Program.cs:150`).** `WebApplicationFactory<TEntryPoint>` requires `TEntryPoint` to be visible to the test assembly via the C# type system, not via `[InternalsVisibleTo]` — the constraint crosses into `Microsoft.AspNetCore.Mvc.Testing`, a third-party assembly that the IVT grant does not cover. The `public partial class Program;` stub is the canonical pattern; do not remove or flip it.
2. **`AuthMode` (`src/ExpertiseApi/Auth/AuthMode.cs`).** Consumed as a parameter type by `[Theory]` tests in `AuthModeStartupGuardTests.cs`. xUnit 2 (current) requires test methods to be `public`, and public methods cannot take internal parameter types. Cascading the test method to `internal` is not viable in xUnit 2. If/when the test project upgrades to xUnit 3 (which supports non-public test methods), `AuthMode` can flip to `internal`.
3. **All classes in `src/ExpertiseApi/Migrations/`.** EF migration scaffolding (`dotnet ef migrations add`) emits `public partial class` by default. Flipping the existing migrations to `internal` would create persistent drift — every future migration would re-add as `public`. Suppressed via folder-scoped `.editorconfig` rather than per-file annotations.

The fourth carve-out originally proposed — `DesignTimeDbContextFactory` — was dropped after empirical validation: EF Core's `dotnet ef` tooling honors `internal` factories via reflection (`Activator.CreateInstance` with non-public binding flags), so the defensive concern was unfounded. The factory is now `internal`.

### `[InternalsVisibleTo]` entries

The API project already grants `InternalsVisibleTo("ExpertiseApi.Tests")`. This ADR adds:

- **`InternalsVisibleTo("DynamicProxyGenAssembly2")`** — required for NSubstitute (which uses Castle DynamicProxy) to mock `internal` interfaces. Without this entry, `Substitute.For<IInternalInterface>()` throws `TypeLoadException` at first invocation — silent at build time, silent at xUnit discovery, fails only when the test runs. This is a well-known Castle convention; the assembly name is fixed.

### Consequences

- **Good:** the access modifier becomes truthful. CA1515 drops from 108 occurrences to 0–3 (the carve-outs). The build gets noticeably closer to the single-digit baseline that gates `dotnet format` enforcement in CI.
- **Good:** changes to internal types no longer require the same care as public-API changes — refactoring is easier.
- **Bad:** forecloses the path of consuming these types from another assembly without superseding this ADR. If the project grows a typed-client SDK, a shared-contract package, or splits into multiple projects, the relevant types will need to flip back to `public` (or move to a new shared assembly).
- **Bad:** strong-naming the API assembly later would require updating the IVT entries to include the public-key tokens of the test assembly and `DynamicProxyGenAssembly2`. Recorded as a known follow-up; not in scope today.
- **Neutral:** ASP.NET Core JSON model binding, DI registration, EF Core entity discovery, and xUnit `[InlineData]` enum constants all work identically with `internal` types — verified by full unit-test pass after the sweep.

## References

- Issue: [#101](https://github.com/TheSemicolon/agent-expertise-api/issues/101)
- PR: this ADR's PR (the CA1515 sweep)
- Related: [ADR-004](004-security-scanning-stack.md) records the original 201-warning baseline; this ADR moves CA1515 (the largest contributor) toward closure.
