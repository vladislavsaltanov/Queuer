using System.Text.Json;

namespace Queuer;

// JSON snapshot of queues. Best effort, never throws.
public sealed class QueueStore
{
    public static string Path =>
        Environment.GetEnvironmentVariable("QUEUER_DATA_FILE") ?? "data/queues.json";

    public sealed record SavedUser(long Id, string Name);
    public sealed record SavedQueue(long ChatId, int MsgId, DateTimeOffset Deadline, string Title, List<SavedUser> Users);
    public sealed record SavedClosed(long ChatId, string Title, List<SavedUser> Users);
    public sealed record SavedBotMsg(long ChatId, int MsgId, DateTimeOffset At);
    public sealed record Snapshot(List<SavedQueue> Active, List<SavedClosed> Closed, List<SavedBotMsg>? BotMsgs = null);

    public static void Save(Snapshot snap)
    {
        var p = Path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p) ?? "data");
        var tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(snap));
        File.Move(tmp, p, overwrite: true);
    }

    public static Snapshot Load()
    {
        try
        {
            var raw = File.ReadAllText(Path);
            return JsonSerializer.Deserialize<Snapshot>(raw) ?? new([], []);
        }
        catch { return new([], []); } // no file or corrupt, start fresh
    }
}
