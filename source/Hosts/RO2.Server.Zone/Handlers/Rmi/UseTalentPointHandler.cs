using System.Buffers.Binary;
using RO2.Skills.Domain;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using RO2.Application.Interfaces;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1310 ReqPCUseTalentPoint (ByteArray) - Allocate a talent/stat point</summary>
public class UseTalentPointHandler : ZoneRmiHandlerBase<UseTalentPointHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCUseTalentPoint;
    public override string Name => "UseTalentPoint";

    /// <summary>msgTag at body+4 of a 1310 (0xE40E).</summary>
    internal const ushort MsgTag = 58382;

    /// <summary>
    /// Payload bytes before the first field of a 1310: the 0x01 marker, the 1-byte compact length, the u16 selfLen,
    /// the u16 msgTag and the u32 self PC handle (1 + 1 + 2 + 2 + 4).
    /// </summary>
    private const int EnvelopeLength = 10;

    /// <summary>Payload offset of the u16 msgTag.</summary>
    private const int MsgTagOffset = 4;

    /// <summary>Payload offset of the u32 self PC handle.</summary>
    private const int SelfHandleOffset = 6;

    /// <summary>Wire size of one {u32 skill group id, u8 level} element of the allocation vector.</summary>
    private const int AllocationElementSize = sizeof(uint) + sizeof(byte);

    /// <summary>
    /// Shortest payload TryParse accepts: the envelope, the u8 cost, the u32 element count and one element.
    /// </summary>
    private const int MinPayloadLength = EnvelopeLength + sizeof(byte) + sizeof(uint) + AllocationElementSize;

    /// <summary>Why <see cref="TryParse"/> refused a 1310 body.</summary>
    internal enum RequestError { None, TooShort, BadMsgTag, HandleMismatch, TooManyElements, Truncated }

    public UseTalentPointHandler(IServiceScopeFactory s, ILogger<UseTalentPointHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>
    /// Checks a 1310 body before any work is done on it and reads it: the msgTag at body+4 must be
    /// <see cref="MsgTag"/>, the self PC handle at body+6 must be <paramref name="selfHandle"/> (the sender's own
    /// entity id), the declared element count at body+11 at most <see cref="TalentRules.MaxTalentAllocations"/>
    /// and every declared {u32 id, u8 lvl} element present. <paramref name="allocations"/> comes back deduplicated by
    /// group (the last entry of a group wins) with the zero-group and zero-level entries dropped, which is exactly
    /// the set that is validated, persisted and echoed in the 2543.
    /// The handle, count and completeness checks are defensive rules against forged or corrupt frames.
    /// </summary>
    internal static RequestError TryParse(ReadOnlySpan<byte> payload, uint selfHandle,
        out byte cost, out uint count, out List<(uint Id, byte Level)> allocations)
    {
        cost = 0;
        count = 0;
        allocations = [];
        if (payload.Length < MinPayloadLength) return RequestError.TooShort;
        if (BinaryPrimitives.ReadUInt16LittleEndian(payload[MsgTagOffset..]) != MsgTag) return RequestError.BadMsgTag;
        if (BinaryPrimitives.ReadUInt32LittleEndian(payload[SelfHandleOffset..]) != selfHandle) return RequestError.HandleMismatch;

        var reader = new PacketReader(payload);
        reader.Skip(EnvelopeLength); // marker/env(6) + self handle(4)
        cost = reader.ReadByte();    // points the client says the change costs
        count = reader.ReadUInt32(); // element count k
        if (count > TalentRules.MaxTalentAllocations) return RequestError.TooManyElements;
        if (reader.Remaining < (long)AllocationElementSize * count) return RequestError.Truncated;

        var raw = new List<(uint Id, byte Level)>((int)count);
        for (uint i = 0; i < count; i++)
        {
            var id = reader.ReadUInt32();
            var lvl = reader.ReadByte();
            raw.Add((id, lvl));
        }
        allocations = DedupKeepLast(raw);
        return RequestError.None;
    }

    /// <summary>
    /// The request without its zero-group and zero-level entries (the handler persists neither) and with each group
    /// once, at the level of its LAST entry, in the order those last entries appeared.
    /// </summary>
    internal static List<(uint Id, byte Level)> DedupKeepLast(IReadOnlyList<(uint Id, byte Level)> raw)
    {
        var seen = new HashSet<uint>();
        var reversed = new List<(uint Id, byte Level)>(raw.Count);
        for (var i = raw.Count - 1; i >= 0; i--)
        {
            var (id, level) = raw[i];
            if (id == 0 || level == 0) continue;
            if (seen.Add(id)) reversed.Add((id, level));
        }
        reversed.Reverse();
        return reversed;
    }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // C2S ReqPCUseTalentPoint (1310) msgTag envelope: body+0 marker, body+4 msgTag(58382),
        // body+6 self PC handle (0x11), body+10 u8 cost, body+11 u32 element count k, then a stride-5
        // vec of {u32 id, u8 lvl} at body+15. TryParse checks the tag and handle, bounds the count and reads the
        // whole vector, then it is echoed back in the S2C 2543 reply below.
        // body+10 is the points the change costs: the 2543 blob+10 equals the pool before it minus that byte.
        // It is only logged; the pool sent back is derived from the stored tree below.
        var error = TryParse(payload.Span, player.EntityId, out var cost, out var count, out var allocations);
        if (error != RequestError.None)
        {
            // TooShort is the old silent return; the rest are forgeries or corruption worth a line.
            if (error != RequestError.TooShort)
                Logger.LogWarning("{Name}: malformed 1310 refused ({Error}, count={Count}); nothing persisted",
                    player.Name, error, count);
            return;
        }

        // PERSIST the allocation. Each entry is a { Skill_Group, level } pair (e.g. archer groups 3101/3120 at
        // level 1) — the same id space as learned/starter skills. The
        // vector holds only the skills this change touches, each at its NEW level, not the whole tree:
        // a vector may name a skill at level 2 and leave out others held at level 1, or name (4121, 5) for a cost
        // of 4 with 4121 already at level 1.
        // So each entry is upserted to its exact level and nothing absent from the vector is touched.
        // Without persisting, the tree (and the hotbar that references those skills) was empty on relog.
        // Mirrored into runtime LearnedSkills so the world-entry 2488 rebuilds the tree next login.
        // (Un-allocation/respec removal is not modelled yet. 1311 ReqPCInitialTalentPoint resets nothing:
        // InitialTalentPointHandler answers the current pool.)
        var store = services.GetRequiredService<GameDataStore>();

        // Validate-all, write once, then apply: the legality check, the single batched write and the memory mirror
        // run under the player's gate so two requests cannot validate against the same stale tree.
        byte remaining;
        await player.LearnedSkillsWriteGate.WaitAsync(ct);
        try
        {
            // Validate the requested allocation against the authoritative Talent_Table BEFORE persisting any
            // of it. A modified client could otherwise learn a skill outside its class's tree (junk or another
            // class's ids), raise one past its cap, take one below the level it unlocks at, buy one without its
            // prerequisite or lower a held one — all for free.
            // All-or-nothing: on any illegal entry, nothing is persisted and no 2543 is sent (the stored tree
            // is unchanged and the next world-entry 2488 rebuilds from it).
            if (allocations.Count > 0
                && !TalentRules.IsTalentAllocationLegal(store, player.Job, player.Level, allocations,
                    player.SnapshotLearnedSkills(), out var reason))
            {
                Logger.LogWarning("{Name}: illegal 1310 talent allocation refused ({Reason}); nothing persisted",
                    player.Name, reason);
                return;
            }

            if (allocations.Count > 0)
            {
                // ONE batched upsert (at most MaxTalentAllocations rows). The memory mirror is applied only after it
                // returns, so a failed write leaves LearnedSkills as it was and no 2543 goes out.
                try
                {
                    await services.GetRequiredService<ISkillRepository>()
                        .UpsertSkillsAsync(player.CharacterId, allocations, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.LogError(ex, "{Name}: 1310 talent batch write failed; nothing applied, no reply", player.Name);
                    return;
                }
                player.OverlayLearnedSkills(allocations);
            }

            // The client's 2543 handler SETS its counter to blob+10, so this is the pool left after the
            // change. It comes from the whole stored tree, as the relog 2310 body[142] and the reset 2544 do
            // (TalentRules.TalentPointsRemaining): level - 1 less the Talent_Table levels held, the starters'
            // first level free. Summing only the vector's levels would ignore everything bought before, and sending 0
            // would empty the pool after one spend.
            var tree = player.SnapshotLearnedSkills().Select(kv => (kv.Key, kv.Value)).ToArray();
            remaining = TalentRules.TalentPointsRemaining(store, player.Job, player.Level, tree);

            // The point budget is not a hard gate (the earned pool this server models is known to undercount
            // for some characters, see TalentRules.IsTalentAllocationLegal), so an over-spend is only logged;
            // the pool reported to the client is already clamped to zero by TalentPointsRemaining.
            int spent = TalentRules.TalentPointsSpent(store, player.Job, tree);
            int earned = Math.Max(0, player.Level - 1);
            if (spent > earned)
                Logger.LogWarning("{Name}: talent tree costs {Spent} points, over the earned {Earned} at level {Level}",
                    player.Name, spent, earned, player.Level);
        }
        finally
        {
            player.LearnedSkillsWriteGate.Release();
        }

        Logger.LogDebug("{Name} uses talent point: count={Count}, cost={Cost}, remaining={Remaining}",
            player.Name, count, cost, remaining);

        // ENVELOPE FIX + DERIVE. OnUseTalentPoint 2543 is a COMPACT-BA opcode (msgTag 58383, marker
        // 0x01), VARIABLE length. BuildUseTalentPoint2543 echoes the applied allocation
        // vector inside a correct compact-BA envelope and carries the remaining total at blob+10.
        await session.SendRmiAsync(ZoneS2COpcodes.OnUseTalentPoint,
            writer => writer.WriteBytes(QuestProgressPackets.BuildUseTalentPoint2543(allocations, remaining)));
    }
}
