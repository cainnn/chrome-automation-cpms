using System.Diagnostics;
using System.Text.Json;

namespace ChromeAutomation.Client;

/// <summary>
/// Client for the 32-bit Java Access Bridge helper process.
/// Launches ChromeAutomation.JavaAccessBridge.exe as a child process
/// and communicates via JSON-over-stdin/stdout IPC.
/// </summary>
public class JabClient : IDisposable
{
    private Process? _process;
    private int _requestId;
    private bool _disposed;

    /// <summary>Launch the JAB helper process and wait for it to be ready.</summary>
    public async Task ConnectAsync(int timeoutMs = 15000)
    {
        var exePath = FindJabHelper();
        if (exePath == null)
            throw new FileNotFoundException(
                "ChromeAutomation.JavaAccessBridge.exe not found. " +
                "Run: dotnet build src/ChromeAutomation.JavaAccessBridge");

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            }
        };

        // Log stderr to console
        _process.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                Console.WriteLine($"[JAB-err] {e.Data}");
        };

        _process.Start();
        _process.BeginErrorReadLine();

        Console.WriteLine($"[JAB] Started helper process (pid={_process.Id})");

        // Wait for "Ready" message
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            // Read the initial "[JAB] Ready..." line
            var line = await ReadLineAsync(timeoutMs);
            if (line != null && line.Contains("\"cmd\""))
            {
                // Unexpected command response — skip
                continue;
            }
            if (line != null && line.Contains("Ready"))
                return;

            await Task.Delay(200);
        }

        throw new TimeoutException("JAB helper did not become ready in time");
    }

    /// <summary>Enumerate all Java JVMs and their windows.</summary>
    public async Task<JabJvm[]> EnumJvmsAsync(int timeoutMs = 10000)
    {
        var result = await SendCommandAsync("enum_jvms", null, timeoutMs);
        if (result?.TryGetProperty("jvms", out var jvmsArr) == true)
        {
            return JsonSerializer.Deserialize<JabJvm[]>(jvmsArr.GetRawText()) ?? Array.Empty<JabJvm>();
        }
        return Array.Empty<JabJvm>();
    }

    /// <summary>Find a node by role and/or name in a Java window.</summary>
    public async Task<JabNode?> FindNodeAsync(long hwnd, string? role = null, string? name = null, string? nameContains = null, int timeoutMs = 10000)
    {
        var @params = new Dictionary<string, object?> { ["hwnd"] = hwnd };
        if (role != null) @params["role"] = role;
        if (name != null) @params["name"] = name;
        if (nameContains != null) @params["nameContains"] = nameContains;

        var result = await SendCommandAsync("find_node", @params, timeoutMs);
        if (result.HasValue && result.Value.TryGetProperty("found", out var found) && found.GetBoolean())
        {
            if (result.Value.TryGetProperty("node", out var nodeEl))
                return JsonSerializer.Deserialize<JabNode>(nodeEl.GetRawText());
        }
        return null;
    }

    /// <summary>Click a node identified by role/name in a Java window.</summary>
    public async Task<bool> ClickNodeAsync(long hwnd, string? role = null, string? name = null, string? nameContains = null, int timeoutMs = 10000)
    {
        var @params = new Dictionary<string, object?> { ["hwnd"] = hwnd };
        if (role != null) @params["role"] = role;
        if (name != null) @params["name"] = name;
        if (nameContains != null) @params["nameContains"] = nameContains;

        var result = await SendCommandAsync("click", @params, timeoutMs);
        return result?.TryGetProperty("success", out var ok) == true && ok.GetBoolean();
    }

    /// <summary>Execute an accessible action on a node.</summary>
    public async Task<bool> DoActionAsync(int vmId, long ac, int timeoutMs = 10000)
    {
        var result = await SendCommandAsync("do_action", new { vmId, ac }, timeoutMs);
        return result?.TryGetProperty("success", out var ok) == true && ok.GetBoolean();
    }

    /// <summary>Set text content of a text node.</summary>
    public async Task<bool> SetTextAsync(int vmId, long ac, string text, int timeoutMs = 10000)
    {
        var result = await SendCommandAsync("set_text", new { vmId, ac, text }, timeoutMs);
        return result?.TryGetProperty("success", out var ok) == true && ok.GetBoolean();
    }

    /// <summary>Get text content of a node.</summary>
    public async Task<string> GetTextAsync(int vmId, long ac, int timeoutMs = 10000)
    {
        var result = await SendCommandAsync("get_text", new { vmId, ac }, timeoutMs);
        if (result?.TryGetProperty("text", out var text) == true)
            return text.GetString() ?? "";
        return "";
    }

    /// <summary>Get the accessible tree of a window.</summary>
    public async Task<JsonElement?> GetTreeAsync(long hwnd, int depth = 3, int timeoutMs = 15000)
    {
        var result = await SendCommandAsync("get_tree", new { hwnd, depth }, timeoutMs);
        if (result.HasValue && result.Value.TryGetProperty("tree", out var tree))
            return tree;
        return null;
    }

    // ---- IPC ----

    async Task<JsonElement?> SendCommandAsync(string cmd, object? @params, int timeoutMs)
    {
        if (_process == null || _process.HasExited)
            throw new InvalidOperationException("JAB helper not connected");

        var id = Interlocked.Increment(ref _requestId).ToString();
        var msg = new Dictionary<string, object?> { ["id"] = id, ["cmd"] = cmd };
        if (@params != null) msg["params"] = @params;

        var json = JsonSerializer.Serialize(msg);
        await _process.StandardInput.WriteLineAsync(json);
        await _process.StandardInput.FlushAsync();

        // Read response matching our id
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var line = await ReadLineAsync(timeoutMs);
            if (line == null) throw new IOException("JAB helper closed stdout");

            // Skip non-JSON lines (log messages)
            if (!line.StartsWith("{")) continue;

            try
            {
                var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("id", out var respId) && respId.GetString() == id)
                {
                    if (doc.RootElement.TryGetProperty("result", out var result))
                    {
                        if (result.TryGetProperty("error", out var err))
                            throw new InvalidOperationException($"JAB error: {err.GetString()}");
                        return result;
                    }
                    return doc.RootElement;
                }
            }
            catch (JsonException) { /* skip malformed */ }
        }

        throw new TimeoutException($"JAB command '{cmd}' timed out after {timeoutMs}ms");
    }

    async Task<string?> ReadLineAsync(int timeoutMs)
    {
        if (_process == null) return null;
        try
        {
            var task = _process.StandardOutput.ReadLineAsync();
            if (await Task.WhenAny(task, Task.Delay(timeoutMs)) == task)
                return await task;
            return null;
        }
        catch { return null; }
    }

    static string? FindJabHelper()
    {
        // Look for the exe relative to the solution directory
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var searchPaths = new[]
        {
            Path.Combine(baseDir, "ChromeAutomation.JavaAccessBridge.exe"),
            Path.Combine(baseDir, "..", "..", "..", "..",
                "ChromeAutomation.JavaAccessBridge", "bin", "Debug", "net8.0-windows",
                "ChromeAutomation.JavaAccessBridge.exe"),
            Path.Combine(baseDir, "..", "..", "..", "..", "src",
                "ChromeAutomation.JavaAccessBridge", "bin", "Debug", "net8.0-windows",
                "ChromeAutomation.JavaAccessBridge.exe"),
        };

        foreach (var p in searchPaths)
        {
            var full = Path.GetFullPath(p);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_process != null && !_process.HasExited)
            {
                // Send shutdown command
                var shutdownJson = JsonSerializer.Serialize(new { id = "0", cmd = "shutdown" });
                _process.StandardInput.WriteLine(shutdownJson);
                _process.StandardInput.Flush();

                if (!_process.WaitForExit(3000))
                    _process.Kill();
            }
        }
        catch { }

        _process?.Dispose();
    }
}

// ---- JSON DTOs ----

public class JabJvm
{
    public int vmId { get; set; }
    public string title { get; set; } = "";
    public List<JabWindow> windows { get; set; } = new();
}

public class JabWindow
{
    public long hwnd { get; set; }
    public string name { get; set; } = "";
    public string role { get; set; } = "";
}

public class JabNode
{
    public string name { get; set; } = "";
    public string role { get; set; } = "";
    public string states { get; set; } = "";
    public int vmId { get; set; }
    public long ac { get; set; }
    public int x { get; set; }
    public int y { get; set; }
    public int width { get; set; }
    public int height { get; set; }
    public int childrenCount { get; set; }
}
