using System.Buffers.Binary;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1657 (ReqRankerBuff): sells the buff a ranking statue grants to the requesting player.</summary>
/// <remarks>
/// Request: statue NPC handle (u32) and buff id (u32). The buff id must be the function slot of type 47
/// on that statue's NPC data. The buff price is charged from the player's Zeny. The reply is RMI 2967
/// (AnsRankerBuff), which echoes the buff id and carries a result code. Requests that fail a check get a
/// result code and no buff is applied.
/// </remarks>
public class RankerBuffHandler : ZoneRmiHandlerBase<RankerBuffHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqRankerBuff;
    public override string Name => "RankerBuff";

 /// <summary>NPCInfo Function_Type whose Function_ID is the buff a ranking statue grants.</summary>
    private const int RankerBuffFunctionType = 47;

 /// <summary>Config_Def row that holds the statue buff price.</summary>
    private const ushort RankerBuffPriceConfigType = 69;

 /// <summary>Result code of a bought buff.</summary>
    internal const ushort ResultOk = 0;

 /// <summary>Result code: the player does not have enough Zeny.</summary>
    internal const ushort ResultNotEnoughZeny = 1;

 /// <summary>Result code: the requested handle is not an NPC.</summary>
    internal const ushort ResultNotAStatue = 21019;

    public RankerBuffHandler(IServiceScopeFactory s, ILogger<RankerBuffHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 8) return;
        var span = payload.Span;
        uint statueHandle = BinaryPrimitives.ReadUInt32LittleEndian(span);
        uint buffId = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);

        var data = services.GetRequiredService<GameDataStore>();

        
        
        
        var statue = services.GetRequiredService<NpcStateManager>().GetNpc(statueHandle);
        if (statue is null || statue.MapId != player.MapId || !GrantsRankerBuff(data, statue.NpcId))
        {
            
            
            
            Logger.LogWarning("[RankerBuff] {Name}: {Handle:X8} is not a ranking statue on map {Map}: refused with {Code}",
                player.Name, statueHandle, player.MapId, ResultNotAStatue);
            await SendAnswerAsync(session, buffId, ResultNotAStatue, ct);
            return;
        }
        if (!IsRankerBuff(data, buffId))
        {
            Logger.LogWarning("[RankerBuff] {Name} asked statue {Handle:X8} for {Buff}, which no statue grants",
                player.Name, statueHandle, buffId);
            return;
        }
        if (!TryGetBuffPrice(data, out long price))
        {
            Logger.LogWarning("[RankerBuff] Config_Def type {Type} (statue buff price) is missing or not a number",
                RankerBuffPriceConfigType);
            return;
        }

        
        
        
        var buffSkill = data.GetSkillLevel(buffId, 1);
        if (buffSkill is null || buffSkill.Effect_Duration <= 0f)
        {
            Logger.LogWarning("[RankerBuff] buff {Buff} has no Skill_Table_SkillLevel row with a duration", buffId);
            return;
        }
        ulong durationMs = (ulong)Math.Truncate(buffSkill.Effect_Duration) * 1000UL;

        if (!player.TryDebit(price))   
        {
            
            
            
            
            
            Logger.LogInformation("[RankerBuff] {Name} cannot afford buff {Buff}: {Gold} < {Price}: refused with {Code}",
                player.Name, buffId, player.Gold, price, ResultNotEnoughZeny);
            await SendAnswerAsync(session, buffId, ResultNotEnoughZeny, ct);
            return;
        }

        
        long newGold = player.Gold;
        await services.GetRequiredService<ICharacterRepository>().UpdateGoldAsync(player.CharacterId, newGold);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnZenyChange,
            ActorVitalsPackets.BuildZenyChange2435(player.EntityId, newGold), ct);

        var effects = services.GetRequiredService<EffectService>();

        
        
        foreach (var displaced in OtherActiveEffectsOfGroup(data, player, buffId))
            await effects.CancelPlayerEffectAsync(player, displaced);

        
        
        await effects.ApplyEffectToPlayerAsync(player, buffId, sourceSkillId: 0,
            casterHandle: player.EntityId, casterName: player.Name, durationMs: durationMs, effectValue: 0);

        
        
        await SendAnswerAsync(session, buffId, ResultOk, ct);

        Logger.LogInformation("[RankerBuff] {Name} bought buff {Buff} ({Ms} ms) from statue {Handle:X8} for {Price} -> {Gold}",
            player.Name, buffId, durationMs, statueHandle, price, newGold);
    }

 /// <summary>
 /// Builds the 2967 AnsRankerBuff reply: the echoed buff id and a 16-bit result code.
 /// </summary>
    internal static byte[] BuildAnsRankerBuff2967(uint buffId, ushort result)
    {
        var body = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(body, buffId);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), result);
        return body;
    }

    private static Task SendAnswerAsync(ProudSession session, uint buffId, ushort result, CancellationToken ct)
    {
        var body = BuildAnsRankerBuff2967(buffId, result);
        return session.SendRmiAsync(ZoneS2COpcodes.AnsRankerBuff, writer => writer.WriteBytes(body), ct);
    }

 /// <summary>Returns true when the NPC grants a ranking buff through a type-47 function slot.</summary>
    private static bool GrantsRankerBuff(GameDataStore data, uint npcId)
        => data.Npcs.TryGetValue(npcId, out var npc) && RankerBuffOf(npc) != 0;

 /// <summary>Returns true when some NPC's type-47 function slot names <paramref name="buffId"/>.</summary>
    private static bool IsRankerBuff(GameDataStore data, uint buffId)
    {
        if (buffId == 0) return false;
        foreach (var npc in data.Npcs.Values)
            if (RankerBuffOf(npc) == buffId) return true;
        return false;
    }

 /// <summary>Returns the Function_ID of the NPC's type-47 slot, or 0 when it has none.</summary>
    private static uint RankerBuffOf(NpcData npc)
    {
        if (npc.Function_Type_1 == RankerBuffFunctionType) return npc.Function_ID_1;
        if (npc.Function_Type_2 == RankerBuffFunctionType) return npc.Function_ID_2;
        if (npc.Function_Type_3 == RankerBuffFunctionType) return npc.Function_ID_3;
        return 0;
    }

    private static bool TryGetBuffPrice(GameDataStore data, out long price)
    {
        foreach (var row in data.ConfigDef)
        {
            if (row.type == RankerBuffPriceConfigType)
                return long.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out price)
                       && price >= 0;
        }
        price = 0;
        return false;
    }

 /// <summary>
 /// Returns the ids of the player's active effects that share the Effect_Table group of
 /// <paramref name="buffId"/>, excluding that buff. Empty when the buff has no group.
 /// </summary>
    private static List<uint> OtherActiveEffectsOfGroup(GameDataStore data, PlayerState player, uint buffId)
    {
        uint group = 0;
        foreach (var row in data.EffectTable)
            if (row.ID == buffId) { group = row.Group; break; }

        var result = new List<uint>();
        if (group == 0) return result;

        var sameGroup = new HashSet<uint>();
        foreach (var row in data.EffectTable)
            if (row.Group == group) sameGroup.Add(row.ID);

        lock (player.ActiveEffects)
        {
            foreach (var e in player.ActiveEffects)
                if (e.EffectId != buffId && sameGroup.Contains(e.EffectId) && !result.Contains(e.EffectId))
                    result.Add(e.EffectId);
        }
        return result;
    }
}
