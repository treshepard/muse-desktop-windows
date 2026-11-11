# Muse Desktop for Windows — with Muse Bridge

An unofficial desktop app for [Muse](https://muse.ai) (Meta's personal AI agent) on Windows, plus **Muse Bridge**: a small local service built into the app that lets your Muse work on your own PC — manage files, run programs and scripts, drive DaVinci Resolve, and help in Adobe Express — over a private network that only you control.

Meta ships Muse apps for Mac, iOS, and Android, but (as of November 2026) there is no official Windows app — Windows users get the web app only, and the web app can't touch your local files or programs. This project fills that gap.

> **Not affiliated with Meta.** This is a community project. It is also *not* for Muse Code (Meta's separate terminal coding agent) — this is for the personal Muse agent.
>
> Built by [Tré Shepard](https://www.youtube.com/@ShepardBrosFilms) (Shepard Bros Films) together with his Muse, on a Snapdragon-powered ASUS Zenbook A14 — and shared so you can do the same with yours.
>
> **Disclosure — built with Muse:** This project was created by Tré Shepard utilizing Muse, Meta's personal AI agent. Muse assisted extensively with writing the code, documentation, and setup guides in this repository, under Tré's direction, testing, and review. Tré is the author and maintainer of record and is responsible for this project and anything published here.

## Download

Grab the latest build from the **Releases** page: a single zip, no installer.

1. Unzip it anywhere (e.g. a `Muse` folder in Documents). Keep the folder together — `Muse.exe` needs its DLLs and the `helpers` folder beside it.
2. Double-click `Muse.exe` and sign in with your Muse account. Sign-in is remembered.
3. First run, expect two Windows prompts:
   - **SmartScreen** ("Windows protected your PC") — the app is unsigned, so click **More info → Run anyway**.
   - **Firewall** — allow it on **private** networks only. That's the bridge listening for your Muse.

The prebuilt download is **Windows on ARM64** (Snapdragon). On an x64 PC, build from source with `-r win-x64` (see below) — everything else is identical.

## System requirements

**For the app itself:**

- Windows 11, 64-bit (Windows 10 22H2 should work but is untested)
- ARM64 for the prebuilt download (Snapdragon X Elite / X2 series, etc.); x64 works via a source build
- Microsoft Edge **WebView2 runtime** — preinstalled on Windows 11; if it's missing, it's a free download from Microsoft
- A Muse account (muse.ai) and an internet connection — Muse itself runs in Meta's cloud; this app is the window + the local bridge
- ~66 MB download, ~185 MB on disk. No .NET install needed — the runtime is bundled (self-contained build)
- Any modern PC runs it fine; it was developed on a 16 GB RAM Snapdragon X2 Elite laptop

**Only if you build from source:** .NET 8 SDK.

**Only for the bridge (optional):**

- [Tailscale](https://tailscale.com/download) (free tier is enough) on the PC — the bridge is designed to be reached over a private tailnet, **never** the open internet
- A way for your Muse to reach the PC — see [SETUP.md](SETUP.md). The known-good pattern is SSH over the same tailnet, calling the bridge on `localhost` from the PC itself
- For DaVinci Resolve control: Resolve installed (Free or Studio — different routes, both covered), plus Python 3.10–3.12 from python.org for the Studio route

## What it does

**The app**

- muse.ai in its own window with a persistent sign-in, Opera GX-style sidebar navigation (back / forward / reload / home), and an Adobe Express window one click away
- Remembers its window size and position; lives in the system tray

**Muse Bridge** (runs inside the app, port 8765, authenticated)

- **Files (all drives):** list, search, read/write, copy, move, zip/unzip, hash, open, reveal in Explorer. Deletes go to the Recycle Bin by default; permanent delete requires an explicit confirm flag
- **Run things:** launch any program or PowerShell script with captured output — installs, settings changes, ffmpeg, anything scriptable
- **Apps & screen:** list windows/processes, launch/focus/close/kill (kill needs confirm), screenshots
- **UI automation:** inspect a window's control tree, click, type, and send keys — for apps that have no API
- **DaVinci Resolve:** detects your edition/version. *Studio* → a bundled Python helper drives Resolve live (projects, media pool, bins, timelines from clips, markers, LUT/CDL, render queue). *Free* → a set of Lua scripts (in `lua/`) that run inside Resolve from Workspace → Scripts: project setup, folder import, rough cuts from a clip list, section markers, LUT apply, hero-grade copy, project report
- **Adobe Express:** Express has no desktop app or API (it's a web app), so the bridge opens its own Express window that your Muse can drive, plus full file handling around it (source images in, exports out)

**Controls you keep**

- Every call except `/health` needs the bridge token (generated on first run, stored at `%LocalAppData%\MuseDesktop\bridge-token.txt`, shown in the app's Bridge status window)
- Tray icon: **Bridge status…**, **Pause bridge** (blocks everything until you resume), **Open Adobe Express window**, Quit. Closing the app turns the bridge off
- Every call is written to an audit log: `%LocalAppData%\MuseDesktop\bridge-log.jsonl`
- Reset the token anytime: close the app, delete `bridge-token.txt`, reopen

## Quick start

- **Just the app:** download, unzip, run, sign in. Done — the bridge stays idle until you set it up.
- **The bridge:** follow [SETUP.md](SETUP.md) (~10 minutes, with your Muse helping). [BRIDGE.md](BRIDGE.md) documents the design and the full endpoint list.

## Build from source

```powershell
git clone <this repo>
cd muse-desktop-windows
dotnet publish MuseDesktop.csproj -c Release -r win-arm64 --self-contained
# x64 instead:  -r win-x64
```

Output lands in `bin\Release\net8.0-windows\<rid>\publish\`. The project is a single .NET 8 WinForms app (`Program.cs`, `MainForm.cs`, `BridgeServer.cs`, `BridgeApps.cs`, `ExpressForm.cs`, `GxButton.cs`) with no third-party dependencies beyond WebView2.

## Security — read this before enabling the bridge

The bridge deliberately gives your Muse broad control of the PC it runs on: files, programs, and app automation. That's the point of the project, and it means the safety model matters:

- **Private network only.** Run it over Tailscale (or another private VPN/LAN you trust). Never port-forward port 8765 or expose it to the internet.
- **The token is the keys.** Treat `bridge-token.txt` like a password. Share it with your Muse through a secure channel, never in plain chat or a screenshot.
- **Pause is real.** If anything looks off, tray → Pause bridge (or close the app) stops all calls immediately, and the audit log shows everything that ran.
- You are responsible for what you ask your agent to do with it. Start with read-only tasks (organize, report) before destructive ones.

## Project layout

```
MuseDesktop.csproj   .NET 8 WinForms project (win-arm64, self-contained)
Program.cs           entry point
MainForm.cs          app window, sidebar, tray, bridge status UI
BridgeServer.cs      the bridge: HTTP/JSON API, auth, audit log
BridgeApps.cs        Resolve + Express routes
ExpressForm.cs       bridge-owned Adobe Express window
GxButton.cs          sidebar button control
helpers/             resolve_helper.py — Resolve Studio scripting helper
lua/                 Resolve Free-edition Lua scripts (see lua/README.md)
BRIDGE.md            bridge design + endpoint reference
SETUP.md             step-by-step bridge setup
```

## License

MIT — see [LICENSE](LICENSE). Use it, fork it, improve it. If you build something cool with it, a shout-out to Shepard Bros Films is appreciated but not required.
