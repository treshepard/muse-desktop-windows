# Muse Bridge — setup

Goal: your Muse, running in Meta's cloud, can work on this PC — files, programs, DaVinci Resolve, Adobe Express — over a private network. Nothing is exposed to the public internet. ~10 minutes, done together with your Muse in chat.

## Step 1 — Install Tailscale on the PC

- Download from https://tailscale.com/download/windows (pick the ARM64 build if offered; the regular build also works).
- Install, sign in, and leave it running (icon by the clock).

## Step 2 — Connect your Muse's machine to your tailnet

Muse's workspace needs to join the same tailnet. How that works depends on your Muse setup — ask your Muse to join and approve its device in the Tailscale admin console (or via the approval link it sends you). One click.

Then confirm the PC is reachable: open **Bridge status** (double-click the Muse tray icon). It lists the bridge addresses — you want to see a `100.x.x.x` Tailscale address, not only `127.0.0.1`. If only `127.0.0.1` shows, Tailscale isn't connected yet; recheck Step 1.

## Step 3 — Hand over the token, the safe way

- The token is in Bridge status (**Copy token** button). It unlocks the bridge, so **never paste it into plain chat, a screenshot, or a public note**.
- The pattern that works reliably: have your Muse connect to the PC over **SSH through the same tailnet** (Windows' built-in OpenSSH Server + an authorized key), and let it read the token from `%LocalAppData%\MuseDesktop\bridge-token.txt` *on the PC itself*, calling the bridge at `http://127.0.0.1:8765`. The token never travels.
- Why not just send the token and call the bridge over the tailnet directly? That can work, but some secure-credential proxies fail to substitute stored bearer tokens on tunneled requests — the bridge then reports a "surrogate/placeholder received" auth failure in its log even though the token you stored was right. If you hit repeated `auth-fail` entries in `bridge-log.jsonl` with a fingerprint mismatch, that's this issue — switch to the SSH route above rather than re-sending the token.
- Firewall, if Windows asks or the bridge is unreachable: allow `Muse.exe` on private networks, or allow port 8765 from the Tailscale range `100.64.0.0/10` only.

## Step 4 — DaVinci Resolve (optional)

First, one fact decides the route: your Resolve edition + version (Resolve → Help → About).

- **Studio:** Preferences → System → General → *External scripting using* = **Local**, then restart Resolve. Install Python 3.10–3.12 from python.org (not the Microsoft Store stub). The bridge's bundled `helpers/resolve_helper.py` then drives Resolve live: projects, media import, bins, timelines from clips, markers, LUT/CDL apply, render queue.
  - API ceiling (Blackmagic's, not ours): no scripted primary grades/curves/qualifiers, no trimming/moving clips already on a timeline, no speed ramps. Building timelines and applying looks are in.
- **Free:** external scripting is blocked (since 19.1), so use the in-app route: copy the scripts from this repo's `lua/` folder into
  `%APPDATA%\Blackmagic Design\DaVinci Resolve\Support\Fusion\Scripts\Utility`
  and they appear under **Workspace → Scripts** inside Resolve. They cover project setup (bins), folder import, rough cuts from a clip list, section markers, LUT apply, the hero-grade pattern (you grade one clip by hand; the script copies that grade to the rest), and a project report your Muse can read back. Your Muse edits the list/path at the top of each script for each job. See `lua/README.md`.

## Step 5 — Adobe Express

Honest version: there is no Express desktop app with controls to plug into — Adobe ships Express as a web app, with no API. So the bridge gives your Muse three routes, in order of reliability:

1. The bridge's own Express window (tray icon → *Open Adobe Express window*). Sign in there once. Because it's the bridge's window, your Muse can click its menus, type, and handle uploads.
2. Launching your installed Express PWA / Edge app window and driving it with Windows UI automation + screenshots.
3. Full file control around it: source images in, downloads and exports out.

Opening projects, uploading assets, text edits, and exporting are the reliable parts. Dragging canvas elements pixel-perfect is screenshot-checked work — expect your Muse to verify visually as it goes.

## Safety controls you keep

- **Pause or close:** tray icon → *Pause bridge*, or just close Muse. The bridge only runs while the app runs.
- **Audit log:** every call is appended to `%LocalAppData%\MuseDesktop\bridge-log.jsonl`.
- **Token reset:** close the app, delete `bridge-token.txt` in that folder, reopen — a fresh token is generated (your Muse will need the new one).
- **Destructive calls** (permanent delete, kill process) require an explicit `"confirm": true`; normal file removal goes to the Recycle Bin.

## Troubleshooting

- **Blank window:** check your internet connection, close and reopen the app.
- **Bridge not reachable:** Bridge status should say *running*; check Tailscale is connected on the PC.
- **Auth failures in the log:** see Step 3 — use the SSH route; don't keep re-sending the token.
