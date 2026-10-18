# Contributing

Use synthetic examples and report the application version, Windows version,
display scale, source application and expected/actual behavior. English and
Russian reports are welcome. Security issues belong in the private channel in
[SECURITY.md](SECURITY.md), not public issues.

## Build

Install Git, .NET SDK 10.0.103, Node.js 24.12.0 and Python 3.14.2 on Windows.
The project targets .NET Framework 4.7.2; the portable host requires Framework 4.8.
WebView2 Evergreen Runtime is required for web windows. No global OneScript,
OVM, opm packages or personal API key are required to build or run ordinary tests.

```powershell
./tools/ci.ps1 -VerifyReleaseDll
```

The checked-in private OneScript archive is checksum verified and unpacked only
under `artifacts`. Do not substitute the global runtime or change its installation.
Interactive tests require an unlocked desktop and are a separate mandatory
release check. See [TESTING.md](TESTING.md).

Keep changes focused and add behavioral tests. Preserve clipboard, source-focus,
cancellation and hotkey STA invariants. Do not turn skipped desktop tests into
success, weaken coverage thresholds, or silently repeat a paid AI request.
Do not include personal configuration, logs, WebView profiles or generated output.

Own contributions use the project's MIT license. Existing third-party code keeps
its notices and license. Do not relabel vendor code as MIT. A contribution does
not authorize the maintainer to publish your personal test data.
