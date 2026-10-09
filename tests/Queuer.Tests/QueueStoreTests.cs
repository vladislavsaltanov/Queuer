namespace Queuer.Tests;

// Store roundtrip only. No network.
public sealed class QueueStoreTests
{
    [Fact]
    public void Roundtrip_Keeps_Active_And_Closed()
    {
        var p = Path.GetTempFileName();
        Environment.SetEnvironmentVariable("QUEUER_DATA_FILE", p);
        try
        {
            var snap = new QueueStore.Snapshot(
                [new QueueStore.SavedQueue(5, 7, DateTimeOffset.UnixEpoch, "Пятница",
                    [new QueueStore.SavedUser(1, "A"), new QueueStore.SavedUser(2, "B")])],
                [new QueueStore.SavedClosed(6, "", [new QueueStore.SavedUser(3, "C")])]);
            QueueStore.Save(snap);
            var back = QueueStore.Load();
            Assert.Single(back.Active);
            Assert.Equal("Пятница", back.Active[0].Title);
            Assert.Equal([1L, 2L], back.Active[0].Users.Select(u => u.Id));
            Assert.Single(back.Closed);
            Assert.Equal(3L, back.Closed[0].Users[0].Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable("QUEUER_DATA_FILE", null);
            File.Delete(p);
        }
    }

    [Fact]
    public void Load_Missing_File_Starts_Empty()
    {
        var p = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json");
        Environment.SetEnvironmentVariable("QUEUER_DATA_FILE", p);
        try
        {
            var back = QueueStore.Load();
            Assert.Empty(back.Active);
            Assert.Empty(back.Closed);
        }
        finally { Environment.SetEnvironmentVariable("QUEUER_DATA_FILE", null); }
    }
}
