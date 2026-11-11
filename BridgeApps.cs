using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text.Json;

namespace MuseDesktop;

/// <summary>
/// DaVinci Resolve + Adobe Express routes for the bridge.
/// Resolve has a real scripting API (Studio: external Python; Free:
/// in-app Lua/Python scripts + file imports). Adobe Express has NO
/// desktop API at all — it is a web app / PWA — so its routes are
/// launch + a bridge-owned WebView window Muse can script + the
/// generic UI-automation endpoints in BridgeServer.
/// </summary>
internal static class BridgeApps
{
    // Wired by MainForm for the bridge-owned Express window.
    public static Action? ExpressOpenWindow;
    public static Func<string, Task<string>>? ExpressEvalJs;
    public static Func<object>? ExpressWindowInfo;

    public static object? Route(BridgeServer server, BridgeServer.Request req, string path)
    {
        return path switch
        {
            "/resolve/detect" => ResolveDetect(),
            "/resolve/launch" => ResolveLaunch(),
            "/resolve/python" => ResolvePython(req),
            "/resolve/queue-script" => ResolveQueueScript(req),
            "/express/detect" => ExpressDetect(),
            "/express/launch" => ExpressLaunch(req),
            "/express/eval" => ExpressEval(req),
            "/express/window-info" => ExpressWindowInfo?.Invoke()
                ?? throw new Exception("express window support is not wired up"),
            _ => null,
        };
    }

    // ---------- Resolve: detection ----------

    internal static string ResolveExePath =>
        @"C:\Program Files\Blackmagic Design\DaVinci Resolve\Resolve.exe";

    internal static string ResolveScriptsUtilityDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Blackmagic Design\DaVinci Resolve\Support\Fusion\Scripts\Utility");

    public static object ResolveDetect()
    {
        var exe = ResolveExePath;
        var installed = File.Exists(exe);
        string? version = null;
        if (installed)
        {
            try { version = FileVersionInfo.GetVersionInfo(exe).ProductVersion; } catch { }
        }
        var helper = Path.Combine(AppContext.BaseDirectory, "helpers", "resolve_helper.py");
        return new
        {
            installed,
            exePath = installed ? exe : null,
            version,
            scriptsUtilityDir = ResolveScriptsUtilityDir,
            scriptsUtilityDirExists = Directory.Exists(ResolveScriptsUtilityDir),
            helperPath = helper,
            helperPresent = File.Exists(helper),
            note = "External Python scripting is Studio-only (Resolve 19.1+). " +
                   "On Studio: Preferences > System > General > External scripting using = Local, " +
                   "then /resolve/python drives the live app. On Free: use /resolve/queue-script " +
                   "(Lua) and run it from Workspace > Scripts inside Resolve, or import files " +
                   "(OTIO/XML/SRT) the bridge prepares.",
        };
    }

    private static object ResolveLaunch()
    {
        if (!File.Exists(ResolveExePath))
        {
            throw new Exception("Resolve.exe not found at the standard path — is DaVinci Resolve installed?");
        }
        var proc = Process.Start(new ProcessStartInfo(ResolveExePath) { UseShellExecute = true });
        return new { launched = true, pid = proc?.Id };
    }

    // ---------- Resolve: Studio Python route ----------

    private static string? FindPython()
    {
        var candidates = new[]
        {
            @"C:\Program Files\Python312\python.exe",
            @"C:\Program Files\Python311\python.exe",
            @"C:\Program Files\Python310\python.exe",
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        try
        {
            var result = BridgeServer.RunProcess("where.exe", "python.exe", null, 5000);
            var text = JsonSerializer.Serialize(result);
            using var doc = JsonDocument.Parse(text);
            var stdout = doc.RootElement.GetProperty("stdout").GetString() ?? "";
            var first = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).FirstOrDefault(s => s.EndsWith("python.exe"));
            if (first is not null && File.Exists(first) &&
                !first.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                return first;
            }
        }
        catch { }
        return null;
    }

    private static object ResolvePython(BridgeServer.Request req)
    {
        var action = BridgeServer.GetString(req, "action");
        var python = FindPython()
            ?? throw new Exception("No real python.exe found (the Windows Store stub does not count). " +
                                   "Install Python 3.10-3.12 from python.org and retry.");
        var helper = Path.Combine(AppContext.BaseDirectory, "helpers", "resolve_helper.py");
        if (!File.Exists(helper)) throw new Exception("resolve_helper.py missing next to Muse.exe: " + helper);

        var paramsJson = "{}";
        if (reqJsonHas(req, "params"))
        {
            paramsJson = GetRawJson(req, "params");
        }
        var tmpDir = Path.Combine(Path.GetTempPath(), "MuseBridge");
        Directory.CreateDirectory(tmpDir);
        var paramsFile = Path.Combine(tmpDir, "resolve-params.json");
        var outFile = Path.Combine(tmpDir, "resolve-out.json");
        File.WriteAllText(paramsFile, paramsJson);
        if (File.Exists(outFile)) File.Delete(outFile);

