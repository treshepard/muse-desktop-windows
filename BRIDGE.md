# Muse Bridge — design

A small HTTP/JSON API that lives **inside** the Muse desktop app and lets your Muse work on the PC the app runs on: manage all files, run programs, drive DaVinci Resolve, and work in Adobe Express.

## Architecture

- The bridge is part of `Muse.exe` — a raw `TcpListener` HTTP/JSON API on **port 8765** (no HttpListener URL-ACL admin hassle). It starts with the app and is controlled from the tray icon: status, pause/resume, open log, quit.
- **Auth:** every call except `/health` requires `Authorization: Bearer <token>`. The token is generated on first run (256-bit random) and stored in `%LocalAppData%\MuseDesktop\bridge-token.txt`; the app's Bridge status window shows it with copy buttons.
- **Audit:** every call is appended to `bridge-log.jsonl` in the same folder, including auth failures with a machine-readable reason and SHA-256 token *fingerprints* (never the token itself), so you can diagnose a bad-token problem without exposing the secret.
- **Pause:** when paused, everything except `/health` returns `423`.
- **Destructive calls** (permanent delete, kill process) require `"confirm": true` in the request body. Default file removal is the Recycle Bin.

## How your Muse reaches it

The intended network is a **private Tailscale tailnet** — no public internet exposure, no port forwarding.

The reliable calling pattern (learned the hard way): connect to the PC over **SSH through the tailnet**, read the token from its file *on the PC*, and call `http://127.0.0.1:8765` locally. Some credential proxies fail to substitute stored bearer tokens on tunneled HTTP requests, which shows up in the log as a `surrogate_received` auth failure; the SSH route makes the token never leave the machine. See [SETUP.md](SETUP.md).

## Capability surface

All endpoints are `POST` with a JSON body (except `/health` and `/info`, which accept `GET`). Highlights:

**Status**

- `/health` — no auth; liveness + paused flag
- `/info` — app/bridge version, machine info, Resolve detection

**Files (all drives)**

- `/files/list`, `/files/stat`, `/files/search` — browse and find
- `/files/read`, `/files/write` — text or base64 (25 MB cap)
- `/files/mkdir`, `/files/copy`, `/files/move`, `/files/zip`, `/files/unzip`
- `/files/trash` (Recycle Bin), `/files/delete` (permanent, needs `confirm`)
- `/files/hash` (SHA-256), `/files/open`, `/files/reveal` (Explorer)

**Exec**

- `/exec/run` — any program with args; stdout/stderr captured, timeout, working dir
- `/exec/powershell` — a PowerShell script block
- This is the universal fallback: winget installs, Windows settings, ffmpeg, LM Studio's server, etc.

**Apps, windows, screen**

- `/apps/list`, `/apps/launch`, `/apps/focus`, `/apps/close`, `/apps/kill` (kill needs `confirm`)
- `/screen/screenshot` — PNG of the desktop

**UI automation** (for apps with no API)

- `/ui/windows`, `/ui/tree` — window list and UIA control-tree snapshots
- `/ui/click`, `/ui/type`, `/ui/key` — invoke/set-value patterns, or coordinates and SendKeys

**DaVinci Resolve**

- `/resolve/detect` — install path, edition, version from `Resolve.exe`
- `/resolve/launch`
- `/resolve/python` — runs the bundled `helpers/resolve_helper.py` (Studio external-scripting route): projects, media pool import/bins, timelines create/append clips, markers, LUT/CDL apply, render queue add/start/status
- **Free edition:** external scripting is blocked by Blackmagic, so the bridge instead prepares Lua scripts that run inside Resolve (Workspace → Scripts) — see `lua/`

**Adobe Express**

- Express has no desktop app or API (web/PWA only, Adobe-confirmed), so:
- `/express/launch` — opens the bridge-owned WebView2 Express window
- `/express/detect`, `/express/eval` — inspect and script that window
- plus `/apps/*` UI automation against an installed Express PWA, and full file handling around it

## Known limits

- Resolve's scripting API (any edition) cannot build primary grades/curves/qualifiers, trim or move clips already on a timeline, or add speed ramps. The workflow that works: your Muse assembles timelines and applies looks; grades spread via the **hero-grade pattern** (a human grades one clip; the script copies it).
- The bridge can only act while the app is running and unpaused — by design.
