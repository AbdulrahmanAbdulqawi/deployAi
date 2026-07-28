using System.Security.Cryptography;

namespace DeployAI.Api.Services;

/// <summary>
/// Values DeployAI invents on the user's behalf, so nobody has to think up a signing key.
/// </summary>
/// <remarks>
/// Shared rather than duplicated: the wizard suggests these when a repository scan finds a secret,
/// and the missing-configuration flow suggests them when an application says at startup that a
/// secret is absent. Two generators drifting apart would mean one path producing values the other
/// path's app rejects.
/// </remarks>
public static class GeneratedSecrets
{
    /// <summary>Alphanumeric, long enough for a JWT signing key.</summary>
    public static string Secret(int length = 48)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return string.Create(length, alphabet, static (span, chars) =>
        {
            Span<byte> bytes = stackalloc byte[span.Length];
            RandomNumberGenerator.Fill(bytes);
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = chars[bytes[i] % chars.Length];
            }
        });
    }

    /// <summary>
    /// A password satisfying the strictest common policy: upper, lower, digit, and symbol.
    /// Symbols exclude anything docker compose / .env files treat specially ($, #, quotes,
    /// backslash) so the value survives Coolify's env interpolation verbatim. An alphanumeric-only
    /// password crash-looped a live deploy once — ASP.NET Identity rejects it at seed time.
    /// </summary>
    public static string Password(int length = 20)
    {
        const string upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        const string lower = "abcdefghijklmnopqrstuvwxyz";
        const string digits = "0123456789";
        const string symbols = "!@^*-_+=?.";
        const string all = upper + lower + digits + symbols;

        Span<byte> bytes = stackalloc byte[length + 4];
        RandomNumberGenerator.Fill(bytes);

        var result = new char[length];
        for (var i = 0; i < length; i++)
        {
            result[i] = all[bytes[i] % all.Length];
        }

        // Guarantee one of each class at random-but-distinct positions.
        result[bytes[length] % length] = upper[bytes[length] % upper.Length];
        var positions = new HashSet<int> { bytes[length] % length };
        var classSets = new[] { lower, digits, symbols };
        for (var c = 0; c < classSets.Length; c++)
        {
            var pos = bytes[length + 1 + c] % length;
            while (positions.Contains(pos))
            {
                pos = (pos + 1) % length;
            }

            positions.Add(pos);
            result[pos] = classSets[c][bytes[length + 1 + c] % classSets[c].Length];
        }

        return new string(result);
    }

    /// <summary>
    /// The value to offer for a key, by what the name implies. Passwords need every character
    /// class; everything else is a plain high-entropy string.
    /// </summary>
    public static string For(string keyName) =>
        keyName.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
            ? Password()
            : Secret();
}
