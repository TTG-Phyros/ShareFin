using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Sharefin.Model;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.DataProtection;

namespace Jellyfin.Plugin.Sharefin.Security;

/// <summary>
/// Issues and checks the per-link "password already entered" cookie.
/// Content: link id, link stamp and issue date, encrypted and signed by Data Protection
/// with an expiry. Changing the password or revoking changes the stamp, so old cookies fail.
/// </summary>
public sealed class ShareCookie
{
    private const string Purpose = "Sharefin.Access.v1";

    private readonly ITimeLimitedDataProtector _protector;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShareCookie"/> class.
    /// Keys live in the plugin's own data folder, apart from Jellyfin's own Data Protection setup.
    /// </summary>
    /// <param name="paths">Jellyfin application paths.</param>
    public ShareCookie(IApplicationPaths paths)
        : this(DataProtectionProvider.Create(
            new DirectoryInfo(Path.Combine(paths?.DataPath ?? throw new ArgumentNullException(nameof(paths)), "sharefin", "keys")),
            builder => builder.SetApplicationName("Sharefin")))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ShareCookie"/> class with a given provider.
    /// </summary>
    /// <param name="provider">The Data Protection provider.</param>
    internal ShareCookie(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose).ToTimeLimitedDataProtector();
    }

    /// <summary>
    /// Gets the cookie name for a link. One cookie per link, so links never share access.
    /// </summary>
    /// <param name="token">The link token.</param>
    /// <returns>The cookie name.</returns>
    public static string CookieName(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty));
        return "sf_" + Convert.ToHexStringLower(hash.AsSpan(0, 6));
    }

    /// <summary>
    /// Gets when a freshly issued cookie should expire: the link expiry, or a sliding window for a permanent link.
    /// </summary>
    /// <param name="link">The link.</param>
    /// <param name="rememberDays">Sliding window for permanent links.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <returns>The cookie expiry.</returns>
    public static DateTimeOffset ExpiryFor(ShareLink link, int rememberDays, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(link);
        return link.ExpiresUtc is { } expires
            ? new DateTimeOffset(DateTime.SpecifyKind(expires, DateTimeKind.Utc))
            : nowUtc.AddDays(Math.Max(1, rememberDays));
    }

    /// <summary>
    /// Creates the protected cookie value.
    /// </summary>
    /// <param name="link">The link.</param>
    /// <param name="expires">When the value stops being valid.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <returns>The cookie value.</returns>
    public string Issue(ShareLink link, DateTimeOffset expires, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(link);
        var payload = string.Create(CultureInfo.InvariantCulture, $"{link.Id:N}|{link.Stamp}|{nowUtc.UtcTicks}");
        return _protector.Protect(payload, expires);
    }

    /// <summary>
    /// Checks a cookie value against the current state of a link.
    /// The caller must also check the link is active.
    /// </summary>
    /// <param name="value">The cookie value, or null.</param>
    /// <param name="link">The link.</param>
    /// <returns>True when the cookie was issued for this link and its current stamp, and has not expired.</returns>
    public bool Validate(string? value, ShareLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        string payload;
        try
        {
            payload = _protector.Unprotect(value, out _);
        }
        catch (CryptographicException)
        {
            return false; // tampered, foreign key, or expired
        }

        var parts = payload.Split('|');
        return parts.Length == 3
            && string.Equals(parts[0], link.Id.ToString("N"), StringComparison.Ordinal)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(parts[1]), Encoding.UTF8.GetBytes(link.Stamp));
    }
}
