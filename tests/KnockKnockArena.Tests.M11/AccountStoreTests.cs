using System.Security.Cryptography;
using System.Text.Json;
using KnockKnockArena.Server.Auth;

namespace KnockKnockArena.Tests.M11;

/// <summary>
/// Module 11: the account store on its own - what accounts.json holds (a PBKDF2 hash and a
/// salt, never the password), the rules for a new account, the login check, and the score
/// that is added to the account at disconnect. Every test gets its own empty data directory.
/// </summary>
public class AccountStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kka-tests-m11-store-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "accounts.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private JsonElement Stored(string username) =>
        JsonDocument.Parse(File.ReadAllText(FilePath)).RootElement.EnumerateArray()
            .First(a => a.GetProperty("Username").GetString() == username);

    [Fact]
    public void TheStoredHash_IsPbkdf2Sha256_100000Iterations_32Bytes()
    {
        new AccountStore(_dir).TryCreate("carol", "hemmelig1", out _);
        JsonElement carol = Stored("carol");
        byte[] salt = Convert.FromBase64String(carol.GetProperty("SaltBase64").GetString()!);
        byte[] hash = Convert.FromBase64String(carol.GetProperty("PasswordHashBase64").GetString()!);

        Assert.Equal(16, salt.Length);
        Assert.Equal(32, hash.Length);
        Assert.Equal(hash, Rfc2898DeriveBytes.Pbkdf2("hemmelig1", salt, 100_000, HashAlgorithmName.SHA256, 32));
    }

    [Fact]
    public void TheSamePassword_GetsADifferentSaltAndHash()
    {
        AccountStore store = new(_dir);
        store.TryCreate("carol", "hemmelig1", out _);
        store.TryCreate("dave", "hemmelig1", out _);

        Assert.NotEqual(Stored("carol").GetProperty("SaltBase64").GetString(), Stored("dave").GetProperty("SaltBase64").GetString());
        Assert.NotEqual(Stored("carol").GetProperty("PasswordHashBase64").GetString(), Stored("dave").GetProperty("PasswordHashBase64").GetString());
    }

    [Fact]
    public void TheFile_NeverHoldsThePassword()
    {
        new AccountStore(_dir).TryCreate("carol", "hemmelig1", out _);

        Assert.DoesNotContain("hemmelig1", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Validate_TheRightPassword_Succeeds()
    {
        AccountStore store = new(_dir);
        store.TryCreate("carol", "hemmelig1", out _);

        Assert.True(store.Validate("carol", "hemmelig1", out string name));
        Assert.Equal("carol", name);
    }

    [Theory]
    [InlineData("carol", "hemmelig2")]
    [InlineData("carol", "")]
    [InlineData("nobody", "hemmelig1")]
    public void Validate_AWrongPasswordOrAnUnknownName_Fails(string username, string password)
    {
        AccountStore store = new(_dir);
        store.TryCreate("carol", "hemmelig1", out _);

        Assert.False(store.Validate(username, password, out _));
    }

    [Fact]
    public void Validate_AnUnknownName_TakesAsLongAsAWrongPassword()
    {
        AccountStore store = new(_dir);
        store.TryCreate("carol", "hemmelig1", out _);
        store.Validate("carol", "warm-up", out _);

        // The same answer is not enough if the TIME differs: a name that returns at once is a
        // name that does not exist. A wrong password costs one PBKDF2 (milliseconds); an
        // unknown name must cost the same - at least a third of it, to leave room for noise.
        double wrong = MedianMilliseconds(() => store.Validate("carol", "hemmelig2", out _));
        double unknown = MedianMilliseconds(() => store.Validate("nobody_here", "hemmelig2", out _));

        Assert.True(unknown >= wrong / 3, $"an unknown name took {unknown:0.000} ms, a wrong password {wrong:0.000} ms");
    }

    private static double MedianMilliseconds(Action validate)
    {
        List<double> times = new();
        for (int i = 0; i < 7; i++)
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            validate();
            times.Add(watch.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        return times[times.Count / 2];
    }

    [Fact]
    public void ANewStore_HasNoAccounts_UnlessDevAccountsAreAskedFor()
    {
        Assert.False(new AccountStore(_dir).Validate("test", "test", out _));
        Assert.Equal("[]", File.ReadAllText(FilePath).Trim());

        string devDir = _dir + "-dev";
        try
        {
            AccountStore dev = new(devDir, devAccounts: true);
            Assert.True(dev.Validate("test", "test", out _));
            Assert.True(dev.Validate("alice", "password1", out _));
        }
        finally
        {
            try { Directory.Delete(devDir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void TheUsername_IsMatchedWithoutCase_AndGivenBackAsStored()
    {
        AccountStore store = new(_dir);
        store.TryCreate("Carol", "hemmelig1", out _);

        Assert.True(store.Validate("CAROL", "hemmelig1", out string name));
        Assert.Equal("Carol", name);
    }

    [Theory]
    [InlineData("cc", "username must be 3-16 characters: letters, digits, underscore")]
    [InlineData("seventeen_letters", "username must be 3-16 characters: letters, digits, underscore")]
    [InlineData("carol!", "username must be 3-16 characters: letters, digits, underscore")]
    public void AnInvalidUsername_IsRefused(string username, string expected)
    {
        Assert.False(new AccountStore(_dir).TryCreate(username, "hemmelig1", out string error));
        Assert.Equal(expected, error);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    public void ATooShortPassword_IsRefused(string password)
    {
        Assert.False(new AccountStore(_dir).TryCreate("carol", password, out string error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void ATakenName_IsRefused_WhateverTheCase()
    {
        AccountStore store = new(_dir);
        store.TryCreate("carol", "hemmelig1", out _);

        Assert.False(store.TryCreate("CAROL", "andet_pw", out string error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TheScore_IsAddedAndKept_AcrossARestart()
    {
        AccountStore store = new(_dir);
        store.TryCreate("carol", "hemmelig1", out _);
        store.AccumulateStats("carol", 3, 1);
        store.AccumulateStats("carol", 1, 1);

        LeaderboardEntry carol = new AccountStore(_dir).GetLeaderboard(50).First(e => e.Username == "carol");

        Assert.Equal(4, carol.Frags);
        Assert.Equal(2, carol.Deaths);
        Assert.Equal(2.0, carol.Kd);
    }

    [Fact]
    public void TheLeaderboard_IsSortedByKd()
    {
        AccountStore store = new(_dir);
        foreach (string name in new[] { "carol", "dave", "erik" })
            store.TryCreate(name, "hemmelig1", out _);
        store.AccumulateStats("carol", 4, 4);   // 1.00 - more frags than erik, a lower K/D
        store.AccumulateStats("dave", 6, 2);    // 3.00
        store.AccumulateStats("erik", 3, 2);    // 1.50

        List<string> order = store.GetLeaderboard(50).Select(e => e.Username).Where(n => n is "carol" or "dave" or "erik").ToList();

        Assert.Equal(new[] { "dave", "erik", "carol" }, order);
    }
}
