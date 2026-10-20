using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1530 ReqChallengeNextStage, the staged-arena player's "challenge this level" confirm.</summary>
/// <remarks>
/// <para>
/// Body: one u32, the actor handle of the arena stage NPC (NONE family, 4 bytes). The client sends it from
/// the NPC menu of the stage NPC 22110000 (NPCInfo Function_Type_1 = 34).
/// </para>
/// <para>
/// Message flow per level:
/// 1. C2S 1532 [npc] (NextStageLevelHandler).
/// 2. S2C 2753 NfyReadyOkNextLevelOnStage [u32 level][u32 npc], to the requester, shortly after.
/// The client shows a "level %d" prompt.
/// 3. C2S 1530 [npc], this message, when the player confirms.
/// 4. S2C 2755 NfyBeginLevelOnStage [u32 level][u32 Stage_Info.Guide_String_ID][u32 npc], a fraction of a
/// second later, to the requester and its party at the same client ms. The
/// level is 1..7, the guide string 9720 for level 1 and 9721 for levels 2..7. The client
/// looks the level up in its own Stage_Info and plays the level's intro (chat bubble, Cover_FX/SFX,
/// Intro_Image), plus the previous level's Clear_FX.
/// </para>
/// <para>
/// NO REPLY, on purpose. 2753 answers 1532, not 1530; the real answer to 1530, 2755,
/// announces that a level's fight starts, and this server does not run that fight. Each level summons
/// Stage_Info.Summon_ID1..10 after Delay_Time, and those ids point into a summon table that is
/// not part of the game data. Today the bosses of 410302/410303 all spawn at map
/// load with normal respawn, and nothing gates the next level on a clear, sends 2756 at the end, or
/// gives the party an instance. A 2755 alone would advance a counter that no fight gates: a player
/// could click through all 7 intros while every boss already stands in the arena. Silence is the
/// correct answer until a stage-dungeon system exists (a per-party run store, the summon table, per-level
/// 2317 spawns with no respawn, a clear hook on the kill path, 2756, instances).
/// </para>
/// </remarks>
public class ChallengeNextStageHandler : ZoneRmiHandlerBase<ChallengeNextStageHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqChallengeNextStage;
    public override string Name => "ChallengeNextStage";

    /// <summary>Request body length: one u32 actor handle of the stage NPC.</summary>
    private const int BodyLength = 4;

    public ChallengeNextStageHandler(IServiceScopeFactory s, ILogger<ChallengeNextStageHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < BodyLength)
        {
            Logger.LogWarning("Session {Id}: malformed ReqChallengeNextStage ({Len} bytes)", session.SessionId, payload.Length);
            return Task.CompletedTask;
        }

        var npcHandle = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span);
        Logger.LogInformation("{Name} confirmed the next level at stage NPC {Npc:X8}; stage dungeons are not modelled, no 2755 sent",
            player.Name, npcHandle);
        return Task.CompletedTask;
    }
}
