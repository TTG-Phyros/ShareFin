using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Sharefin.Model;
using Jellyfin.Plugin.Sharefin.Security;
using Jellyfin.Plugin.Sharefin.Storage;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Sharefin.Api;

/// <summary>
/// Admin endpoints to manage share links.
/// </summary>
[ApiController]
[Route("Sharefin/links")]
[Authorize(Policy = Policies.RequiresElevation)]
public class AdminController : ControllerBase
{
    /// <summary>Minimum password length.</summary>
    public const int MinPasswordLength = 6;

    private readonly ShareLinkStore _store;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminController"/> class.
    /// </summary>
    /// <param name="store">The link store.</param>
    /// <param name="libraryManager">The library manager.</param>
    public AdminController(ShareLinkStore store, ILibraryManager libraryManager)
    {
        _store = store;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Lists every link.
    /// </summary>
    /// <returns>The links.</returns>
    [HttpGet]
    public IEnumerable<LinkDto> List()
    {
        var now = DateTime.UtcNow;
        return _store.GetAll().Select(l => LinkDto.From(l, now, _libraryManager.GetItemById(l.ItemId) is not null));
    }

    /// <summary>
    /// Creates a link.
    /// </summary>
    /// <param name="request">The item, password and optional expiry.</param>
    /// <returns>The new link.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<LinkDto> Create([FromBody] CreateLinkRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var item = _libraryManager.GetItemById(request.ItemId);
        if (item is null || !ShareScope.IsShareable(item))
        {
            return BadRequest("Item not found, or not a movie, series, season or episode.");
        }

        if (!IsValidPassword(request.Password))
        {
            return BadRequest($"Password must be at least {MinPasswordLength} characters.");
        }

        var now = DateTime.UtcNow;
        var expires = request.ExpiresUtc?.ToUniversalTime();
        if (expires <= now)
        {
            return BadRequest("Expiry must be in the future.");
        }

        var link = new ShareLink
        {
            Id = Guid.NewGuid(),
            Token = SharePassword.NewToken(),
            ItemId = item.Id,
            ItemName = item.Name,
            ItemType = item.GetBaseItemKind().ToString(),
            CreatedUtc = now,
            ExpiresUtc = expires,
        };
        link.SetPassword(SharePassword.Hash(request.Password!));
        _store.Add(link);
        return LinkDto.From(link, now, true);
    }

    /// <summary>
    /// Revokes a link. Every remembered password stops working at once.
    /// </summary>
    /// <param name="id">The link id.</param>
    /// <returns>No content, or not found.</returns>
    [HttpPost("{id}/revoke")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Revoke([FromRoute] Guid id)
    {
        return _store.Update(id, l => l.Revoke(DateTime.UtcNow)) ? NoContent() : NotFound();
    }

    /// <summary>
    /// Changes a link password. Every remembered password stops working at once.
    /// </summary>
    /// <param name="id">The link id.</param>
    /// <param name="request">The new password.</param>
    /// <returns>No content, bad request or not found.</returns>
    [HttpPost("{id}/password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult ChangePassword([FromRoute] Guid id, [FromBody] PasswordRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsValidPassword(request.Password))
        {
            return BadRequest($"Password must be at least {MinPasswordLength} characters.");
        }

        var hash = SharePassword.Hash(request.Password!);
        return _store.Update(id, l => l.SetPassword(hash)) ? NoContent() : NotFound();
    }

    /// <summary>
    /// Changes when a link expires, or makes it permanent. A revoked link stays revoked.
    /// </summary>
    /// <param name="id">The link id.</param>
    /// <param name="request">The new expiry, or null for permanent.</param>
    /// <returns>No content, bad request or not found.</returns>
    [HttpPost("{id}/expiry")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult ChangeExpiry([FromRoute] Guid id, [FromBody] ExpiryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var expires = request.ExpiresUtc?.ToUniversalTime();
        if (expires <= DateTime.UtcNow)
        {
            return BadRequest("Expiry must be in the future.");
        }

        // Cookies keep their own expiry: shortening applies at once (the link is checked on every
        // request), extending means visitors re-enter the password when their cookie runs out.
        return _store.Update(id, l => l.ExpiresUtc = expires) ? NoContent() : NotFound();
    }

    /// <summary>
    /// Deletes a link.
    /// </summary>
    /// <param name="id">The link id.</param>
    /// <returns>No content, or not found.</returns>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Delete([FromRoute] Guid id) => _store.Remove(id) ? NoContent() : NotFound();

    private static bool IsValidPassword(string? password) => password is { Length: >= MinPasswordLength and <= 256 };
}
