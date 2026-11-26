using System;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Sharefin.Security;

namespace Jellyfin.Plugin.Sharefin.Model;

/// <summary>
/// A share link, as persisted in the plugin's JSON store.
/// </summary>
public class ShareLink
{
    /// <summary>Gets or sets the link id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the random URL token (256 bits, base64url).</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Gets or sets the shared library item.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the item name at creation time, for display once the item is gone.</summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>Gets or sets the item kind (Movie, Series, Season, Episode).</summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>Gets the PBKDF2 password hash, see <see cref="SharePassword"/>. Set through <see cref="SetPassword"/>.</summary>
    [JsonInclude] // private setters, so only this class can change what logs visitors out; the JSON store still reads them
    public string PasswordHash { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the version stamp written into access cookies. It changes on every
    /// password change or revocation, which invalidates every cookie already issued.
    /// </summary>
    [JsonInclude]
    public string Stamp { get; private set; } = string.Empty;

    /// <summary>Gets or sets the creation date.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>Gets or sets the expiry date, or null for a permanent link.</summary>
    public DateTime? ExpiresUtc { get; set; }

    /// <summary>Gets the revocation date, or null when not revoked. Set through <see cref="Revoke"/>.</summary>
    [JsonInclude]
    public DateTime? RevokedUtc { get; private set; }

    /// <summary>Gets or sets how many times the shared content was opened.</summary>
    public int AccessCount { get; set; }

    /// <summary>Gets or sets the last time the shared content was opened.</summary>
    public DateTime? LastAccessUtc { get; set; }

    /// <summary>
    /// Whether the link can still be used: not revoked and not expired.
    /// </summary>
    /// <param name="nowUtc">The current time.</param>
    /// <returns>True when the link is usable.</returns>
    public bool IsActive(DateTime nowUtc) => RevokedUtc is null && (ExpiresUtc is null || ExpiresUtc > nowUtc);

    /// <summary>
    /// Sets the password, for a new link or a change. Visitors who entered the old one are logged out.
    /// </summary>
    /// <param name="hash">The hash from <see cref="SharePassword.Hash"/>.</param>
    public void SetPassword(string hash)
    {
        ArgumentException.ThrowIfNullOrEmpty(hash);
        PasswordHash = hash;
        Stamp = SharePassword.NewToken(16);
    }

    /// <summary>
    /// Revokes the link and logs every visitor out. Revoking again keeps the first date.
    /// </summary>
    /// <param name="nowUtc">The current time.</param>
    public void Revoke(DateTime nowUtc)
    {
        RevokedUtc ??= nowUtc;
        Stamp = SharePassword.NewToken(16);
    }
}
