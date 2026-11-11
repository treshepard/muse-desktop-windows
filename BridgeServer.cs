using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using System.Runtime.InteropServices;
using Microsoft.VisualBasic.FileIO;

namespace MuseDesktop;

/// <summary>
/// Muse Bridge: a small authenticated HTTP/JSON API served by the app
/// itself, so Muse (over your private Tailscale network) can manage
/// files, run programs, drive Resolve and Adobe Express on this PC.
/// See BRIDGE.md for the design. Raw TcpListener instead of HttpListener
/// so no admin URL reservation is needed.
/// </summary>
public sealed class BridgeServer : IDisposable
{
    public const int Port = 8765;
    private const long MaxBodyBytes = 64 * 1024 * 1024;
    private const long MaxReadFileBytes = 25 * 1024 * 1024;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _dataDir;
    private readonly object _logLock = new();

    // Set by MainForm: marshals an action onto the WinForms UI thread
    // (SendKeys needs a thread with a message pump; bridge calls arrive
    // on threadpool threads).
    internal static Action<Action>? UiThreadInvoke;

    public string Token { get; private set; }
    public volatile bool Paused;

    // --- Auth diagnostics (v2.3.1) ---
    // When a call is rejected, the bridge records WHY in one of three
    // categories plus short SHA-256 fingerprints — never the values
    // themselves. This settles whether a rejection is the credential
    // swap not happening (a literal hsurr: surrogate arrives), the
    // header being stripped (no token at all), or a genuinely
    // different token arriving (fingerprint differs from expected).
    public string LastAuthFailureCategory { get; private set; } = "";
    public string LastAuthFailureReceivedFingerprint { get; private set; } = "";
    public string LastAuthFailureAt { get; private set; } = "";
    public int AuthFailureCount { get; private set; }

    public string ExpectedTokenFingerprint => Fingerprint(Token);

    internal static string Fingerprint(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    private void RecordAuthFailure(Request req)
    {
        var auth = req.Auth ?? "";
        string category;
        string received = "";
        if (string.IsNullOrWhiteSpace(auth))
        {
            category = "no_token";
        }
        else if (!auth.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            category = "no_token";
            received = auth;
        }
        else
        {
            received = auth["Bearer ".Length..];
            category = received.StartsWith("hsurr:", StringComparison.Ordinal)
                ? "surrogate_received"
                : "token_mismatch";
        }
        LastAuthFailureCategory = category;
        LastAuthFailureReceivedFingerprint = received.Length > 0 ? Fingerprint(received) : "";
        LastAuthFailureAt = DateTimeOffset.Now.ToString("o");
        AuthFailureCount++;
        Log(req.Path, $"auth-fail {category} received_fp={LastAuthFailureReceivedFingerprint} " +
                      $"expected_fp={ExpectedTokenFingerprint}");
    }
    public bool Running { get; private set; }
    public string? LastError { get; private set; }

    public BridgeServer(string dataDir)
    {
        _dataDir = dataDir;
        Directory.CreateDirectory(dataDir);
        Token = LoadOrCreateToken();
        _listener = new TcpListener(IPAddress.Any, Port);
    }

    private string TokenPath => Path.Combine(_dataDir, "bridge-token.txt");
    private string LogPath => Path.Combine(_dataDir, "bridge-log.jsonl");

    private string LoadOrCreateToken()
    {
        try
        {
            if (File.Exists(TokenPath))
            {
                var existing = File.ReadAllText(TokenPath).Trim();
                if (existing.Length >= 24)
                {
                    return existing;
                }
            }
        }
        catch { /* regenerate below */ }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        File.WriteAllText(TokenPath, token);
        return token;
    }

    public string RegenerateToken()
    {
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        File.WriteAllText(TokenPath, Token);
        return Token;
    }

    public void Start()
    {
        try
        {
            _listener.Start();
            Running = true;
            _ = Task.Run(AcceptLoop);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Running = false;
        }
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch
            {
                break;
            }
            _ = Task.Run(() => HandleClient(client));
        }
    }

    // ---------- HTTP plumbing ----------

    internal sealed class Request
    {
        public string Method = "";
        public string Path = "";
        public string? Body;
        public string? Auth;
        public JsonDocument? Json;
        public JsonElement Root => Json!.RootElement;
    }

