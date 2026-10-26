using RO2.Economy.Domain;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using RO2.GameData.Tables;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Protocol helpers for the crafting recipe list: the list rule, the 2535 and 2538 builders and the 2538
/// result codes. Learned recipes are persisted through
/// <see cref="RO2.Economy.Domain.ICraftRecipeRepository"/>. This class turns stored ids into the
/// payload sent to the client.
/// </summary>
/// <remarks>
/// <para>
/// The client clears all learned-recipe flags on each list message and then sets the flag of every listed id.
/// Each message must therefore carry the character's complete recipe set; a list with only the new id would
/// unlearn the rest.
/// </para>
/// <para>
/// The set is the Craft_Level-1 recipes of the character's profession plus the learned recipes of that
/// profession, in ascending order. Stored recipes of another profession are kept but not sent.
/// </para>
/// </remarks>
public static class CraftRecipeStore
{
    
    
    
 /// Result code: the recipe does not exist (string_ui 7689). Sent for an id with no Craft_ItemList row.
    public const ushort ResultNoSuchRecipe = 22621;

 /// Result code: the profession level is too low (string_ui 7690). Sent when Craft_Level is above the character's profession level.
    public const ushort ResultProfessionLevelTooLow = 22622;

 /// Result code: not enough money (string_ui 7691).
    public const ushort ResultNotEnoughMoney = 22623;

 /// Result code: the NPC cannot teach this craft skill (string_ui 7692).
    public const ushort ResultWrongNpc = 22624;

 /// Result code: the craft skill cannot be learned with the current profession (string_ui 7693).
    public const ushort ResultWrongProfession = 22625;

 /// Result code: the recipe is already learned (string_ui 7694).
    public const ushort ResultAlreadyLearned = 22626;

 /// <summary>
 /// Returns the recipe list for a character of profession <paramref name="proType"/>: the Craft_Level-1
 /// recipes of that profession and the <paramref name="learned"/> ids of it, each once, in ascending order.
 /// </summary>
    public static List<ushort> KnownRecipeIds(IEnumerable<CraftItemList> recipes, byte proType, IEnumerable<ushort>? learned)
    {
        var learnedSet = learned is null ? new HashSet<ushort>() : new HashSet<ushort>(learned);
        return recipes
            .Where(r => r.ProType_ID == proType && (r.Craft_Level == 1 || learnedSet.Contains(r.ID)))
            .Select(r => r.ID)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
    }

 /// <summary>
 /// RMI 2538 OnAckCraftLearn, success form: <c>[u32 4 + 2n][u16 result 0][u16 n][n x u16 recipe id]</c>.
 /// </summary>
 /// <remarks>
 /// The result is always 0 in this form. Refusals are built by <see cref="BuildAckCraftLearnRefusal2538"/>.
 /// </remarks>
    public static byte[] BuildAckCraftLearn2538(IReadOnlyList<ushort> recipeIds)
    {
        using var w = new PacketWriter(8 + 2 * recipeIds.Count);
        w.WriteUInt32((uint)(4 + 2 * recipeIds.Count)); 
        w.WriteUInt16(0);                                 
        w.WriteUInt16((ushort)recipeIds.Count);           
        foreach (var id in recipeIds) w.WriteUInt16(id);  
        return w.ToArray();
    }

 /// <summary>
 /// RMI 2538 OnAckCraftLearn, refusal form: <c>[u32 4][u16 code][u16 0]</c>, 8 bytes, no recipe ids.
 /// </summary>
 /// <remarks>
 /// The client shows the message for <paramref name="code"/> and keeps its learned flags, because a refusal
 /// carries no list.
 /// </remarks>
    public static byte[] BuildAckCraftLearnRefusal2538(ushort code)
    {
        if (code == 0) throw new ArgumentOutOfRangeException(nameof(code), "a refusal needs a non-zero result code");
        var body = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 4);          
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), code); 
        return body;                                                  
    }

 /// <summary>Length of a counted list header: the u32 length word and the u16 count.</summary>
    private const int CountedListHeaderLength = sizeof(uint) + sizeof(ushort);

 /// <summary>
 /// Builds the craft index message listing the first-level recipes of a profession, plus any recipes the character has learned.
 /// </summary>
 /// <remarks>
 /// Returns null when there are no recipes, since an empty message is not sent.
 /// </remarks>
    internal static byte[]? BuildNotifyCraftIndex2535(IEnumerable<RO2.GameData.Tables.CraftItemList> recipes, byte proType,
        IEnumerable<ushort>? learned = null)
    {
        var ids = CraftRecipeStore.KnownRecipeIds(recipes, proType, learned);
        if (ids.Count == 0) return null;

        using var w = new PacketWriter(CountedListHeaderLength + sizeof(ushort) * ids.Count);
        w.WriteUInt32((uint)(sizeof(ushort) + sizeof(ushort) * ids.Count)); 
        w.WriteUInt16((ushort)ids.Count);          
        foreach (var id in ids) w.WriteUInt16(id); 
        return w.ToArray();
    }
}
