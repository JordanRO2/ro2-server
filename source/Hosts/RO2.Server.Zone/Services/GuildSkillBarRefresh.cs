using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builds the quick bar and usable-skill refresh sent to a character that has just left a guild: 2472
/// OnQuickSlotSaveData and 2488 OnUseSkillList, twice, in one burst.
/// </summary>
/// <remarks>
/// <para>
/// Callers are the guild delete, leave and kick handlers, and the offline-kick notice in guild info. The
/// quick bar is resent as stored, since no guild skill sits on it. The skill list excludes the guild's line
/// skills once the character is no longer in the guild's roster.
/// </para>
/// </remarks>
internal static class GuildSkillBarRefresh
{
 /// <summary>
 /// 2472 OnQuickSlotSaveData, U32 family: <c>[u32 10+20n][u64 charUid][u16 n][n x {u8 page, u8 slot,
 /// u8 type, u8 0, u32 entryId, u32 amount, u64 itemUid}]</c>. An empty bar is <c>[u32 10][uid][u16 0]</c>.
 /// </summary>
    internal static byte[] BuildQuickSlotSaveData2472(ulong charUid, IReadOnlyList<CharacterQuickSlot> slots)
    {
        using var w = new PacketWriter(14 + 20 * slots.Count);
        w.WriteUInt32((uint)(10 + 20 * slots.Count));
        w.WriteUInt64(charUid);
        w.WriteUInt16((ushort)slots.Count);
        foreach (var s in slots)
        {
            w.WriteByte(s.Page);
            w.WriteByte(s.Slot);
            w.WriteByte(s.EntryType);
            w.WriteByte(0);
            w.WriteUInt32(s.EntryId);
            w.WriteUInt32(s.Amount);
            w.WriteUInt64(s.ItemUid);
        }
        return w.ToArray();
    }

 /// <summary>
 /// Builds the burst in order: 2472, 2488, 2472, 2488. A null <paramref name="bar"/> leaves the 2472 entries
 /// out so the client's bar is not wiped. An empty <paramref name="skills"/> list leaves the 2488 entries out.
 /// </summary>
    internal static IReadOnlyList<(ushort Opcode, byte[] Body)> BuildBurst(
        ulong charUid, IReadOnlyList<CharacterQuickSlot>? bar, IReadOnlyList<(uint Group, byte Level)> skills)
    {
        var quickSlots = bar is null ? null : BuildQuickSlotSaveData2472(charUid, bar);
        var skillList = skills.Count == 0 ? null : WorldEntryService.BuildUseSkillList2488(skills);
        var burst = new List<(ushort, byte[])>(4);
        for (var copy = 0; copy < 2; copy++)
        {
            if (quickSlots is not null) burst.Add((ZoneS2COpcodes.OnQuickSlotSaveData, quickSlots));
            if (skillList is not null) burst.Add((ZoneS2COpcodes.OnUseSkillList, skillList));
        }
        return burst;
    }

 /// <summary>
 /// Sends the burst to the player's session. Call it after the character has left the guild's roster, so the
 /// 2488 carries only its own skills.
 /// </summary>
    internal static async Task SendAsync(ProudSession session, PlayerState player, IServiceProvider services, ILogger logger)
    {
        IReadOnlyList<CharacterQuickSlot>? bar = null;
        try
        {
            bar = await services.GetRequiredService<IQuickSlotRepository>().GetByCharacterAsync(player.CharacterId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Name}: could not load the quick bar for the guild-exit 2472", player.Name);
        }

        var skills = GuildSkillBookLearn.SkillListFor(player, services.GetRequiredService<GameDataStore>());
        foreach (var (opcode, body) in BuildBurst(player.CharacterUid, bar, skills))
            await session.SendInternalRmiAsync(opcode, body);
        
        
        
        if (skills.Count > 0) UseSkillListLedger.Record(session, skills);

        logger.LogDebug("Session {Id}: guild-exit refresh, {Slots} bar cell(s), {Skills} usable skill(s), twice",
            session.SessionId, bar?.Count ?? -1, skills.Count);
    }
}
