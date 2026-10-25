using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Server.Zone.Models;
using NameFilterRow = RO2.GameData.Tables.NameFilter;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Character rename request, shared by the rename check (1564) and the rename itself (1563), together with the server-side checks both run.
/// </summary>
/// <remarks>
/// <para>
/// Wire layout: a 64-bit rename scroll id, then the current name and the new name as length-prefixed UTF-16 strings.
/// </para>
/// <para>
/// Result codes returned to the client: 15401 is a server-side failure of the rename, 15402 means the name is taken or too short (or no scroll is present), 15403 means the name is not allowed, and 0 means the name may be used.
/// </para>
/// <para>
/// Names under <see cref="NameValidator.MinLength"/> characters, names with invalid characters, names longer than <see cref="MaxRenameLength"/> and names matching the name filter are refused. Names that are already taken are refused with 15402.
/// </para>
/// </remarks>
public static class CharacterRename
{
 /// <summary>Item type of the character name change scrolls.</summary>
    public const byte NameChangeScrollItemType = 108;

 /// <summary>Result code 15401: the rename failed on the server.</summary>
    public const uint ResultFailed = 15401;

 /// <summary>Result code 15402: the name is taken, too short, or no rename scroll is present.</summary>
    public const uint ResultRefused = 15402;

 /// <summary>Result code 15403: the name contains invalid characters, is too long, or is prohibited.</summary>
    public const uint ResultNotAllowed = 15403;

 /// <summary>
 /// The longest new name the rename accepts. It is kept separate from <see cref="NameValidator.MaxLength"/>, which also applies to character creation.
 /// </summary>
    public const int MaxRenameLength = 14;

    public readonly record struct Request(ulong ScrollUid, string CurrentName, string NewName);

 /// <summary>Parses a rename request body. Returns false when the body is short or malformed.</summary>
    public static bool TryParse(ReadOnlySpan<byte> body, out Request request)
    {
        request = default;
        if (body.Length < 8) return false;
        ulong uid = BinaryPrimitives.ReadUInt64LittleEndian(body);
        int pos = 8;
        if (!ProudWStr.TryRead(body, ref pos, out var current)) return false;
        if (!ProudWStr.TryRead(body, ref pos, out var next)) return false;
        request = new Request(uid, current, next);
        return true;
    }

 /// <summary>
 /// Checks the new name on its own, without the rename scroll or the database.
 /// </summary>
 /// <returns>
 /// 0 when the name passes, otherwise the result code and a log reason. A name that is too short or is the character's current name gives <see cref="ResultRefused"/>. Invalid characters, prohibited names and names over <see cref="MaxRenameLength"/> give <see cref="ResultNotAllowed"/>.
 /// </returns>
 /// <remarks>
 /// A row of the name filter matches inside a name only when it is "GM" (case-sensitive). Every other row matches only the whole name.
 /// </remarks>
    public static (uint result, string? reason) CheckName(string newName, string currentName, IReadOnlyList<NameFilterRow>? filter)
    {
        if (newName.Length < NameValidator.MinLength)
            return (ResultRefused, $"too short (min {NameValidator.MinLength})");
        var (refusal, reason) = NameValidator.Check(newName, filter);
        if (refusal != NameRefusal.None)
            return (ResultNotAllowed, reason);
        if (newName.Length > MaxRenameLength)
            return (ResultNotAllowed, $"too long for a rename (max {MaxRenameLength})");
        if (string.Equals(newName, currentName, StringComparison.Ordinal))
            return (ResultRefused, "same name");
        return (0, null);
    }

 /// <summary>
 /// Runs the full rename check: the name rules first, then the scroll, then whether another live character already has the name.
 /// </summary>
 /// <returns>
 /// The scroll for the rename to consume and 0, or null and the refusal code.
 /// </returns>
 /// <remarks>
 /// The name rules are checked first, so a duplicate request is refused with the name code whatever state the scroll is in.
 /// </remarks>
    public static async Task<(uint result, InventoryItem? scroll, string? reason)> CheckAsync(
        PlayerState player, Request request, IServiceProvider services)
    {
        var store = services.GetRequiredService<GameDataStore>();

        var (nameResult, nameReason) = CheckName(request.NewName, player.Name, store.NameFilter);
        if (nameResult != 0)
            return (nameResult, null, nameReason);

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var scroll = request.ScrollUid == 0 ? null : await inventory.GetByIdAsync(request.ScrollUid);
        
        if (scroll is null || scroll.CharacterId != player.CharacterId || scroll.StorageType != 0 || scroll.Count <= 0)
            return (ResultRefused, null, "no such scroll in the bag");
        if (!store.Items.TryGetValue(scroll.ItemId, out var info) || info.Item_Type != NameChangeScrollItemType)
            return (ResultRefused, null, $"item {scroll.ItemId} is not a name change scroll");

        if (await services.GetRequiredService<ICharacterRepository>().CharacterNameExistsAsync(request.NewName))
            return (ResultRefused, null, "already taken");

        return (0, scroll, null);
    }
}
