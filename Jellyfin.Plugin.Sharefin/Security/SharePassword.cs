using System;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.Sharefin.Security;

/// <summary>
/// Password hashing (PBKDF2-HMAC-SHA512) and random token generation.
/// </summary>
public static class SharePassword
{
    /// <summary>OWASP 2023 recommendation for PBKDF2-HMAC-SHA512.</summary>
    private const int Iterations = 210_000;

    private const int SaltSize = 16;

    private const int HashSize = 32;

    /// <summary>
    /// Hashes a password with a fresh random salt.
    /// </summary>
    /// <param name="password">The clear password.</param>
    /// <returns>The hash as "iterations.salt.hash", base64.</returns>
    public static string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA512, HashSize);
        return string.Create(CultureInfo.InvariantCulture, $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}");
    }

    /// <summary>
    /// Checks a password against a stored hash, in constant time.
    /// </summary>
    /// <param name="password">The clear password.</param>
    /// <param name="stored">The stored hash from <see cref="Hash"/>.</param>
    /// <returns>True when the password matches.</returns>
    public static bool Verify(string? password, string? stored)
    {
        if (password is null || string.IsNullOrEmpty(stored))
        {
            return false;
        }

        var parts = stored.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) || iterations <= 0)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[1]);
            var expected = Convert.FromBase64String(parts[2]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a cryptographically random URL-safe string.
    /// </summary>
    /// <param name="bytes">Number of random bytes (32 = 256 bits).</param>
    /// <returns>The random value, base64url.</returns>
    public static string NewToken(int bytes = 32) => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));
}
