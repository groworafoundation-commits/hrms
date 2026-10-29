using System.Security.Cryptography;
using Dapper;

namespace Hrms.Api;

// Password storage and the sign-in rules. Hashes are salted PBKDF2-SHA256; the format carries its own cost so it can be raised later.
public static class Passwords
{
    public const int MaxFailed = 5;
    public const int LockMinutes = 15;
    const int Iterations = 100_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"PBKDF2$SHA256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var p = stored.Split('$');
        if (p.Length != 5 || p[0] != "PBKDF2" || !int.TryParse(p[2], out var iter)) return false;
        try
        {
            var expected = Convert.FromBase64String(p[4]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(p[3]), iter, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException) { return false; }
    }

    static readonly string Dummy = Hash("not-a-real-password");

    // Always does the same amount of work, so an unknown user takes as long as a wrong password.
    // Falls back to the old plain-text value only for a user not yet migrated.
    public static bool VerifyAny(string password, string? hash, string? legacyPlain, bool userExists)
    {
        if (!userExists) { Verify(password, Dummy); return false; }
        if (!string.IsNullOrEmpty(hash)) return Verify(password, hash);
        if (string.IsNullOrEmpty(legacyPlain)) { Verify(password, Dummy); return false; }
        return CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(password), System.Text.Encoding.UTF8.GetBytes(legacyPlain));
    }

    // At least 8 characters with a letter and a digit.
    public static string? Policy(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8) return "Password must be at least 8 characters.";
        if (password.Length > 64) return "Password can be at most 64 characters.";
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit)) return "Password must contain a letter and a number.";
        return null;
    }

    // One-time: hash every password still stored as plain text, then clear the plain value.
    public static async Task<int> MigrateLegacy(Db db)
    {
        await using var c = db.OpenWrite();
        var rows = (await c.QueryAsync<(string UserId, string Password)>(
            "SELECT userid AS UserId, password AS Password FROM HRMSINTER.interface.Userdef WHERE PasswordHash IS NULL AND password IS NOT NULL AND LEN(password) > 0")).ToList();
        if (rows.Count == 0) return 0;
        var hashed = new (string UserId, string Hash)[rows.Count];
        Parallel.For(0, rows.Count, i => hashed[i] = (rows[i].UserId, Hash(rows[i].Password)));
        foreach (var (userId, hash) in hashed)
            await c.ExecuteAsync("UPDATE HRMSINTER.interface.Userdef SET PasswordHash=@hash, password=NULL, PasswordChangedAt=GETDATE() WHERE userid=@userId", new { userId, hash });
        return rows.Count;
    }
}
