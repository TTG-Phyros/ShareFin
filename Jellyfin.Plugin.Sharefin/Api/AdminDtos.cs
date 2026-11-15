#pragma warning disable SA1402, SA1649 // small request/response types for one controller, kept together

using System;
using System.ComponentModel.DataAnnotations;
using Jellyfin.Plugin.Sharefin.Model;

namespace Jellyfin.Plugin.Sharefin.Api;

/// <summary>Request body to create a link.</summary>
public class CreateLinkRequest
{
    /// <summary>Gets or sets the item to share.</summary>
    [Required]
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the password.</summary>
    public string? Password { get; set; }

    /// <summary>Gets or sets the expiry date, or null for a permanent link.</summary>
    public DateTime? ExpiresUtc { get; set; }
}

/// <summary>Request body to change a password.</summary>
public class PasswordRequest
{
    /// <summary>Gets or sets the new password.</summary>
    public string? Password { get; set; }
}

/// <summary>A link as shown to the admin. Never includes the password hash or stamp.</summary>
/// <param name="Id">The link id.</param>
/// <param name="Path">Relative public path of the link.</param>
/// <param name="ItemId">The shared item.</param>
/// <param name="ItemName">The item name.</param>
/// <param name="ItemType">The item kind.</param>
/// <param name="CreatedUtc">Creation date.</param>
/// <param name="ExpiresUtc">Expiry date, null when permanent.</param>
/// <param name="Status">Active, Expired, Revoked or Unavailable (item deleted).</param>
/// <param name="AccessCount">How many times the content was opened.</param>
/// <param name="LastAccessUtc">Last opening.</param>
public record LinkDto(Guid Id, string Path, Guid ItemId, string ItemName, string ItemType, DateTime CreatedUtc, DateTime? ExpiresUtc, string Status, int AccessCount, DateTime? LastAccessUtc)
{
    /// <summary>Builds the DTO.</summary>
    /// <param name="l">The link.</param>
    /// <param name="now">The current time.</param>
    /// <param name="itemExists">Whether the item is still in the library.</param>
    /// <returns>The DTO.</returns>
    public static LinkDto From(ShareLink l, DateTime now, bool itemExists)
    {
        ArgumentNullException.ThrowIfNull(l);
        var status = l.RevokedUtc is not null ? "Revoked"
            : !l.IsActive(now) ? "Expired"
            : !itemExists ? "Unavailable"
            : "Active";
        return new LinkDto(l.Id, "Sharefin/s/" + l.Token, l.ItemId, l.ItemName, l.ItemType, l.CreatedUtc, l.ExpiresUtc, status, l.AccessCount, l.LastAccessUtc);
    }
}

/// <summary>Request body to unlock a share link.</summary>
public class AuthRequest
{
    /// <summary>Gets or sets the password.</summary>
    public string? Password { get; set; }
}

/// <summary>Request body to change a link expiry.</summary>
public class ExpiryRequest
{
    /// <summary>Gets or sets the new expiry date, or null for a permanent link.</summary>
    public DateTime? ExpiresUtc { get; set; }
}
