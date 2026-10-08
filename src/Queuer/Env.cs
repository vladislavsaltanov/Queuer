namespace Queuer;

// Minimal .env support. No deps.
public static class Env
{
    // Load .env from cwd upward. Real env wins.
    public static void Load(string? startDir = null)
    {
        var dir = startDir ?? Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            var p = Path.Combine(dir, ".env");
            if (File.Exists(p)) { LoadFile(p); return; }
            dir = Directory.GetParent(dir)?.FullName;
        }
    }

    public static void LoadFile(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            if (t.StartsWith("export ")) t = t[7..].TrimStart();
            var i = t.IndexOf('=');
            if (i < 1) continue;
            var key = t[..i].Trim();
            var val = t[(i + 1)..].Trim().Trim('"', '\'');
            if (key.Length == 0 || Environment.GetEnvironmentVariable(key) is not null) continue;
            Environment.SetEnvironmentVariable(key, val);
        }
    }

    // ADMIN_CHAT_<chatId>=id1,id2
    public static HashSet<long> ChatAdmins(long chatId)
    {
        var set = new HashSet<long>();
        var raw = Environment.GetEnvironmentVariable($"ADMIN_CHAT_{chatId}");
        if (string.IsNullOrWhiteSpace(raw)) return set;
        foreach (var p in raw.Split(','))
            if (long.TryParse(p.Trim(), out var id)) set.Add(id);
        return set;
    }

    // OPEN_CONTROL=1 (all chats) or OPEN_CONTROL_<chatId>=1: anyone may manage queue.
    public static bool IsOpenControl(long chatId)
        => IsOn(Environment.GetEnvironmentVariable("OPEN_CONTROL"))
        || IsOn(Environment.GetEnvironmentVariable($"OPEN_CONTROL_{chatId}"));

    private static bool IsOn(string? v)
        => v?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
}
