# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

Chrome Automation Bridge: a three-component system that lets a .NET 8 program drive Chrome via a WebSocket bridge to a Manifest V3 extension. The primary application built on top is `ChromeAutomation.CpmsExport`, which automates exporting a report from CPMS (`cpms.hq.cmcc`) and importing the resulting Excel into a separate .NET project at `D:\NET` (configurable via `NET_IMPORT_PROJECT`).

## Common commands

All `dotnet` commands run from `src/`:

```powershell
# 1. Start bridge server (long-running, listens on ws://127.0.0.1:9333)
dotnet run --project ChromeAutomation.Bridge

# 2. Run example/smoke test against a connected extension
dotnet run --project ChromeAutomation.Example

# 3. Run the full CPMS export + DB import workflow
dotnet run --project ChromeAutomation.CpmsExport

# Build whole solution
dotnet build ChromeAutomation.sln
```

End-to-end launcher (Windows, from repo root): `./run-cpms-export.ps1` — starts the bridge if port 9333 isn't already listening, then runs `CpmsExport`. `schedule-cpms-export.ps1` is the unattended variant (sets `CPMS_UNATTENDED=1`) for Task Scheduler.

There is no test suite and no linter configured.

## Architecture

Three processes communicate over a single WebSocket port (`9333` by default, overridable via `BRIDGE_PORT`):

```
C# controller  ⇄  Bridge server  ⇄  Chrome extension (service worker)
   (Client)         (Bridge)            (extension/background.js)
```

- **`ChromeAutomation.Bridge`** (`src/ChromeAutomation.Bridge/`) — ASP.NET Core WebSocket host. Holds exactly one extension socket and any number of controller sockets. The extension self-identifies with `{type:"register", role:"extension"}`. Controller messages with `{id, action, params}` are forwarded verbatim to the extension; responses (matched by `id`) are routed back via a `ConcurrentDictionary<string, PendingRequest>`. `BridgeHost.GetRequestTimeoutSeconds` bumps the per-request timeout to 300s for any action whose name contains `Download` or starts with `cpmsDownload`.

- **`ChromeAutomation.Client`** (`src/ChromeAutomation.Client/`) — thin C# SDK. `ChromeController` opens a `ClientWebSocket`, serializes camelCase JSON, and waits for the response whose `id` matches. `CommandAsync` always injects `timeoutMs` into `params` so the bridge knows how long to wait. Convenience wrappers (`NavigateAsync`, `ClickAsync`, …) exist for the common actions; `ChromeAutomationHelpers` adds extension methods (`ClickByTextAsync`, `WaitForTextAsync`, `ExtractSerialNumberAsync`).

- **`extension/`** — Manifest V3 extension. `background.js` is the service worker that receives bridge messages and dispatches via a giant `switch` in `executeAction` to one of: tab management, `chrome.scripting.executeScript` into `MAIN` world (`runInTab`), `chrome.downloads`-backed download helpers, or CPMS-specific DOM probes (`cpms*` actions). `evaluate` is intentionally disabled (CSP); callers must use a named action instead. An offscreen document (`offscreen.html`) is created on demand to keep the service worker alive during long CPMS waits. The popup (`extension/popup/`) lets the user set the bridge URL and shows connection status; the extension auto-reconnects every 3s and pings every 15s.

- **`ChromeAutomation.CpmsExport`** — the real workflow. `Program.cs` is the orchestrator (`RunAsync` for the 7-step export-then-import flow, `RunDownloadOnlyAsync` for the skip-export variant). `CpmsWorkflow.cs` contains all the CPMS DOM heuristics: it identifies the report page via URL fragments + body text markers, drives the export, polls the download list for the resulting serial number until backend processing finishes, then waits for the file to land in `~/Downloads`, unzips it if needed, and invokes a separate import project at `D:\NET` (resolved from env var `NET_IMPORT_PROJECT`).

### Wire protocol

Request: `{ "id": "<uuid>", "action": "<name>", "params": { ... , "timeoutMs": <ms> } }`
Response: `{ "id": "<uuid>", "success": true|false, "data": <any>, "error": <string|null> }`

Action names are documented in `README.md`. The extension's `runInTab` executes a single `contentAction` function in **every frame** of the target tab and returns the first non-error result; selectors traverse open shadow roots. Tabs are addressed by numeric `tabId`; if a stale `tabId` is provided with `recreateUrl`, the extension opens a new tab rather than failing — this is intentional so Chrome is never closed mid-workflow.

## CPMS workflow specifics

When changing `CpmsExport` behavior, keep these invariants — they reflect deliberate operational choices, not accidents:

- **Never close Chrome or the user's tabs.** `GetOrNavigateTabAsync` reuses an existing CPMS tab (or the active tab) by default; only set `CPMS_NEW_TAB=1` to force a new tab. The launcher script and the workflow both advertise "Chrome stays open" — preserve that contract.
- **Tab reuse is keyed on URL substring `cpms.hq.cmcc`**, not on tab title.
- **Report page detection** combines URL match (`BigTableDefind` / `wideTable`), absence of `attachmentDownload`/`mops/tools`, presence of the "导出" button (preferred) or strong body markers ("项目明细查询报表", "显示列配置", "字段说明"), and a negative check for the login page.
- **Serial number** is parsed from the export confirmation dialog via regex `流水号[为：:\s]*(\d+)`. If parsing fails the workflow falls back to "latest row in the download list."
- **Download resolution**: `cpmsDownloadBySerial` resolves URL via `cpmsResolveDownloadUrl` (DOM + list API), then `downloadWithSessionCookies` (fetch + blob URL). Sniff-on-click (`cpmsSniffDownloadUrlOnClick`) is last resort. Do **not** rely on `acceptDanger` (unavailable in MV3 service worker). See `docs/download-troubleshooting.md`.
- **Long-running downloads need the offscreen keepalive.** Any new action that may take >30s should either match the `Download`/`cpms*` naming convention (so the bridge auto-bumps timeout to 300s) or pass an explicit `timeoutMs`. The extension's `executeAction` also uses the same naming convention to decide when to spin up the offscreen document.

## Environment variables

| Variable | Purpose |
|---|---|
| `BRIDGE_PORT` | Bridge listen port (default `9333`). |
| `CPMS_EXPORT_TASK_URL` | Override CPMS download-list URL. |
| `CPMS_SKIP_EXPORT=1` | Skip export step; download existing latest task only. |
| `CPMS_SERIAL` | Use a specific serial number instead of detecting one. |
| `EXCEL_PATH` | Skip Chrome entirely; just run DB import on this xlsx (or path that resolves to one). |
| `CPMS_NEW_TAB=1` | Force a new tab rather than reusing an existing CPMS tab. |
| `CPMS_UNATTENDED=1` | Suppress `Read-Host` prompts in launcher (Task Scheduler). |
| `CPMS_USE_KEEP_CLICKER=1` | Start `click-chrome-keep.ps1` alongside (rarely needed). |
| `NET_IMPORT_PROJECT` | Path to the downstream import csproj (default `D:\NET`). |

## Extension reload caveat

After editing anything under `extension/`, the user must reload the unpacked extension at `chrome://extensions/`. The README and launcher both call this out — surface it in any error message that suggests the extension is stale (e.g., "Chrome 扩展未连接" / "Unknown action").
