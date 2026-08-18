using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using WindowsAccessBridgeInterop;

Console.WriteLine("[JAB] Java Access Bridge helper started (x86)");

// Initialize JAB on an STA thread with message pump
var bridgeReady = new ManualResetEventSlim();
AccessBridge? accessBridge = null;

var jabThread = new Thread(() =>
{
    try
    {
        accessBridge = new AccessBridge();
        accessBridge.Initialize();
        Console.WriteLine("[JAB] Access Bridge initialized");
        bridgeReady.Set();
        System.Windows.Forms.Application.Run();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[JAB] Init failed: {ex.Message}");
        bridgeReady.Set();
    }
});
jabThread.SetApartmentState(ApartmentState.STA);
jabThread.IsBackground = true;
jabThread.Start();

if (!bridgeReady.Wait(5000))
{
    Console.Error.WriteLine("[JAB] Timeout waiting for initialization");
    Environment.Exit(1);
}

if (accessBridge == null || !accessBridge.IsLoaded)
{
    Console.Error.WriteLine("[JAB] Access Bridge not loaded. Ensure Java is installed and jabswitch /enable was run.");
    Environment.Exit(1);
}

Console.WriteLine("[JAB] Ready. Reading commands from stdin...");
Console.Out.Flush();

// IPC loop
while (true)
{
    var line = Console.ReadLine();
    if (line == null) break;

    JsonDocument? doc = null;
    try { doc = JsonDocument.Parse(line); }
    catch { Respond("error", new { error = "Invalid JSON" }); continue; }

    var root = doc.RootElement;
    var id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "0" : "0";
    var cmd = root.TryGetProperty("cmd", out var cmdProp) ? cmdProp.GetString() ?? "" : "";
    var @params = root.TryGetProperty("params", out var pProp) ? pProp : (JsonElement?)null;

    try
    {
        var result = cmd switch
        {
            "enum_jvms" => EnumJvms(accessBridge),
            "get_tree" => GetTree(accessBridge, @params),
            "find_node" => FindNodeCmd(accessBridge, @params),
            "get_node_at" => GetNodeAtCmd(accessBridge, @params),
            "do_action" => DoActionCmd(accessBridge, @params),
            "set_text" => SetTextCmd(accessBridge, @params),
            "get_text" => GetTextCmd(accessBridge, @params),
            "click" => ClickNodeCmd(accessBridge, @params),
            "shutdown" => new { success = true },
            _ => (object)new { error = $"Unknown command: {cmd}" }
        };
        Respond(id, result);
    }
    catch (Exception ex)
    {
        Respond(id, new { error = ex.Message });
    }

    if (cmd == "shutdown") break;
}

if (accessBridge != null) accessBridge.Dispose();
Console.WriteLine("[JAB] Shutdown complete");

// ---- Command implementations ----

object EnumJvms(AccessBridge ab)
{
    var jvms = ab.EnumJvms(hwnd => ab.CreateAccessibleWindow(hwnd));
    var result = jvms.Select(jvm =>
    {
        return new
        {
            vmId = jvm.JvmId,
            title = jvm.GetTitle() ?? "",
            windows = jvm.Windows.Select(w =>
            {
                var wi = w.GetInfo();
                return new
                {
                    hwnd = w.Hwnd.ToInt64(),
                    name = wi.name ?? "",
                    role = wi.role ?? ""
                };
            }).ToList()
        };
    }).ToList();
    return new { jvms = result };
}

object GetTree(AccessBridge ab, JsonElement? p)
{
    var hwnd = GetHwnd(p);
    var depth = p?.TryGetProperty("depth", out var d) == true ? d.GetInt32() : 3;
    var window = ab.CreateAccessibleWindow(hwnd);
    if (window == null) return new { error = "Window not found" };
    return new { tree = SerializeNode(window, depth) };
}

object FindNodeCmd(AccessBridge ab, JsonElement? p)
{
    var hwnd = GetHwnd(p);
    var role = GetString(p, "role");
    var name = GetString(p, "name");
    var nameContains = GetString(p, "nameContains");

