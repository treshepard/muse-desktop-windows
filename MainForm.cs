using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MuseDesktop;

/// <summary>
/// A single window that hosts muse.ai, the same approach as the official
/// Mac app (the web app inside a native window). Login cookies persist in
/// the user's LocalAppData so sign-in survives restarts.
/// </summary>
public sealed class MainForm : Form
{
    private const string HomeUrl = "https://muse.ai";

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    private readonly string _dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MuseDesktop");

    private string SettingsPath => Path.Combine(_dataDir, "window.json");

    private BridgeServer? _bridge;
    private NotifyIcon? _tray;
    private ExpressForm? _expressForm;
    private Button _btnBack = null!;
    private Button _btnForward = null!;
    private Label _urlLabel = null!;

    public MainForm()
    {
        Text = "Muse";
        Width = 1240;
        Height = 820;
        MinimumSize = new Size(480, 360);
        Controls.Add(_web);

        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        BuildNavBar();
        StartBridge();
        SetupTray();

        RestoreSavedBounds();

        Load += OnLoad;
        FormClosing += OnClosing;
    }

    // ---------- Navigation (v2.3, Opera GX style) ----------
    // Following a link used to strand the user on the target page with no way
    // back to chat short of restarting the app. The GX-style sidebar +
    // Alt+Left / Alt+Right fix that.

