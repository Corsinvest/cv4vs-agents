---
name: cv4vs-ci-log
description: Read a GitHub Actions run log for this repo and report whether the run actually did what it should — step outcomes, expected verifications present or missing, and real anomalies with the known noise filtered out. Use when asked to check, read or verify a CI run, a workflow log, a build on GitHub, or why a build passed. Read-only.
---

# Read a CI run

A green run is not evidence the package is good. On 2026-07-21 three separate defects shipped
through a `success` build:

- The VSIX was packaged without the `WebView2/` folder — 16 entries instead of 22. It installed
  cleanly and threw `DirectoryNotFoundException` the moment the chat pane opened.
- `dotnet-typegen` was not installed on the runners: the `Exec` exited 9009, `ContinueOnError`
  swallowed it, and the DTO codegen never ran.
- `tgconfig.json` was shipped inside the VSIX (23 entries instead of 22).

None of them was an error in the log. They were **absences** — a line that should have been printed
and wasn't, or an `MSB3073` buried under 147 known warnings. Reading the log by hand took five
attempts and two wrong filters, because CodeQL query *names* contain the word "error".

This skill reads a finished run and answers one question: did every check actually run, and did
anything real go wrong?

**Read-only.** No fixes, no re-runs, no git commands.

## Procedure

### 1. Find the run

```powershell
gh run list --branch <branch> --limit 1 --json databaseId,headSha,status,conclusion
```

No argument given → last run of the current branch. An argument may be a run id, a PR number, or a
branch name.

Two things to check before going further:

- **`status` must be `completed`.** On a run still going the expected lines have not been printed
  yet and would read as absences. Say so and stop — this skill does not wait.
- **Compare `headSha` with `git rev-parse HEAD`.** Called right after a push, the last run of the
  branch is still the previous commit's: the report would be accurate but about different code.
  Say so; the user decides whether to continue.

### 2. Collect

```powershell
gh run view <id> --json jobs        # steps and their conclusions
gh run view <id> --log              # full log, ~2400 lines
```

Write the log to a file and filter from there. Keeping 2400 lines in context to find two of them is
waste.

### 3. Filter the noise

Everything below is background and must not reach the report — except as a count.

| Category | Pattern |
|---|---|
| CodeQL banners | `CODEQL_ACTION_CLI_VERSION_INFO`, `CODEQL_DIST`, any `CODEQL_*` |
| CodeQL query names | `Loaded C:`, `Starting evaluation`, `Evaluation done`, `.qlx`, `.bqrs` |
| Script echo | lines containing `^[[36;1m` |
| Known warnings | `VSTHRD*`, `VSSDK*` (147 of them), `no-explicit-any` (7) |
| Infrastructure deprecations | Node 20→24, CodeQL Action v3, `DEP0169`, `npm warn deprecated` |
| CodeQL informational | `overlay database`, `TRAP caching` |

Two traps worth naming, both of which produced false positives when this was done by hand:

- **CodeQL query names read like failures.** `MissingASPNETGlobalErrorHandler`, `Missing global
  error handler` — these are the names of security queries being loaded, not results.
- **Script echo is not execution.** GitHub prints each step's source before running it, so a step
  whose script contains `::error::` shows that string even when it passed. The result line comes
  after, without the escape prefix.

### 4. Check what should be there

For every step in the run that prints an outcome, the matching line must be in the log. Take the
step list from `--json jobs`, not from `quality.yml` on disk: a run predating a step cannot have
printed its outcome, and comparing against today's workflow invents absences.

Known outcomes:

| Step | Expected line |
|---|---|
| Verify VSIX contains the WebView | `VSIX OK: <n> entries, all <n> WebView files present.` |
| Build (TypeGen, inside MSBuild) | `Files for project "bin\Release\netstandard2.0" generated successfully.` |
| Audit (advisory only) | `found 0 vulnerabilities` |
| Generated DTOs are up to date | `git diff --exit-code` producing no output |

**A step that ran without printing its outcome is a finding, even on a green run.** That is defect
2 above: `dotnet-typegen` failed, `ContinueOnError` hid it, and the step still reported success.

## Report

```
Run <id> — <branch> @<sha> — <conclusion>

Steps: <ok>/<total>

Expected checks
  ok       VSIX OK: 25 entries, all 9 WebView files present.
  ok       Files for project "bin\Release\netstandard2.0" generated successfully.
  ok       found 0 vulnerabilities
  ok       DTOs match the committed files
  MISSING  <what was expected, and which step should have printed it>

Anomalies
  <job> / <step>: <line>

Noise skipped: 147 VSTHRD/VSSDK, 7 ESLint, Node 20 + CodeQL v3 deprecations
```

Lead with anything `MISSING` or any anomaly — at the bottom of a report they get skimmed past, and
they are the reason this skill exists. If everything is present and nothing anomalous, say so in one
line rather than padding.

Report the noise as a count, never silently. "147 VSTHRD skipped" and "no warnings" are different
claims, and only one of them is true.

## Limits

- Reads finished runs only; does not wait for one in progress.
- No baseline, no comparison with previous runs. The question is whether *this* run did its job.
- Requires `gh` authenticated for this repo.

## Known-good references

Two runs to check behaviour against:

- **29838886808** — green, 2380 lines. All four checks present, no anomalies.
- **29826650583** — reports `success` and shipped the broken VSIX. Filtering leaves exactly two
  lines out of 2122:

  ```
  'dotnet-typegen' is not recognized as an internal or external command,
  ...Contracts.csproj(33,5): warning MSB3073: The command "dotnet-typegen generate" exited with code 9009.
  ```

  No `MISSING` entries for it: that run predates the verification steps. A filter that cleans up the
  green log but hides those two lines is worse than no filter.