    var window = ab.CreateAccessibleWindow(hwnd);
    if (window == null) return new { found = false, error = "Window not found" };

    var node = FindNodeRecursive(window, role, name, nameContains, 10);
    if (node == null) return new { found = false };

    return new { found = true, node = GetNodeInfo(node) };
}

object GetNodeAtCmd(AccessBridge ab, JsonElement? p)
{
    var hwnd = GetHwnd(p);
    var x = p?.TryGetProperty("x", out var xp) == true ? xp.GetInt32() : 0;
    var y = p?.TryGetProperty("y", out var yp) == true ? yp.GetInt32() : 0;

    var window = ab.CreateAccessibleWindow(hwnd);
    if (window == null) return new { found = false, error = "Window not found" };

    var path = window.GetNodePathAt(new Point(x, y));
    if (path?.Leaf is AccessibleContextNode node)
        return new { found = true, node = GetNodeInfo(node) };

    return new { found = false };
}

object DoActionCmd(AccessBridge ab, JsonElement? p)
{
    var vmId = GetInt(p, "vmId");
    var acHandle = GetLong(p, "ac");

    var ac = new JavaObjectHandle(vmId, new JOBJECT64(acHandle));
    AccessibleActions actions;
    if (Failed(ab.Functions.GetAccessibleActions(vmId, ac, out actions)))
        return new { error = "Failed to get actions" };

    if (actions.actionsCount == 0)
        return new { error = "No actions available" };

    var actionToDo = new AccessibleActionsToDo
    {
        actions = actions.actionInfo,
        actionsCount = 1
    };
    int failure;
    ab.Functions.DoAccessibleActions(vmId, ac, ref actionToDo, out failure);
    return new { success = failure == 0, failure };
}

object SetTextCmd(AccessBridge ab, JsonElement? p)
{
    var vmId = GetInt(p, "vmId");
    var acHandle = GetLong(p, "ac");
    var text = GetString(p, "text") ?? "";

    var ac = new JavaObjectHandle(vmId, new JOBJECT64(acHandle));
    var ok = ab.Functions.SetTextContents(vmId, ac, text);
    return new { success = ok };
}

object GetTextCmd(AccessBridge ab, JsonElement? p)
{
    var vmId = GetInt(p, "vmId");
    var acHandle = GetLong(p, "ac");

    var ac = new JavaObjectHandle(vmId, new JOBJECT64(acHandle));
    AccessibleTextInfo textInfo;
    if (Failed(ab.Functions.GetAccessibleTextInfo(vmId, ac, out textInfo, 0, 0)))
        return new { text = "" };

    var sb = new StringBuilder();
    if (textInfo.charCount > 0)
    {
        var len = (short)Math.Min(textInfo.charCount, 10000);
        var buffer = new char[len];
        if (ab.Functions.GetAccessibleTextRange(vmId, ac, 0, textInfo.charCount, buffer, len))
        {
            sb.Append(buffer, 0, len);
        }
    }
    return new { text = sb.ToString() };
}

object ClickNodeCmd(AccessBridge ab, JsonElement? p)
{
    var hwnd = GetHwnd(p);
    var role = GetString(p, "role");
    var name = GetString(p, "name");
    var nameContains = GetString(p, "nameContains");

    var window = ab.CreateAccessibleWindow(hwnd);
    if (window == null) return new { error = "Window not found" };

    var node = FindNodeRecursive(window, role, name, nameContains, 10);
    if (node == null) return new { found = false, error = "Node not found" };

    // Try accessible action first
    var info = node.GetInfo();
    var ac = node.AccessibleContextHandle;
    AccessibleActions actions;
    bool actionSucceeded = false;
    if (ab.Functions.GetAccessibleActions(node.JvmId, ac, out actions) && actions.actionsCount > 0)
    {
        var actionToDo = new AccessibleActionsToDo
        {
            actions = actions.actionInfo,
            actionsCount = 1
        };
        int failure;
        ab.Functions.DoAccessibleActions(node.JvmId, ac, ref actionToDo, out failure);
        actionSucceeded = failure == 0;
        if (actionSucceeded)
            return new { success = true, method = "accessible_action" };
        // Accessible action failed — fall through to OS-level click
    }

