namespace Queuer.Tests;

// Core rules only. No network.
public sealed class QueueCoreTests
{
    private static QueueCore New() => new();

    [Fact]
    public void Join_Appends_Second_Gets_Alert()
    {
        var q = New();
        var now = DateTimeOffset.UtcNow;
        Assert.Null(q.Join(new QueueUser(1, "A"), now));
        Assert.NotNull(q.Join(new QueueUser(1, "A"), now));
        Assert.Null(q.Join(new QueueUser(2, "B"), now));
        Assert.Equal(2, q.List.Count);
    }

    [Fact]
    public void Leave_Removes_And_Renumbers()
    {
        var q = New();
        var now = DateTimeOffset.UtcNow;
        q.Join(new QueueUser(1, "A"), now);
        q.Join(new QueueUser(2, "B"), now);
        Assert.Null(q.Leave(1, now));
        Assert.Single(q.List);
        Assert.Equal(1, q.Pos(2));
        Assert.NotNull(q.Leave(1, now));
    }

    [Fact]
    public void Rejoin_Within_15s_Blocked()
    {
        var q = New();
        var now = DateTimeOffset.UtcNow;
        q.Join(new QueueUser(1, "A"), now);
        q.Leave(1, now);
        Assert.NotNull(q.Join(new QueueUser(1, "A"), now.AddSeconds(5)));
        Assert.Null(q.Join(new QueueUser(1, "A"), now.AddSeconds(16)));
    }

    [Fact]
    public void Swap_Accept_Swaps_By_Id()
    {
        var q = New();
        var now = DateTimeOffset.UtcNow;
        q.Join(new QueueUser(1, "A"), now);
        q.Join(new QueueUser(2, "B"), now);
        q.Join(new QueueUser(3, "C"), now);
        Assert.Null(q.ProposeSwap(1, 3, out var target));
        Assert.Equal(3, target);
        Assert.True(q.AcceptSwap(1, 3));
        Assert.Equal(3, q.List[0].Id);
        Assert.Equal(1, q.List[2].Id);
    }

    [Fact]
    public void Swap_Bad_Number_Or_Self_Rejected()
    {
        var q = New();
        var now = DateTimeOffset.UtcNow;
        q.Join(new QueueUser(1, "A"), now);
        Assert.NotNull(q.ProposeSwap(1, 5, out _));
        Assert.NotNull(q.ProposeSwap(1, 1, out _));
    }

    [Fact]
    public void Swap_Double_Out_Blocked()
    {
        var q = New();
        var now = DateTimeOffset.UtcNow;
        q.Join(new QueueUser(1, "A"), now);
        q.Join(new QueueUser(2, "B"), now);
        Assert.Null(q.ProposeSwap(1, 2, out _));
        Assert.NotNull(q.ProposeSwap(1, 2, out _));
    }

    [Fact]
    public void Close_Blocks_Join_Renders_Mark()
    {
        var q = New();
        q.Join(new QueueUser(1, "A"), DateTimeOffset.UtcNow);
        q.Close();
        Assert.NotNull(q.Join(new QueueUser(2, "B"), DateTimeOffset.UtcNow));
        Assert.Contains("закрыта", q.Render());
    }

    [Fact]
    public void Restore_Keeps_Order_Skips_Dups()
    {
        var q = New();
        q.Restore([new QueueUser(2, "B"), new QueueUser(1, "A"), new QueueUser(2, "B2")]);
        Assert.Equal([2, 1], q.List.Select(u => u.Id));
        Assert.Equal("B", q.List[0].Name);
    }

    [Fact]
    public void Render_With_Title_Shows_Title()
    {
        var q = New();
        q.Title = "Пятница";
        q.Join(new QueueUser(1, "A"), DateTimeOffset.UtcNow);
        Assert.Contains("Пятница", q.Render());
        Assert.DoesNotContain("🧾 Очередь", q.Render());
        Assert.Contains("🧾 Очередь", New().Render());
    }
}
