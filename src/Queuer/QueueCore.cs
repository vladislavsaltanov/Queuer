namespace Queuer;

// Pure queue state. No Telegram calls.
public sealed record QueueUser(long Id, string Name);

public sealed class QueueCore
{
    private readonly List<QueueUser> _list = [];
    private readonly Dictionary<long, DateTimeOffset> _lastLeave = [];
    private readonly HashSet<long> _pendingOut = []; // user waits for answer
    private readonly HashSet<long> _pendingIn = []; // user has offer to answer

    public IReadOnlyList<QueueUser> List => _list;
    public bool IsClosed { get; private set; }

    // Null = ok, text = alert.
    public string? Join(QueueUser u, DateTimeOffset now)
    {
        if (IsClosed) return "Closed";
        if (_list.Any(x => x.Id == u.Id)) return $"Already {Pos(u.Id)}";
        if (_lastLeave.TryGetValue(u.Id, out var t) && now - t < TimeSpan.FromSeconds(15))
            return "Wait 15s";
        _list.Add(u);
        return null;
    }

    public string? Leave(long userId, DateTimeOffset now)
    {
        var i = _list.FindIndex(x => x.Id == userId);
        if (i < 0) return "Not in queue";
        _list.RemoveAt(i);
        _lastLeave[userId] = now;
        _pendingOut.Remove(userId);
        return null;
    }

    // Number to user id at once. Shifts stay safe.
    public string? ProposeSwap(long fromId, int number, out long targetId)
    {
        targetId = 0;
        if (number < 1 || number > _list.Count) return "No such number";
        var target = _list[number - 1];
        if (target.Id == fromId) return "Same user";
        if (_pendingOut.Contains(fromId)) return "Wait answer";
        if (_pendingIn.Contains(target.Id)) return "Wait answer";
        targetId = target.Id;
        _pendingOut.Add(fromId);
        _pendingIn.Add(targetId);
        return null;
    }

    // False = someone left.
    public bool AcceptSwap(long fromId, long targetId)
    {
        var a = _list.FindIndex(x => x.Id == fromId);
        var b = _list.FindIndex(x => x.Id == targetId);
        _pendingOut.Remove(fromId);
        _pendingIn.Remove(targetId);
        if (a < 0 || b < 0) return false;
        (_list[a], _list[b]) = (_list[b], _list[a]);
        return true;
    }

    public void CancelSwap(long fromId, long targetId)
    {
        _pendingOut.Remove(fromId);
        _pendingIn.Remove(targetId);
    }

    public void Kick(int number)
    {
        if (number >= 1 && number <= _list.Count) _list.RemoveAt(number - 1);
    }

    public void Close() => IsClosed = true;

    public int Pos(long userId) => _list.FindIndex(x => x.Id == userId) + 1;

    public string Render()
    {
        var head = $"Queue ({_list.Count})";
        var body = _list.Count == 0
            ? "Empty. Press join."
            : string.Join("\n", _list.Select((u, i) => $"{i + 1}. {u.Name}"));
        return IsClosed ? $"{head}\n{body}\nClosed" : $"{head}\n{body}";
    }
}