    // Fallback: OS-level click at center
    var rect = node.GetScreenRectangle();
    if (rect.HasValue && rect.Value.Width > 0 && rect.Value.Height > 0)
    {
        int x = rect.Value.Left + rect.Value.Width / 2;
        int y = rect.Value.Top + rect.Value.Height / 2;
        SetCursorPos(x, y);
        Thread.Sleep(100);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(30);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        return new { success = true, method = "os_click", x, y };
    }

    return new { success = false, error = "Cannot click (action failed, no valid rect)" };
}

// ---- Helpers ----

static bool Failed(bool result) => !result;

AccessibleContextNode? FindNodeRecursive(AccessibleNode parent, string? role, string? exactName, string? nameContains, int maxDepth)
{
    if (parent is not AccessibleContextNode acNode) return null;

    try
    {
        var info = acNode.GetInfo();
        var nodeName = info.name ?? "";
        var nodeRole = info.role ?? "";

        bool roleMatch = role == null || nodeRole == role;
        bool nameMatch = exactName != null && nodeName == exactName;
        bool nameContainsMatch = nameContains != null && nodeName.Contains(nameContains);

        if (roleMatch && (nameMatch || nameContainsMatch))
            return acNode;
    }
    catch { }

    if (maxDepth <= 0) return null;

    try
    {
        foreach (var child in acNode.GetChildren())
        {
            var found = FindNodeRecursive(child, role, exactName, nameContains, maxDepth - 1);
            if (found != null) return found;
        }
    }
    catch { }

    return null;
}

object GetNodeInfo(AccessibleContextNode node)
{
    try
    {
        var info = node.GetInfo();
        var rect = node.GetScreenRectangle();
        return new
        {
            name = info.name ?? "",
            role = info.role ?? "",
            states = info.states ?? "",
            description = info.description ?? "",
            vmId = node.JvmId,
            ac = node.AccessibleContextHandle.Handle.Value,
            x = info.x, y = info.y, width = info.width, height = info.height,
            childrenCount = info.childrenCount,
            indexInParent = info.indexInParent
        };
    }
    catch (Exception ex)
    {
        return new { error = ex.Message };
    }
}

object SerializeNode(AccessibleContextNode node, int depth)
{
    try
    {
        var info = node.GetInfo();
        var result = new Dictionary<string, object?>
        {
            ["name"] = info.name ?? "",
            ["role"] = info.role ?? "",
            ["states"] = info.states ?? "",
            ["x"] = info.x, ["y"] = info.y,
            ["width"] = info.width, ["height"] = info.height,
        };

        if (depth > 0 && info.childrenCount > 0)
        {
            try
            {
                var children = node.GetChildren()
                    .OfType<AccessibleContextNode>()
                    .Select(c => SerializeNode(c, depth - 1))
                    .ToArray();
                result["children"] = children;
            }
            catch { }
        }
        return result;
    }
    catch (Exception ex)
    {
        return new { error = ex.Message };
    }
}

void Respond(string id, object result)
{
    var json = JsonSerializer.Serialize(new { id, result },
        new JsonSerializerOptions { WriteIndented = false });
    Console.WriteLine(json);
    Console.Out.Flush();
}

IntPtr GetHwnd(JsonElement? p)
    => p?.TryGetProperty("hwnd", out var h) == true ? new IntPtr(h.GetInt64()) : IntPtr.Zero;

string? GetString(JsonElement? p, string key)
    => p?.TryGetProperty(key, out var v) == true ? v.GetString() : null;

int GetInt(JsonElement? p, string key)
    => p?.TryGetProperty(key, out var v) == true ? v.GetInt32() : 0;

long GetLong(JsonElement? p, string key)
    => p?.TryGetProperty(key, out var v) == true ? v.GetInt64() : 0;

[DllImport("user32.dll")] static extern bool SetCursorPos(int X, int Y);
[DllImport("user32.dll")] static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
