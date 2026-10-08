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

    [Fact]
    public void IsOpenControl_Global_Or_PerChat()
    {
        try
        {
            Assert.False(Env.IsOpenControl(900010));
            Environment.SetEnvironmentVariable("OPEN_CONTROL_900010", "yes");
            Assert.True(Env.IsOpenControl(900010));
            Assert.False(Env.IsOpenControl(900011));
            Environment.SetEnvironmentVariable("OPEN_CONTROL", "1");
            Assert.True(Env.IsOpenControl(900011));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPEN_CONTROL", null);
            Environment.SetEnvironmentVariable("OPEN_CONTROL_900010", null);
        }
    }

    [Fact]
    public void IsOpenControl_Garbage_Means_Off()
    {
        Environment.SetEnvironmentVariable("OPEN_CONTROL_900012", "maybe");
        try { Assert.False(Env.IsOpenControl(900012)); }
        finally { Environment.SetEnvironmentVariable("OPEN_CONTROL_900012", null); }
    }
}