        var psi = new ProcessStartInfo
        {
            FileName = python,
            Arguments = "\"" + helper + "\" --action " + action +
                        " --params \"" + paramsFile + "\" --out \"" + outFile + "\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // Standard Resolve scripting environment (Windows defaults).
        psi.Environment["RESOLVE_SCRIPT_API"] =
            @"C:\ProgramData\Blackmagic Design\DaVinci Resolve\Support\Developer\Scripting";
        psi.Environment["RESOLVE_SCRIPT_LIB"] =
            @"C:\Program Files\Blackmagic Design\DaVinci Resolve\fusionscript.dll";
        psi.Environment["PYTHONPATH"] =
            @"C:\ProgramData\Blackmagic Design\DaVinci Resolve\Support\Developer\Scripting\Modules";

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(120000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new Exception("resolve helper timed out after 120s (is Resolve running?)");
        }

        if (File.Exists(outFile))
        {
            var text = File.ReadAllText(outFile);
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        throw new Exception("resolve helper produced no result. stdout: " +
            Truncate(stdout.Result) + " stderr: " + Truncate(stderr.Result));
    }

    private static string Truncate(string s) => s.Length <= 4000 ? s : s[..4000];

    private static bool reqJsonHas(BridgeServer.Request req, string name) =>
        req.Json is not null && req.Root.ValueKind == JsonValueKind.Object &&
        req.Root.TryGetProperty(name, out _);

    private static string GetRawJson(BridgeServer.Request req, string name)
    {
        if (req.Json is not null && req.Root.TryGetProperty(name, out var v))
        {
            return v.GetRawText();
        }
        return "{}";
    }

    // ---------- Resolve: Free in-app script route ----------

    private static object ResolveQueueScript(BridgeServer.Request req)
    {
        var kind = BridgeServer.GetStringOpt(req, "kind", "lua").ToLowerInvariant();
        var code = BridgeServer.GetString(req, "code");
        if (kind != "lua" && kind != "python")
        {
            throw new Exception("kind must be \"lua\" or \"python\"");
        }
        // Python scripts no longer list in the Scripts menu on Free 21.1;
        // Lua still does. Studio accepts both.
        var dir = ResolveScriptsUtilityDir;
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "MuseRun." + (kind == "lua" ? "lua" : "py"));
        File.WriteAllText(file, code);
        return new
        {
            written = file,
            howToRun = "In DaVinci Resolve: Workspace > Scripts > MuseRun. " +
                       "The script runs inside Resolve with a live resolve object. " +
                       "Re-queue to replace it with the next task.",
        };
    }

    // ---------- Adobe Express ----------

    public static object ExpressDetect()
    {
        var shortcuts = new List<string>();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(file).Contains("Express", StringComparison.OrdinalIgnoreCase))
                    {
                        shortcuts.Add(file);
                    }
                }
            }
            catch { }
        }
        var edge = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Microsoft\Edge\Application\msedge.exe");
        if (!File.Exists(edge))
        {
            edge = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"Microsoft\Edge\Application\msedge.exe");
        }
        return new
        {
            shortcuts,
            pwaInstalled = shortcuts.Count > 0,
            edgePath = File.Exists(edge) ? edge : null,
            webUrl = "https://new.express.adobe.com",
            note = "Adobe Express has no native desktop app and no local API — Adobe ships it " +
                   "as a web app / PWA (the Microsoft Store listing is a shell). Control routes: " +
                   "launch the PWA/Edge app, use the bridge-owned Express window (/express/eval " +
                   "runs page JS for menus, text, uploads), the generic /ui/* automation + " +
                   "screenshots, and /files/* for its uploads, downloads and exports.",
        };
    }

    private static object ExpressLaunch(BridgeServer.Request req)
    {
        var mode = BridgeServer.GetStringOpt(req, "mode", "window");
        switch (mode)
        {
            case "window":
                ExpressOpenWindow?.Invoke();
                return new { launched = "bridge-window" };
            case "edge":
            {
                var det = (dynamic)ExpressDetect();
                string? edgePath = det.edgePath;
                if (edgePath is null) throw new Exception("Microsoft Edge not found");
                var proc = Process.Start(new ProcessStartInfo(edgePath,
                    "--app=https://new.express.adobe.com") { UseShellExecute = true });
                return new { launched = "edge-app", pid = proc?.Id };
            }
            case "browser":
            {
                var proc = Process.Start(new ProcessStartInfo("https://new.express.adobe.com")
                    { UseShellExecute = true });
                return new { launched = "default-browser", pid = proc?.Id };
            }
            case "pwa":
            {
                var det = (dynamic)ExpressDetect();
                var shortcuts = (List<string>)det.shortcuts;
                if (shortcuts.Count == 0)
                {
                    throw new Exception("No Adobe Express shortcut found. Install it from the " +
                        "Microsoft Store, or use mode \"edge\" / \"window\".");
                }
                var proc = Process.Start(new ProcessStartInfo(shortcuts[0]) { UseShellExecute = true });
                return new { launched = "pwa", shortcut = shortcuts[0], pid = proc?.Id };
            }
            default:
                throw new Exception("mode must be window, edge, browser, or pwa");
        }
    }

    private static object ExpressEval(BridgeServer.Request req)
    {
        var script = BridgeServer.GetString(req, "script");
        if (ExpressEvalJs is null) throw new Exception("express window support is not wired up");
        var result = ExpressEvalJs(script).GetAwaiter().GetResult();
        return new { result };
    }
}
