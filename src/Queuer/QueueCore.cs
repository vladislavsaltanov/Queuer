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
        if (IsClosed) return "Очередь закрыта";
        if (_list.Any(x => x.Id == u.Id)) return $"Ты уже {Pos(u.Id)}-й";
        if (_lastLeave.TryGetValue(u.Id, out var t) && now - t < TimeSpan.FromSeconds(15))
            return "Подожди 15с";
        _list.Add(u);
        return null;
    }

    public string? Leave(long userId, DateTimeOffset now)
    {
        var i = _list.FindIndex(x => x.Id == userId);
        if (i < 0) return "Тебя нет в очереди";
        _list.RemoveAt(i);
        _lastLeave[userId] = now;
        _pendingOut.Remove(userId);
        return null;
    }

    // Number to user id at once. Shifts stay safe.
    public string? ProposeSwap(long fromId, int number, out long targetId)
    {
        targetId = 0;
        if (number < 1 || number > _list.Count) return "Нет такого номера";
        var target = _list[number - 1];
        if (target.Id == fromId) return "Это ты";
        if (_pendingOut.Contains(fromId)) return "Дождись ответа";
        if (_pendingIn.Contains(target.Id)) return "Дождись ответа";
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

    // Restore saved list. Skip dups.
    public void Restore(IEnumerable<QueueUser> users)
    {
        foreach (var u in users)
            if (!_list.Any(x => x.Id == u.Id)) _list.Add(u);
    }

    public int Pos(long userId) => _list.FindIndex(x => x.Id == userId) + 1;

    public string Render()
    {
        var head = $"🧾 Очередь ({_list.Count})";
        var body = _list.Count == 0
            ? "Очередь пуста. Жми кнопку ниже."
            : string.Join("\n", _list.Select((u, i) => $"{i + 1}. {u.Name}"));
        return IsClosed ? $"{head}\n{body}\n⛔ Очередь закрыта" : $"{head}\n{body}";
    }
}
