using System.Collections.Frozen;
using System.Text.Json;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Revival by another player's skill: which skills revive, the 2718 their landing sends, the 3016
/// NfyRevivalSkill offer to the dead target, and the burst that answers the target's 1684
/// AnsRevivalSkill.
/// </summary>
/// <remarks>
/// <b>The chain.</b>
/// Caster: C2S 1561 (item skill) or 1508 (learned skill) naming the dead PC at +16, then C2S 1509 with
/// the dead PC as primary target.
/// </remarks>
public static class RevivalSkillPackets
{
    
    
    
 /// <summary>Skill_Table_Base.Skill_Function of a revival skill.</summary>
    internal const int RevivalSkillFunction = 7;

 /// <summary>
 /// The file GameDataStore loads Skill_Table_Base from. Program.cs hands <c>LoadAsync</c> the
 /// relative directory "data", so this resolves against the same working directory.
 /// </summary>
    private static readonly string ShippedSkillTable = Path.Combine("data", "Skill_Table_Base.json");

    private static readonly Lazy<(FrozenSet<uint> Groups, string? Error)> ShippedGroups = new(LoadShippedGroups);

 /// <summary>
 /// True when <paramref name="skillId"/> (group*100+level) belongs to a revival skill group.
 /// </summary>
 /// <remarks>
 /// Read from the shipped Skill_Table_Base.Skill_Function, not from a list in code. The curated
 /// SkillData model does not carry Skill_Function, so the column is read from the same JSON the
 /// store loads, once.
 /// </remarks>
    public static bool IsRevivalSkill(uint skillId) => ShippedGroups.Value.Groups.Contains(skillId / 100);

 /// <summary>Why the revival-skill groups could not be read, or null when they were.</summary>
    public static string? SkillTableError => ShippedGroups.Value.Error;

 /// <summary>
 /// The Skill_Group of every row whose Skill_Function is <see cref="RevivalSkillFunction"/>.
 /// </summary>
    internal static FrozenSet<uint> ParseRevivalGroups(ReadOnlySpan<byte> skillTableJson)
    {
        var reader = new Utf8JsonReader(skillTableJson, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        using var doc = JsonDocument.ParseValue(ref reader);
        var groups = new HashSet<uint>();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (row.TryGetProperty("Skill_Function", out var fn) && fn.ValueKind == JsonValueKind.Number
                && fn.TryGetInt32(out var function) && function == RevivalSkillFunction
                && row.TryGetProperty("Skill_Group", out var g) && g.TryGetUInt32(out var group))
            {
                groups.Add(group);
            }
        }
        return groups.ToFrozenSet();
    }

    private static (FrozenSet<uint>, string?) LoadShippedGroups()
    {
        try
        {
            var groups = ParseRevivalGroups(File.ReadAllBytes(ShippedSkillTable));
            return groups.Count > 0
                ? (groups, null)
                : (groups, $"{ShippedSkillTable} has no Skill_Function {RevivalSkillFunction} row");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return (FrozenSet<uint>.Empty, $"{ShippedSkillTable} could not be read: {ex.Message}");
        }
    }

    
    
    
 /// <summary>blob+33 castType of a revival 2718. See <see cref="BuildRevivalSkillDamage2718"/>.</summary>
    internal const byte RevivalCastTypeRetail = 2;

 /// <summary>
 /// True when <paramref name="target"/> is a PC a revival skill can be offered to: dead, on the
 /// caster's map, and not the caster.
 /// </summary>
 /// <remarks>
 /// Same dead test as RevivalHandler (NpcCombatService writes HP 0 and IsDead together).
 /// </remarks>
    public static bool CanBeOffered(PlayerState? target, PlayerState caster) =>
        target is not null
        && target.SessionId != caster.SessionId
        && target.MapId == caster.MapId
        && (target.IsDead || target.Hp <= 0);

 /// <summary>Builds the 2718 NfySkillDamage frame for the landing of a revival skill.</summary>
    public static byte[] BuildRevivalSkillDamage2718(uint casterHandle, uint skillId, uint primaryTargetHandle,
        PlayerState? offeredTarget, byte castType)
    {
        var hit = offeredTarget is null
            ? new SkillDamageHit(0, 0, 0, Array.Empty<SkillHitEffect>())
            : new SkillDamageHit(
                offeredTarget.EntityId,
                (uint)Math.Max(0, offeredTarget.Hp),
                (uint)Math.Max(0, offeredTarget.Mp),
                Array.Empty<SkillHitEffect>());

        return SkillDamagePackets.BuildSkillDamage2718(
            casterHandle: casterHandle,
            skillId: skillId,
            primaryTargetHandle: primaryTargetHandle,
            hits: new[] { hit },
            x: 0f, y: 0f, z: 0f,
            castType: castType,
            skillEffectIndex: SkillDamagePackets.SkillEffectIndexPcModal);   
    }

    
    
    
 /// <summary>Body length of RMI 3016: a fixed 20-byte blob.</summary>
    internal const int NfyRevivalSkillBodyLen = 20;

 /// <summary>
 /// 3016 NfyRevivalSkill, the revival offer: <c>[f32 x][f32 y][f32 z][u32 casterHandle][u32
 /// skillId]</c>, raw, with no compact envelope.
 /// </summary>
 /// <remarks>Layout from the client and the wire together.</remarks>
    public static byte[] BuildNfyRevivalSkill3016(float x, float y, float z, uint casterHandle, uint skillId)
    {
        using var pw = new PacketWriter();
        pw.WriteSingle(x);
        pw.WriteSingle(y);
        pw.WriteSingle(z);
        pw.WriteUInt32(casterHandle);
        pw.WriteUInt32(skillId);
        return pw.ToArray();
    }

    
    
    
 /// <summary>Blob+19 state value of the state-0 RMI 2398 sent in the revival burst.</summary>
    internal const uint RevivedStateTrailer = 2;

 /// <summary>
 /// The four frames sent to a PC revived by RMI 1684, in order, to the revived PC and its onlookers.
 /// </summary>
 /// <param name="actorHandle">The revived PC.</param>
 /// <param name="x">
 /// The accepted offer's position (the caster's), which is where the PC comes back.
 /// </param>
 /// <param name="y">See <paramref name="x"/>.</param>
 /// <param name="z">See <paramref name="x"/>.</param>
 /// <param name="curHp">The PC's HP after the revival, which the caller has set to its max.</param>
 /// <param name="curSp">The PC's SP after the revival, likewise its max.</param>
 /// <param name="maxHp">The PC's max HP.</param>
 /// <param name="maxSp">The PC's max SP.</param>
    public static IReadOnlyList<(ushort Opcode, byte[] Body)> BuildRevivalBurst(
        uint actorHandle, float x, float y, float z, int curHp, int curSp, int maxHp, int maxSp)
    {
        return new (ushort, byte[])[]
        {
            (ZoneS2COpcodes.OnRevival, ActorStatePackets.BuildOnRevival(actorHandle, x, y, z)),
            (ZoneS2COpcodes.OnChangeState,
                CombatPackets.BuildChangeState2398(actorHandle, CombatPackets.ActorState.PcNormal, 0, RevivedStateTrailer)),
            (ZoneS2COpcodes.OnChangeActorMoveSpeed,
                TraitPackets.BuildActorMoveSpeed2497(actorHandle, TraitPackets.PcModalWorldEntrySpeed)),
            (ZoneS2COpcodes.OnActorAutoHeal,
                ActorVitalsPackets.BuildActorAutoHeal2401(actorHandle, curHp, curSp, maxHp, maxSp)),
        };
    }
}
