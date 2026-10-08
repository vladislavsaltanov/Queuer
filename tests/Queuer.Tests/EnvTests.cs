using Queuer;

namespace Queuer.Tests;

// Env parsing only. No network.
public sealed class EnvTests
{
    [Fact]
    public void LoadFile_Sets_Vars_Ignores_Comments()
    {
        var p = Path.GetTempFileName();
        File.WriteAllText(p, "# c\nTEST_Q_ENV_1=hi\nTEST_Q_ENV_2=\"a b\"\nEMPTY\n");
        try
        {
            Env.LoadFile(p);
            Assert.Equal("hi", Environment.GetEnvironmentVariable("TEST_Q_ENV_1"));
            Assert.Equal("a b", Environment.GetEnvironmentVariable("TEST_Q_ENV_2"));
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void LoadFile_Real_Env_Wins()
    {
        Environment.SetEnvironmentVariable("TEST_Q_ENV_3", "real");
        var p = Path.GetTempFileName();
        File.WriteAllText(p, "TEST_Q_ENV_3=fake\n");
        try
        {
            Env.LoadFile(p);
            Assert.Equal("real", Environment.GetEnvironmentVariable("TEST_Q_ENV_3"));
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void ChatAdmins_Parses_Csv_Skips_Bad()
    {
        Environment.SetEnvironmentVariable("ADMIN_CHAT_900002", " 7, 8 ,x,7");
        var set = Env.ChatAdmins(900002);
        Assert.Equal(new HashSet<long> { 7, 8 }, set);
        Assert.Empty(Env.ChatAdmins(900003));
    }
}