    private void HandleClient(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 30000;
                var stream = client.GetStream();
                var req = ReadRequest(stream);
                if (req is null)
                {
                    return;
                }

                var (status, payload) = Dispatch(req);
                WriteResponse(stream, status, payload);
            }
            catch
            {
                // Client went away mid-request; nothing useful to do.
            }
        }
    }

    private static Request? ReadRequest(NetworkStream stream)
    {
        // Read headers byte by byte until CRLFCRLF (requests are tiny).
        var headerBytes = new List<byte>();
        var window = new byte[4];
        int b;
        while ((b = stream.ReadByte()) != -1)
        {
            headerBytes.Add((byte)b);
            window[0] = window[1]; window[1] = window[2]; window[2] = window[3]; window[3] = (byte)b;
            if (window[0] == '\r' && window[1] == '\n' && window[2] == '\r' && window[3] == '\n')
            {
                break;
            }
            if (headerBytes.Count > 64 * 1024)
            {
                return null;
            }
        }
        if (headerBytes.Count == 0)
        {
            return null;
        }

        var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var first = lines[0].Split(' ');
        if (first.Length < 2)
        {
            return null;
        }
        var rawPath = first[1].Split('?')[0];
        // Proxies may send the absolute-URI form (GET http://host:port/health);
        // normalize it to just the path or routing/auth checks miss.
        if (rawPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            rawPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(rawPath, UriKind.Absolute, out var abs))
            {
                rawPath = abs.AbsolutePath;
            }
        }
        var req = new Request { Method = first[0], Path = rawPath };
        long contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            var idx = line.IndexOf(':');
            if (idx < 0) continue;
            var name = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                long.TryParse(value, out contentLength);
            }
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                req.Auth = value;
            }
        }

        if (contentLength > 0)
        {
            if (contentLength > MaxBodyBytes)
            {
                return null;
            }
            var buf = new byte[contentLength];
            int read = 0;
            while (read < buf.Length)
            {
                int n = stream.Read(buf, read, buf.Length - read);
                if (n <= 0) break;
                read += n;
            }
            req.Body = Encoding.UTF8.GetString(buf, 0, read);
            if (!string.IsNullOrWhiteSpace(req.Body))
            {
                try { req.Json = JsonDocument.Parse(req.Body); } catch { /* leave null */ }
            }
        }
        return req;
    }

    private static void WriteResponse(NetworkStream stream, int status, string payload)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        var reason = status switch
        {
            200 => "OK", 400 => "Bad Request", 401 => "Unauthorized",
            403 => "Forbidden", 404 => "Not Found", 423 => "Locked",
            _ => "Error",
        };
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        stream.Write(head, 0, head.Length);
        stream.Write(body, 0, body.Length);
        stream.Flush();
    }

    private static string Ok(object data) =>
        JsonSerializer.Serialize(new { ok = true, data });

    private static string Err(string message) =>
        JsonSerializer.Serialize(new { ok = false, error = message });

    private void Log(string path, string summary)
    {
        try
        {
            lock (_logLock)
            {
                var line = JsonSerializer.Serialize(new
                {
                    t = DateTimeOffset.Now.ToString("o"),
                    path,
                    summary,
                });
                File.AppendAllText(LogPath, line + "\n");
            }
        }
        catch { /* logging must never break the bridge */ }
    }

    public string[] RecentLog(int count = 50)
    {
        try
        {
            if (!File.Exists(LogPath)) return Array.Empty<string>();
            var lines = File.ReadAllLines(LogPath);
            return lines.Skip(Math.Max(0, lines.Length - count)).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    // ---------- Routing ----------

    private (int, string) Dispatch(Request req)
    {
        var path = req.Path.TrimEnd('/');
        if (path.Length == 0) path = "/health";

        if (path == "/health")
        {
            return (200, Ok(new { bridge = "muse", version = "2.3.1", running = Running, paused = Paused }));
        }

        if (req.Auth != "Bearer " + Token)
        {
            RecordAuthFailure(req);
            return (401, Err("missing or wrong bridge token"));
        }
        if (Paused)
        {
            return (423, Err("bridge is paused on the laptop — resume it from the Muse tray icon"));
        }

        try
        {
            var result = Route(req, path);
            Log(path, "ok");
            return (200, Ok(result));
        }
        catch (BridgeException ex)
        {
            Log(path, "error: " + ex.Message);
            return (ex.Status, Err(ex.Message));
        }
        catch (Exception ex)
        {
            Log(path, "error: " + ex.Message);
            return (400, Err(ex.Message));
        }
    }

    private sealed class BridgeException : Exception
    {
        public int Status { get; }
        public BridgeException(string message, int status = 400) : base(message) { Status = status; }
    }

    private object Route(Request req, string path)
    {
        return path switch
        {
            "/info" => Info(),
            "/log" => RecentLog(GetInt(req, "count", 50)),
            // files
            "/files/list" => FilesList(req),
            "/files/stat" => FilesStat(req),
            "/files/search" => FilesSearch(req),
            "/files/read-text" => FilesReadText(req),
            "/files/read-base64" => FilesReadBase64(req),
            "/files/write-text" => FilesWrite(req, binary: false),
            "/files/write-base64" => FilesWrite(req, binary: true),
            "/files/mkdir" => FilesMkdir(req),
            "/files/copy" => FilesCopyMove(req, move: false),
            "/files/move" => FilesCopyMove(req, move: true),
            "/files/trash" => FilesTrash(req),
            "/files/delete" => FilesDelete(req),
            "/files/zip" => FilesZip(req),
            "/files/unzip" => FilesUnzip(req),
            "/files/hash" => FilesHash(req),
            "/files/open" => FilesOpen(req),
            "/files/reveal" => FilesReveal(req),
            // exec + apps
            "/exec/run" => ExecRun(req),
            "/exec/powershell" => ExecPowerShell(req),
            "/apps/list" => AppsList(),
            "/apps/launch" => AppsLaunch(req),
            "/apps/focus" => AppsFocus(req),
            "/apps/close" => AppsClose(req),
            "/apps/kill" => AppsKill(req),
            "/screen/screenshot" => ScreenShot(),
            // ui automation
            "/ui/windows" => UiWindows(),
            "/ui/tree" => UiTree(req),
            "/ui/click" => UiClick(req),
            "/ui/set-text" => UiSetText(req),
            "/ui/type" => UiType(req),
            "/ui/key" => UiKey(req),
            // resolve + express (other files)
            _ => BridgeApps.Route(this, req, path)
                ?? throw new BridgeException("unknown endpoint: " + path, 404),
        };
    }

    // ---------- JSON helpers (shared with BridgeApps) ----------

    internal static string GetString(Request req, string name, bool required = true)
    {
        if (req.Json is not null && req.Root.ValueKind == JsonValueKind.Object &&
            req.Root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
        {
            return v.GetString()!;
        }
        if (required) throw new BridgeException("missing string field: " + name);
        return "";
    }

    internal static string GetStringOpt(Request req, string name, string fallback = "")
    {
        if (req.Json is not null && req.Root.ValueKind == JsonValueKind.Object &&
            req.Root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
        {
            return v.GetString() ?? fallback;
        }
        return fallback;
    }

    internal static int GetInt(Request req, string name, int fallback)
    {
        if (req.Json is not null && req.Root.ValueKind == JsonValueKind.Object &&
            req.Root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
            v.TryGetInt32(out var n))
        {
            return n;
        }
        return fallback;
    }

    internal static bool GetBool(Request req, string name, bool fallback = false)
    {
        if (req.Json is not null && req.Root.ValueKind == JsonValueKind.Object &&
            req.Root.TryGetProperty(name, out var v) &&
            (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
        {
            return v.GetBoolean();
        }
        return fallback;
    }

    internal static string[] GetStringArray(Request req, string name)
    {
        var list = new List<string>();
        if (req.Json is not null && req.Root.ValueKind == JsonValueKind.Object &&
            req.Root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString()!);
            }
        }
        return list.ToArray();
    }

    internal static string RequirePath(Request req, string name = "path") => GetString(req, name);

    internal static void RequireConfirm(Request req, string what)
    {
        if (!GetBool(req, "confirm"))
        {
            throw new BridgeException(what + " requires \"confirm\": true in the request");
        }
    }

    // ---------- Info ----------

    private object Info()
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => new
            {
                name = d.Name,
                label = d.VolumeLabel,
                totalGB = Math.Round(d.TotalSize / 1073741824.0, 1),
                freeGB = Math.Round(d.AvailableFreeSpace / 1073741824.0, 1),
            }).ToArray();

        var tailnet = new List<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var bytes = addr.Address.GetAddressBytes();
                    // Tailscale CGNAT range 100.64.0.0/10
                    if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
                    {
                        tailnet.Add(addr.Address.ToString());
                    }
                }
            }
        }
        catch { /* report what we have */ }

        return new
        {
            computer = Environment.MachineName,
            user = Environment.UserName,
            os = Environment.OSVersion.VersionString,
            arch = RuntimeInformation.OSArchitecture.ToString(),
            dotnet = Environment.Version.ToString(),
            port = Port,
            tailnetAddresses = tailnet,
            bridgeAddresses = tailnet.Select(ip => $"http://{ip}:{Port}").ToArray(),
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            drives,
            resolve = BridgeApps.ResolveDetect(),
            express = BridgeApps.ExpressDetect(),
        };
    }

    // ---------- Files ----------

    private static object FileEntry(string fullPath)
    {
        var fi = new FileInfo(fullPath);
        var isDir = Directory.Exists(fullPath);
        return new
        {
            name = Path.GetFileName(fullPath),
            path = fullPath,
            dir = isDir,
            size = isDir ? (long?)null : fi.Length,
            modified = (isDir ? Directory.GetLastWriteTime(fullPath) : fi.LastWriteTime).ToString("o"),
        };
    }

    private object FilesList(Request req)
    {
        var dir = RequirePath(req);
        if (!Directory.Exists(dir)) throw new BridgeException("folder not found: " + dir);
        var entries = Directory.EnumerateFileSystemEntries(dir)
            .OrderByDescending(Directory.Exists).ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Take(2000).Select(FileEntry).ToArray();
        return new { path = dir, count = entries.Length, entries };
    }

    private object FilesStat(Request req)
    {
        var path = RequirePath(req);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new BridgeException("not found: " + path);
        }
        return FileEntry(path);
    }

    private object FilesSearch(Request req)
    {
        var root = RequirePath(req, "root");
        var pattern = GetStringOpt(req, "pattern", "*");
        var max = GetInt(req, "max", 200);
        if (!Directory.Exists(root)) throw new BridgeException("folder not found: " + root);
        var results = new List<object>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0 && results.Count < max)
        {
            var dir = pending.Pop();
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(dir, pattern))
                {
                    results.Add(FileEntry(entry));
                    if (results.Count >= max) break;
                }
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    pending.Push(sub);
                }
            }
            catch { /* skip unreadable folders */ }
        }
        return new { root, pattern, count = results.Count, results };
    }

    private object FilesReadText(Request req)
    {
        var path = RequirePath(req);
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new BridgeException("file not found: " + path);
        if (fi.Length > MaxReadFileBytes) throw new BridgeException("file too large to read (25 MB cap): " + fi.Length + " bytes");
        return new { path, size = fi.Length, text = File.ReadAllText(path) };
    }

    private object FilesReadBase64(Request req)
    {
        var path = RequirePath(req);
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new BridgeException("file not found: " + path);
        if (fi.Length > MaxReadFileBytes) throw new BridgeException("file too large to read (25 MB cap): " + fi.Length + " bytes");
        return new { path, size = fi.Length, base64 = Convert.ToBase64String(File.ReadAllBytes(path)) };
    }

    private object FilesWrite(Request req, bool binary)
    {
        var path = RequirePath(req);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (binary)
        {
            var bytes = Convert.FromBase64String(GetString(req, "base64"));
            File.WriteAllBytes(path, bytes);
            return new { path, size = bytes.Length };
        }
        var text = GetString(req, "text");
        File.WriteAllText(path, text);
        return new { path, size = new FileInfo(path).Length };
    }

    private object FilesMkdir(Request req)
    {
        var path = RequirePath(req);
        Directory.CreateDirectory(path);
        return new { path };
    }

    private object FilesCopyMove(Request req, bool move)
    {
        var from = RequirePath(req, "from");
        var to = RequirePath(req, "to");
        var overwrite = GetBool(req, "overwrite");
        var destDir = Path.GetDirectoryName(to);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
        if (Directory.Exists(from))
        {
            if (move)
            {
                Directory.Move(from, to);
            }
            else
            {
                CopyDirectory(from, to, overwrite);
            }
        }
        else if (File.Exists(from))
        {
            if (move) File.Move(from, to, overwrite);
            else File.Copy(from, to, overwrite);
        }
        else
        {
            throw new BridgeException("not found: " + from);
        }
        return new { from, to };
    }

    private static void CopyDirectory(string from, string to, bool overwrite)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite);
        }
        foreach (var sub in Directory.EnumerateDirectories(from))
        {
            CopyDirectory(sub, Path.Combine(to, Path.GetFileName(sub)), overwrite);
        }
    }

    private object FilesTrash(Request req)
    {
        var path = RequirePath(req);
        if (Directory.Exists(path))
        {
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        else if (File.Exists(path))
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        else
        {
            throw new BridgeException("not found: " + path);
        }
        return new { path, recycled = true };
    }

    private object FilesDelete(Request req)
    {
        RequireConfirm(req, "permanent delete");
        var path = RequirePath(req);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
        else throw new BridgeException("not found: " + path);
        return new { path, deleted = true };
    }

    private object FilesZip(Request req)
    {
        var from = RequirePath(req, "from");
        var to = RequirePath(req, "to");
        if (File.Exists(to) && !GetBool(req, "overwrite")) throw new BridgeException("zip already exists: " + to);
        if (File.Exists(to)) File.Delete(to);
        if (Directory.Exists(from)) ZipFile.CreateFromDirectory(from, to, CompressionLevel.Fastest, includeBaseDirectory: true);
        else if (File.Exists(from))
        {
            using var archive = ZipFile.Open(to, ZipArchiveMode.Create);
            archive.CreateEntryFromFile(from, Path.GetFileName(from), CompressionLevel.Fastest);
        }
        else throw new BridgeException("not found: " + from);
        return new { from, to, size = new FileInfo(to).Length };
    }

    private object FilesUnzip(Request req)
    {
        var from = RequirePath(req, "from");
        var to = RequirePath(req, "to");
        Directory.CreateDirectory(to);
        ZipFile.ExtractToDirectory(from, to, overwriteFiles: GetBool(req, "overwrite"));
        return new { from, to };
    }

    private object FilesHash(Request req)
    {
        var path = RequirePath(req);
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return new { path, sha256 = Convert.ToHexString(hash).ToLowerInvariant(), size = new FileInfo(path).Length };
    }

    private object FilesOpen(Request req)
    {
        var path = RequirePath(req);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return new { path, opened = true };
    }

    private object FilesReveal(Request req)
    {
        var path = RequirePath(req);
        Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
        return new { path, revealed = true };
    }

    // ---------- Exec & apps ----------

    internal static object RunProcess(string fileName, string arguments, string? cwd,
        int timeoutMs, string? stdinText = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinText is not null,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrEmpty(cwd)) psi.WorkingDirectory = cwd;
        using var proc = new Process { StartInfo = psi };
        proc.Start();
        if (stdinText is not null)
        {
            proc.StandardInput.Write(stdinText);
            proc.StandardInput.Close();
        }
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(Math.Clamp(timeoutMs, 1000, 600000)))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return new
            {
                exitCode = (int?)null,
                timedOut = true,
                stdout = Truncate(stdoutTask.Result),
                stderr = Truncate(stderrTask.Result),
            };
        }
        return new
        {
            exitCode = (int?)proc.ExitCode,
            timedOut = false,
            stdout = Truncate(stdoutTask.Result),
            stderr = Truncate(stderrTask.Result),
        };
    }

    private static string Truncate(string s) =>
        s.Length <= 200000 ? s : s[..200000] + "\n…[truncated]";

    private object ExecRun(Request req)
    {
        var program = GetString(req, "program");
        var args = GetStringOpt(req, "args", "");
        var cwd = GetStringOpt(req, "cwd", "");
        var timeout = GetInt(req, "timeoutMs", 60000);
        return RunProcess(program, args, string.IsNullOrEmpty(cwd) ? null : cwd, timeout);
    }

    private object ExecPowerShell(Request req)
    {
        var script = GetString(req, "script");
        var timeout = GetInt(req, "timeoutMs", 120000);
        // Pass the script on stdin to avoid quoting/escaping traps.
        return RunProcess("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -",
            null, timeout, stdinText: script);
    }

    private static IEnumerable<Process> WindowProcesses()
    {
        foreach (var p in Process.GetProcesses())
        {
            bool hasWindow = false;
            try { hasWindow = p.MainWindowHandle != IntPtr.Zero; } catch { }
            if (hasWindow) yield return p;
            else p.Dispose();
        }
    }

    private object AppsList()
    {
        var list = new List<object>();
        foreach (var p in WindowProcesses())
        {
            using (p)
            {
                list.Add(new
                {
                    pid = SafeGet(() => p.Id),
                    name = SafeGet(() => p.ProcessName),
                    title = SafeGet(() => p.MainWindowTitle),
                });
            }
        }
        return new { count = list.Count, windows = list };
    }

    private static T SafeGet<T>(Func<T> f)
    {
        try { return f(); } catch { return default!; }
    }

    private object AppsLaunch(Request req)
    {
        var target = GetString(req, "target");
        var args = GetStringOpt(req, "args", "");
        var proc = Process.Start(new ProcessStartInfo(target, args) { UseShellExecute = true });
        return new { target, pid = proc?.Id };
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static Process FindWindowProcess(Request req)
    {
        var pid = GetInt(req, "pid", 0);
        var namePart = GetStringOpt(req, "name", "");
        var titlePart = GetStringOpt(req, "title", "");
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (pid != 0 && p.Id == pid) return p;
                if (pid == 0 && p.MainWindowHandle != IntPtr.Zero)
                {
                    if (!string.IsNullOrEmpty(namePart) &&
                        p.ProcessName.Contains(namePart, StringComparison.OrdinalIgnoreCase)) return p;
                    if (!string.IsNullOrEmpty(titlePart) &&
                        p.MainWindowTitle.Contains(titlePart, StringComparison.OrdinalIgnoreCase)) return p;
                }
            }
            catch { }
        }
        throw new BridgeException("no matching window/process found");
    }

    private object AppsFocus(Request req)
    {
        using var p = FindWindowProcess(req);
        ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
        SetForegroundWindow(p.MainWindowHandle);
        return new { pid = p.Id, title = SafeGet(() => p.MainWindowTitle), focused = true };
    }

    private object AppsClose(Request req)
    {
        using var p = FindWindowProcess(req);
        var ok = p.CloseMainWindow();
        return new { pid = p.Id, closeRequested = ok };
    }

    private object AppsKill(Request req)
    {
        RequireConfirm(req, "kill process");
        using var p = FindWindowProcess(req);
        var name = SafeGet(() => p.ProcessName);
        p.Kill(entireProcessTree: false);
        return new { pid = p.Id, name, killed = true };
    }

    // ---------- Screen ----------

    private object ScreenShot()
    {
        var bounds = SystemInformation.VirtualScreen;
        using var bmp = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return new
        {
            width = bounds.Width,
            height = bounds.Height,
            pngBase64 = Convert.ToBase64String(ms.ToArray()),
        };
    }

    // ---------- UI automation ----------

    private object UiWindows()
    {
        var list = new List<object>();
        foreach (var el in AutomationElement.RootElement.FindAll(
            TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>())
        {
            try
            {
                list.Add(new
                {
                    name = el.Current.Name,
                    className = el.Current.ClassName,
                    pid = el.Current.ProcessId,
                    automationId = el.Current.AutomationId,
                });
            }
            catch { }
        }
        return new { count = list.Count, windows = list };
    }

    private static AutomationElement FindWindow(Request req)
    {
        var titlePart = GetStringOpt(req, "title", "");
        var namePart = GetStringOpt(req, "name", "");
        var wanted = !string.IsNullOrEmpty(titlePart) ? titlePart : namePart;
        if (string.IsNullOrEmpty(wanted)) throw new BridgeException("ui: give a window \"title\" (substring)");
        foreach (var el in AutomationElement.RootElement.FindAll(
            TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>())
        {
            try
            {
                if (el.Current.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return el;
                }
            }
            catch { }
        }
        throw new BridgeException("window not found: " + wanted);
    }

    private static AutomationElement? FindByName(AutomationElement root, string name, int maxNodes = 4000)
    {
        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<(AutomationElement el, int depth)>();
        queue.Enqueue((root, 0));
        var seen = 0;
        while (queue.Count > 0 && seen < maxNodes)
        {
            var (el, depth) = queue.Dequeue();
            seen++;
            try
            {
                if (!ReferenceEquals(el, root) &&
                    el.Current.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return el;
                }
                if (depth < 12)
                {
                    var child = walker.GetFirstChild(el);
                    while (child is not null)
                    {
                        queue.Enqueue((child, depth + 1));
                        child = walker.GetNextSibling(child);
                    }
                }
            }
            catch { }
        }
        return null;
    }

    private object UiTree(Request req)
    {
        var window = FindWindow(req);
        var maxNodes = GetInt(req, "max", 300);
        var sb = new StringBuilder();
        var count = 0;
        void Walk(AutomationElement el, int depth)
        {
            if (count >= maxNodes) return;
            count++;
            try
            {
                var c = el.Current;
                sb.Append(new string(' ', depth * 2))
                  .Append(c.ControlType.ProgrammaticName.Replace("ControlType.", ""))
                  .Append(" | ").Append(c.Name)
                  .Append(" | id=").Append(c.AutomationId)
                  .Append(" | class=").AppendLine(c.ClassName);
            }
            catch { return; }
            var walker = TreeWalker.ControlViewWalker;
            var child = walker.GetFirstChild(el);
            while (child is not null && count < maxNodes)
            {
                Walk(child, depth + 1);
                child = walker.GetNextSibling(child);
            }
        }
        Walk(window, 0);
        return new { nodes = count, tree = sb.ToString() };
    }

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    private object UiClick(Request req)
    {
        var window = FindWindow(req);
        var name = GetString(req, "name");
        var el = FindByName(window, name)
            ?? throw new BridgeException("element not found in window: " + name);
        try
        {
            if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var inv))
            {
                ((InvokePattern)inv).Invoke();
                return new { clicked = name, via = "invoke" };
            }
            if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var tog))
            {
                ((TogglePattern)tog).Toggle();
                return new { clicked = name, via = "toggle" };
            }
            if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sel))
            {
                ((SelectionItemPattern)sel).Select();
                return new { clicked = name, via = "select" };
            }
        }
        catch (BridgeException) { throw; }
        catch { /* fall through to coordinate click */ }

        var rect = el.Current.BoundingRectangle;
        if (rect.IsEmpty) throw new BridgeException("element has no clickable area: " + name);
        var x = (uint)(rect.Left + rect.Width / 2);
        var y = (uint)(rect.Top + rect.Height / 2);
        SetCursorPos((int)x, (int)y);
        mouse_event(0x0002, x, y, 0, UIntPtr.Zero); // left down
        mouse_event(0x0004, x, y, 0, UIntPtr.Zero); // left up
        return new { clicked = name, via = "coordinates", x, y };
    }

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    private object UiSetText(Request req)
    {
        var window = FindWindow(req);
        var name = GetString(req, "name");
        var text = GetString(req, "text");
        var el = FindByName(window, name)
            ?? throw new BridgeException("element not found in window: " + name);
        if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
        {
            ((ValuePattern)vp).SetValue(text);
            return new { set = name };
        }
        throw new BridgeException("element does not accept text via ValuePattern: " + name);
    }

    private static void OnUiThread(Action action)
    {
        if (UiThreadInvoke is null)
        {
            action();
            return;
        }
        using var done = new ManualResetEventSlim();
        Exception? error = null;
        UiThreadInvoke(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });
        if (!done.Wait(15000))
        {
            throw new BridgeException("UI-thread action timed out");
        }
        if (error is not null) throw new BridgeException(error.Message);
    }

    private object UiType(Request req)
    {
        // Types into the target window after bringing it to the foreground.
        var text = GetString(req, "text");
        using var p = FindWindowProcess(req);
        var handle = p.MainWindowHandle;
        OnUiThread(() =>
        {
            ShowWindow(handle, 9);
            SetForegroundWindow(handle);
            Thread.Sleep(250);
            SendKeys.SendWait(EscapeSendKeys(text));
        });
        return new { typed = text.Length };
    }

    private object UiKey(Request req)
    {
        var keys = GetString(req, "keys");
        using var p = FindWindowProcess(req);
        var handle = p.MainWindowHandle;
        OnUiThread(() =>
        {
            ShowWindow(handle, 9);
            SetForegroundWindow(handle);
            Thread.Sleep(250);
            SendKeys.SendWait(keys); // SendKeys syntax, e.g. "^s", "{ENTER}", "%{F4}"
        });
        return new { sent = keys };
    }

    private static string EscapeSendKeys(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if ("+^%~(){}[]".Contains(ch)) sb.Append('{').Append(ch).Append('}');
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch { }
        Running = false;
    }
}