    private void BuildNavBar()
    {
        // v2.3 — Opera GX style: the horizontal bar is gone.
        // GX's signature is a slim LEFT SIDEBAR of icon buttons on
        // near-black, neon-red accents, angular chamfered buttons
        // (GxButton), plus a dark top strip with an omnibox-style
        // address pill. Icons are Segoe MDL2 Assets glyphs, which
        // Windows renders at exact size.
        var iconFont = new Font("Segoe MDL2 Assets", 15f);
        var tips = new ToolTip();

        GxButton SideButton(string glyph, string tip, Action onClick)
        {
            var btn = new GxButton
            {
                Text = glyph,
                Size = new Size(46, 46),
                Font = iconFont,
            };
            tips.SetToolTip(btn, tip);
            btn.Click += (_, _) => onClick();
            return btn;
        }

        // --- Sidebar (docked first: full height on the left) ---
        var sidebar = new Panel
        {
            Dock = DockStyle.Left,
            Width = 60,
            BackColor = Color.FromArgb(11, 11, 18),
        };
        var accent = new Panel
        {
            Dock = DockStyle.Right,
            Width = 2,
            BackColor = GxButton.GxRed,
        };
        sidebar.Controls.Add(accent);

        var topGroup = new Panel { Dock = DockStyle.Top, Height = 238, BackColor = Color.Transparent };
        _btnBack = SideButton("\uE72B", "Back (Alt+Left)", () =>
        {
            if (_web.CanGoBack) _web.GoBack();
        });
        _btnBack.Location = new Point(6, 12);
        _btnForward = SideButton("\uE72A", "Forward (Alt+Right)", () =>
        {
            if (_web.CanGoForward) _web.GoForward();
        });
        _btnForward.Location = new Point(6, 66);
        var btnReload = SideButton("\uE72C", "Reload (F5)", () => _web.Reload());
        btnReload.Location = new Point(6, 120);
        var btnHome = SideButton("\uE80F", "Back to Muse chat (Alt+Home)", () =>
        {
            _web.Source = new Uri(HomeUrl);
        });
        btnHome.Location = new Point(6, 174);
        topGroup.Controls.Add(_btnBack);
        topGroup.Controls.Add(_btnForward);
        topGroup.Controls.Add(btnReload);
        topGroup.Controls.Add(btnHome);
        sidebar.Controls.Add(topGroup);

        var bottomGroup = new Panel { Dock = DockStyle.Bottom, Height = 116, BackColor = Color.Transparent };
        var btnExpress = SideButton("\uE8B9", "Adobe Express window", () => ShowExpressWindow());
        btnExpress.Location = new Point(6, 6);
        var btnBridge = SideButton("\uE713", "Bridge status", () => ShowBridgeStatus());
        btnBridge.Location = new Point(6, 60);
        bottomGroup.Controls.Add(btnExpress);
        bottomGroup.Controls.Add(btnBridge);
        sidebar.Controls.Add(bottomGroup);

        // --- Top strip with the address pill ---
        var topBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 46,
            BackColor = Color.FromArgb(15, 15, 23),
        };
        var wordmark = new Label
        {
            Text = "MUSE",
            Location = new Point(14, 13),
            AutoSize = true,
            ForeColor = GxButton.GxRed,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold | FontStyle.Italic),
        };
        topBar.Controls.Add(wordmark);

        var pill = new Panel
        {
            Location = new Point(92, 8),
            Height = 30,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            BackColor = Color.FromArgb(26, 26, 40),
        };
        pill.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(58, 58, 84));
            e.Graphics.DrawRectangle(pen, 0, 0, pill.Width - 1, pill.Height - 1);
            using var accentBrush = new SolidBrush(GxButton.GxRed);
            e.Graphics.FillRectangle(accentBrush, 0, 0, 3, pill.Height);
        };
        _urlLabel = new Label
        {
            Location = new Point(14, 6),
            AutoSize = false,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            ForeColor = Color.FromArgb(198, 198, 216),
            Font = new Font("Segoe UI", 9.5f),
            Text = HomeUrl,
        };
        pill.Controls.Add(_urlLabel);
        topBar.Controls.Add(pill);
        topBar.Resize += (_, _) =>
        {
            pill.Width = Math.Max(120, topBar.Width - 92 - 14);
            if (_urlLabel is not null) _urlLabel.Width = Math.Max(80, pill.Width - 24);
        };

        // Dock order matters: add the top strip first, then the sidebar,
        // so the sidebar claims the full-height left edge (GX layout)
        // and the strip fills the top of what remains.
        Controls.Add(topBar);
        Controls.Add(sidebar);
        UpdateNavState();
    }

    private void UpdateNavState()
    {
        if (_btnBack is null) return;
        _btnBack.Enabled = _web.CanGoBack;
        _btnForward.Enabled = _web.CanGoForward;
        _btnBack.ForeColor = _btnBack.Enabled ? Color.White : Color.FromArgb(120, 118, 160);
        _btnForward.ForeColor = _btnForward.Enabled ? Color.White : Color.FromArgb(120, 118, 160);
        try { _urlLabel.Text = _web.Source?.ToString() ?? HomeUrl; } catch { }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        try
        {
            if (keyData == (Keys.Alt | Keys.Left))
            {
                if (_web.CanGoBack) _web.GoBack();
                return true;
            }
            if (keyData == (Keys.Alt | Keys.Right))
            {
                if (_web.CanGoForward) _web.GoForward();
                return true;
            }
            if (keyData == (Keys.Alt | Keys.Home))
            {
                _web.Source = new Uri(HomeUrl);
                return true;
            }
            if (keyData == Keys.F5)
            {
                _web.Reload();
                return true;
            }
        }
        catch { /* navigation shortcuts are a nicety */ }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private async void OnLoad(object? sender, EventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_dataDir);
            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: Path.Combine(_dataDir, "WebView2"));
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;

            // Links that would open a new window stay inside the app instead.
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (!string.IsNullOrEmpty(args.Uri))
                {
                    core.Navigate(args.Uri);
                }
            };

            // Keep the nav bar's back/forward state and URL current.
            core.NavigationStarting += (_, _) => UpdateNavState();
            core.NavigationCompleted += (_, _) => UpdateNavState();
            core.SourceChanged += (_, _) => UpdateNavState();
            core.HistoryChanged += (_, _) => UpdateNavState();

            _web.Source = new Uri(HomeUrl);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Muse needs the Microsoft Edge WebView2 Runtime. It ships with " +
                "Windows 11, so if you see this, install it from " +
                "https://developer.microsoft.com/microsoft-edge/webview2/ " +
                "and try again.\n\nDetails: " + ex.Message,
                "Muse",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    // ---------- Muse Bridge (v2) ----------

    private void StartBridge()
    {
        try
        {
            Directory.CreateDirectory(_dataDir);
            _bridge = new BridgeServer(_dataDir);
            BridgeServer.UiThreadInvoke = action => BeginInvoke(action);
            _bridge.Start();

            BridgeApps.ExpressOpenWindow = () => BeginInvoke(new Action(ShowExpressWindow));
            BridgeApps.ExpressEvalJs = script =>
            {
                var tcs = new TaskCompletionSource<string>();
                BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        ShowExpressWindow();
                        tcs.TrySetResult(await _expressForm!.EvalAsync(script));
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetResult("{\"error\":" + JsonSerializer.Serialize(ex.Message) + "}");
                    }
                }));
                return tcs.Task;
            };
            BridgeApps.ExpressWindowInfo = () =>
                _expressForm?.Info() ?? new { ready = false, note = "express window not opened yet" };
        }
        catch (Exception ex)
        {
            // The chat window matters more than the bridge; report, don't die.
            LastBridgeError = ex.Message;
        }
    }

    private string? LastBridgeError { get; set; }

    private void ShowExpressWindow()
    {
        _expressForm ??= new ExpressForm(_dataDir);
        if (!_expressForm.Visible)
        {
            _expressForm.Show();
        }
        _expressForm.Activate();
    }

    private void SetupTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Bridge status…", null, (_, _) => ShowBridgeStatus());
        menu.Items.Add("Open Adobe Express window", null, (_, _) => ShowExpressWindow());
        menu.Items.Add(new ToolStripSeparator());
        var pauseItem = new ToolStripMenuItem("Pause bridge");
        pauseItem.Click += (_, _) =>
        {
            if (_bridge is null) return;
            _bridge.Paused = !_bridge.Paused;
            pauseItem.Text = _bridge.Paused ? "Resume bridge" : "Pause bridge";
            UpdateTrayText();
        };
        menu.Items.Add(pauseItem);
        menu.Items.Add("Quit Muse", null, (_, _) => Close());

        _tray = new NotifyIcon
        {
            Icon = Icon,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowBridgeStatus();
        UpdateTrayText();
    }

    private void UpdateTrayText()
    {
        if (_tray is null) return;
        var state = _bridge is null ? "off"
            : _bridge.Paused ? "paused"
            : _bridge.Running ? $"on :{BridgeServer.Port}" : "error";
        _tray.Text = "Muse — bridge " + state;
    }

    private void ShowBridgeStatus()
    {
        var addresses = new List<string> { $"http://127.0.0.1:{BridgeServer.Port}" };
        if (_bridge is not null)
        {
            // Reuse the bridge's own /info data via a fresh server probe is
            // overkill; read tailnet addresses the same way BridgeServer does.
        }
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    var b = addr.Address.GetAddressBytes();
                    if (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                    {
                        addresses.Add($"http://{addr.Address}:{BridgeServer.Port}");
                    }
                }
            }
        }
        catch { }

        var form = new Form
        {
            Text = "Muse Bridge",
            Width = 560,
            Height = 430,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
        };
        var label = new Label
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            Text =
                "Bridge: " + (_bridge?.Running == true ? "running" : "NOT running") +
                (_bridge?.Paused == true ? " (paused)" : "") + "\n" +
                (LastBridgeError is not null ? "Error: " + LastBridgeError + "\n" : "") +
                (_bridge?.LastError is not null ? "Error: " + _bridge.LastError + "\n" : "") +
                "\nAddresses (give Muse the Tailscale one):\n" +
                string.Join("\n", addresses) +
                "\n\nToken (keep private — it unlocks this PC's bridge):\n" +
                (_bridge?.Token ?? "(no bridge)") +
                "\nToken fingerprint (safe to share): " +
                (_bridge?.ExpectedTokenFingerprint ?? "-") +
                "\n\nLast rejected call: " +
                (_bridge is null || _bridge.AuthFailureCount == 0
                    ? "none since this app started"
                    : _bridge.LastAuthFailureAt + " — reason: " + _bridge.LastAuthFailureCategory +
                      ", received fingerprint: " +
                      (string.IsNullOrEmpty(_bridge.LastAuthFailureReceivedFingerprint)
                          ? "(none)" : _bridge.LastAuthFailureReceivedFingerprint) +
                      " (" + _bridge.AuthFailureCount + " total)") +
                "\nReasons: no_token = no credential arrived; surrogate_received = " +
                "Muse's placeholder was not swapped for the real token on the way; " +
                "token_mismatch = a different token arrived." +
                "\n\nYour Muse reaches the bridge over your private Tailscale network.\n" +
                "Log: " + Path.Combine(_dataDir, "bridge-log.jsonl"),
        };
        var copyToken = new Button { Text = "Copy token", Dock = DockStyle.Bottom, Height = 34 };
        copyToken.Click += (_, _) =>
        {
            if (_bridge is not null) Clipboard.SetText(_bridge.Token);
        };
        var copyAddr = new Button { Text = "Copy Tailscale address", Dock = DockStyle.Bottom, Height = 34 };
        copyAddr.Click += (_, _) =>
        {
            var ts = addresses.FirstOrDefault(a => !a.Contains("127.0.0.1"));
            Clipboard.SetText(ts ?? addresses[0]);
        };
        form.Controls.Add(label);
        form.Controls.Add(copyAddr);
        form.Controls.Add(copyToken);
        form.ShowDialog(this);
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        try
        {
            if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
            _bridge?.Dispose();
        }
        catch { }
        try
        {
            Directory.CreateDirectory(_dataDir);
            var state = new SavedWindow
            {
                Bounds = WindowState == FormWindowState.Normal
                    ? Bounds
                    : RestoreBounds,
                Maximized = WindowState == FormWindowState.Maximized,
            };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(state));
        }
        catch
        {
            // Window memory is a nicety; never block closing over it.
        }
    }

    private void RestoreSavedBounds()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return;
            }

            var state = JsonSerializer.Deserialize<SavedWindow>(
                File.ReadAllText(SettingsPath));
            if (state is null || state.Bounds.Width < 200 || state.Bounds.Height < 200)
            {
                return;
            }

            // Only restore if the saved spot is still on a connected screen.
            foreach (var screen in Screen.AllScreens)
            {
                if (!screen.WorkingArea.IntersectsWith(state.Bounds))
                {
                    continue;
                }

                StartPosition = FormStartPosition.Manual;
                Bounds = state.Bounds;
                if (state.Maximized)
                {
                    WindowState = FormWindowState.Maximized;
                }

                return;
            }
        }
        catch
        {
            // Corrupt settings file: fall back to defaults.
        }
    }

    private sealed class SavedWindow
    {
        public Rectangle Bounds { get; set; }
        public bool Maximized { get; set; }
    }
}
