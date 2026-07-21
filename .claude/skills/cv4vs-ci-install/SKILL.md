---
name: cv4vs-ci-install
description: Download the VSIX built by the last CI run of this branch and install it into the Visual Studio experimental hives, checking the package is complete first. Use when asked to install the CI build, the artifact, the last build from GitHub, or to test what CI produced. Installs into the Exp hives only.
---

# Install the CI artifact

Testing what CI actually produced is four manual steps: find the run, download the artifact, open
the zip to see whether it is intact, then hand it to `tools\extension.ps1`. This does the sequence
and refuses to install a package that is missing pieces.

Why the check matters: on 2026-07-21 the CI artifact was missing the entire `WebView2/` folder. It
installed without a word of complaint, and the failure only surfaced as a
`DirectoryNotFoundException` when the chat pane was opened. A VSIX that installs is not a VSIX that
works.

## Procedure

### 1. Find the run

```powershell
gh run list --branch <branch> --limit 1 --json databaseId,headSha,status,conclusion
```

Same rules as [cv4vs-ci-log](../cv4vs-ci-log/SKILL.md): the run must be `completed`, and `headSha`
has to be compared with `git rev-parse HEAD`. Two extra conditions here, because this one changes
the machine:

- **`conclusion` must be `success`.** A failed run may still have uploaded an artifact.
- **If `headSha` differs from HEAD, stop and say so.** With a report a stale run is merely
  confusing; installing one puts the wrong commit on disk under the right name, which is exactly
  the kind of mismatch that costs an afternoon.

### 2. Download

```powershell
$dir = "C:\temp\cv4vs-ci-vsix"
Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
gh run download <id> --repo Corsinvest/cv4vs-agents --dir $dir
```

Clear the folder first: `gh run download` does not overwrite, and a leftover from an earlier run
would be installed instead.

### 3. Check the package before installing

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
$vsix = Get-ChildItem $dir -Recurse -Filter *.vsix | Select-Object -First 1
$zip = [IO.Compression.ZipFile]::OpenRead($vsix.FullName)
$entries = $zip.Entries.FullName
$zip.Dispose()
$entries | Where-Object { $_ -like 'WebView2/*' } | Sort-Object
```

Refuse to install unless the package has:

- `WebView2/index.html` — without it the chat pane throws on open
- `WebView2/bundle.js` and `bundle.css`
- `WebView2/images/plugin-logo.png` — the welcome screen renders a broken image without it
- no `tgconfig.json` at the root; it is a codegen input that has no business shipping

For reference, a sound package has 25 entries and 9 files under `WebView2/`. Report the count either
way — a number that has moved is worth knowing even when nothing is missing.

If something is missing, say which files and stop. Do not install and mention it afterwards.

### 4. Install

```powershell
.\tools\extension.ps1 -Reinstall -Path <path to the .vsix>
```

The script prints version, packaging date and WebView file count before installing, refuses to run
while `devenv` is up, and removes every existing copy first — VS keys extensions by `Identity Id`,
so leftovers install alongside rather than being replaced.

`-Reinstall`, not `-Install`: the point is to test this artifact, and an older copy left in the hive
would keep loading.

Report what landed, including the packaging date, so it is clear which build is now installed.
Read that date as relative: zip stores no time zone, so a CI artifact carries the runner's UTC while
a local build carries local time.

## Limits

- Experimental hives only. `-Normal` exists on the script but installing a test build into the IDE
  you work in is a separate decision — ask first.
- Does not start Visual Studio; it must be closed for the install to work anyway.
- Does not verify the extension behaves once loaded. Nothing outside VS can see the tool windows —
  that check is manual.
