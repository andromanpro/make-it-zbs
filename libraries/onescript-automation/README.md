# OneScript.Automation

Independent Windows automation module, `0.1.0-beta.1`, MIT, .NET Framework 4.7.2.
Tested host: MakeItZBS.OneScript `1.9.4-zbs.1`. Not a 1C NativeAPI component or a
promise of OneScript 2.x compatibility. No installed Make it ZBS, tray, settings,
WebView, VS Code extension, global OVM libraries or provider key is required.

## API

Load `OneScript.Automation.dll` with `AttachAddIn` / `ПодключитьВнешнююКомпоненту`.
Keep all worker EXEs, configs and dependency DLLs beside it. Do not substitute
these files into a running application's directory. Do not load this module and
`ZBSWindowsComponent.dll` together: their linked legacy context aliases overlap.
`OneScript.Windows` (clipboard/foreground window) is a separate optional module.

- `КлиентыАвтоматизации / AutomationClients`: `CaptureWindow(absolutePngPath,
  backend="auto")`, `StartCodex(instruction, input, model="", reasoning="",
  imagePath="", timeoutSeconds=180)`, `StartOpenRouter(url, body, authorization,
  timeoutSeconds=180)`, `ResolveChatProfile(executable, title)`.
- `ЗаданиеАвтоматизации / AutomationJob`: `IsCompleted`, `IsCancelled`,
  `ResultJson()`, `Cancel()`, `Close()`. One job at a time, no hidden queue or
  retry. Poll from the script's own thread. A cancelled job cannot return a late
  result. Always close it in cleanup. Close cancels and waits for owned cleanup.
- `КлавишиАвтоматизации / AutomationHotkeys`: `Listen(key)`, `TakePressed()`,
  `SetBusy(bool)`, `Close()`. One replaceable global combination, one pending
  event, MOD_NOREPEAT, hidden HWND on its own background STA. No script callback
  on a foreign thread. A failed replacement preserves the previous registration.

Names above use English method aliases; Russian aliases are in the source.
Examples are in [examples](examples/capture-window.os).

## Capture Contract

Only the foreground window at the time of the call is captured. The owned
worker checks HWND/PID, focus, bounds and minimization before and after capture.
Output is PNG; the response JSON reports backend, bounds, DPI, capture time,
elapsed time and fallback reason. `auto` tries DXGI and falls back to GDI on
unsupported capture or worker timeout; `dxgi` and `gdi` force that backend.
No result is published after a failed source check. A cancelled/failed job does
not make an already published file disappear: publication and later cancellation
are distinct events. Save into a directory controlled by the current user.

This is a visible desktop capture, not an off-screen rendering API. Other windows
overlapping the target may appear in pixels. Protected, minimized, locked, RDP
and multi-monitor desktops need separate validation; do not promise their support.
Temporary capture files currently share the per-user `MakeItZBS/Temp` namespace
of the linked core, but no installed application or private config is loaded.

## AI Contract

Codex runs in a private temporary working directory, with prompt on stdin and
arguments quoted individually. `--image` is supported for reference images;
model/reasoning are explicit. Existing user CLI login and its limits still apply.
No automatic login, key discovery, paid request, model selection or image generation
occurs on module load. `CODEX_BIN` may select a trusted executable; treat that
environment variable as code execution configuration.

OpenRouter accepts HTTPS requests only to `openrouter.ai`; the caller explicitly
supplies JSON and authorization. It returns `statusCode` and response `body`;
non-2xx HTTP status is not an automatic success. The module does not persist keys.
Do not log request/response content or raw authorization in your application.

Codex, Claude and OpenCode **profile recognition** is exported. UI chat reading,
draft insertion and sending are not exported here; detecting a process/title is
not proof of its identity or permission to send. Claude/OpenCode headless model
execution is not implemented. The existing application retains those UI routes.

## Build And Verify

From the repository: `python libraries/onescript-automation/build.py` builds
binary/source ZIPs with SHA-256 and per-file manifests. It rebuilds a materialized
source tree outside Git and compares every shipped binary. `--allow-dirty` is
for inspection only, never a public release.

The source ZIP includes its workers, locked NuGet metadata, SDK pin, engine
source/license and dependency notices. Install .NET SDK `10.0.103`, set
`DOTNET_CLI_UI_LANGUAGE=ru-RU` and `DOTNET_ROLL_FORWARD=Disable`, then run `dotnet build -c Release
OneScript.Automation.csproj -p:RestoreLockedMode=true`. NuGet access/cache is
required for dependencies not included in the local `vendor/nuget` feed.

`python libraries/onescript-automation/run-tests.py --coverage` uses an isolated
deployment, fixture CLI and synthetic files. `--desktop` is a separate explicit
opt-in requiring an idle, unlocked interactive desktop. Tests never use real
AI conversations or paid provider requests. Dependency notices are retained in
the archives; the host supplies its own compatible OneScript runtime.
