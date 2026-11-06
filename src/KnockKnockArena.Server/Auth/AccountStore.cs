using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KnockKnockArena.Server.Auth;

/// <summary>One persisted account. Passwords are stored as PBKDF2 hashes with a
/// per-account salt — hashing happens SERVER-side (T08: the client sends the
/// password, the server hashes; a client-side hash would just BE the password).</summary>
public sealed class Account
{
    public string Username { get; set; } = "";
    public string PasswordHashBase64 { get; set; } = "";
    public string SaltBase64 { get; set; } = "";
    public long TotalFrags { get; set; }
    public long TotalDeaths { get; set; }
}

public sealed record LeaderboardEntry(string Username, long Frags, long Deaths, double Kd);

/// <summary>
/// Account storage: created via the REST API, validated at TCP login, stats
/// accumulated at session end (disconnect). Persisted as JSON in the data dir —
/// a real database is out of scope for the course; the file survives container
/// restarts when the data dir is a mounted volume.
/// </summary>
public sealed class AccountStore
{
    private const int Pbkdf2Iterations = 100_000;
    private const int HashLength = 32;

    private static readonly Regex UsernamePattern = new("^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly List<Account> _accounts;

    private static readonly byte[] DummySalt = RandomNumberGenerator.GetBytes(16);
    private static readonly byte[] DummyHash = new byte[HashLength];

    /// <param name="devAccounts">Seed a NEW file with alice/password1, bob/password2 and
    /// test/test (--dev-accounts) - for a local development server only: on a shared
    /// server everyone could log in as test.</param>
    public AccountStore(string dataDir, bool devAccounts = false)
    {
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, "accounts.json");

        if (File.Exists(_filePath))
        {
            _accounts = JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(_filePath)) ?? new List<Account>();
            Log.Info($"Loaded {_accounts.Count} accounts from {_filePath}");
        }
        else if (devAccounts)
        {
            _accounts = new List<Account>();
            // Development accounts so the reference client and templates work on a
            // fresh local server. Students create their own via the REST API.
            TryCreate("alice", "password1", out _);
            TryCreate("bob", "password2", out _);
            TryCreate("test", "test", out _);
            Log.Info($"Created new account store at {_filePath} (seeded 3 dev accounts)");
        }
        else
        {
            _accounts = new List<Account>();
            Save();
            Log.Info($"Created new account store at {_filePath} (no accounts yet - create them with POST /api/accounts)");
        }
    }

    /// <summary>Creates an account. The error string doubles as the REST error message.</summary>
    public bool TryCreate(string username, string password, out string error)
    {
        if (!UsernamePattern.IsMatch(username))
        {
            error = "username must be 3-16 characters: letters, digits, underscore";
            return false;
        }
        if (password.Length is < 4 or > 64)
        {
            error = "password must be 4-64 characters";
            return false;
        }

        lock (_gate)
        {
            if (_accounts.Any(a => a.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))
            {
                error = "username already taken";
                return false;
            }

            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, HashLength);
            _accounts.Add(new Account
            {
                Username = username,
                SaltBase64 = Convert.ToBase64String(salt),
                PasswordHashBase64 = Convert.ToBase64String(hash),
            });
            Save();
        }

        error = "";
        return true;
    }

    /// <summary>Checks the password. Usernames match case-insensitively, so the
    /// account's OWN spelling comes back in <paramref name="accountUsername"/> —
    /// callers must use that, not what was typed, or "TEST" and "test" would count
    /// as two different people for one account.</summary>
    public bool Validate(string username, string password, out string accountUsername)
    {
        accountUsername = "";
        Account? account;
        lock (_gate)
            account = _accounts.FirstOrDefault(a => a.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

        byte[] salt = account != null ? Convert.FromBase64String(account.SaltBase64) : DummySalt;
        byte[] expected = account != null ? Convert.FromBase64String(account.PasswordHashBase64) : DummyHash;
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, HashLength);
        bool match = CryptographicOperations.FixedTimeEquals(actual, expected);
        if (account == null)
            return false;
        accountUsername = account.Username;
        return match;
    }

    /// <summary>Adds a finished session's frags/deaths to the account totals
    /// (S01 6.4 decided: accumulation happens at session end / disconnect).</summary>
    public void AccumulateStats(string username, int frags, int deaths)
    {
        if (frags == 0 && deaths == 0)
            return;

        lock (_gate)
        {
            Account? account = _accounts.FirstOrDefault(a => a.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (account == null)
                return;
            account.TotalFrags += frags;
            account.TotalDeaths += deaths;
            Save();
        }
        Log.Info($"Stats: {username} +{frags} frags, +{deaths} deaths (accumulated)");
    }

    /// <summary>Leaderboard sorted by K/D (deaths counted as at least 1), then frags.</summary>
    public List<LeaderboardEntry> GetLeaderboard(int max)
    {
        lock (_gate)
        {
            return _accounts
                .Select(a => new LeaderboardEntry(a.Username, a.TotalFrags, a.TotalDeaths,
                    Math.Round(a.TotalFrags / (double)Math.Max(1, a.TotalDeaths), 2)))
                .OrderByDescending(e => e.Kd)
                .ThenByDescending(e => e.Frags)
                .Take(max)
                .ToList();
        }
    }

    private void Save()
    {
        // Called under _gate. Write-to-temp + move keeps the file intact if the
        // server dies mid-write.
        string json = JsonSerializer.Serialize(_accounts, new JsonSerializerOptions { WriteIndented = true });
        string tempPath = _filePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _filePath, overwrite: true);
    }
}
