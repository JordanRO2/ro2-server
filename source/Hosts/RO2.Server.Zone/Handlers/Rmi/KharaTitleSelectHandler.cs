using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1438 ReqKharaTitleSelect - Select Khara title to display</summary>
/// <remarks>
/// Request outcomes: a request succeeds with 2582 result 0, and each success is answered with a 2592 about the
/// selector sent to the selector's OWN session before its 2582, plus the same 2592 to the other players around.
/// A refusal (2582 result 21751) carries no 2592 at all. A non-zero A or B is a Khara id with a Khara_Info row that
/// grants a title (for example Khara 572 -> Reward_Title_Id 206, or Khara 410 -> 155 on its second block); A = 0 clears slot A, and B is a Board 6 id.
/// Which requests are refused, and what 2582 u32@4 carries, is decided from the character's stored title state
/// (<see cref="KharaTitleStore.Decide"/>, where the rules are; the timer constant is
/// <see cref="KharaTitleStore.TitleTimerMs"/>), on the character's play clock
/// (<see cref="KharaTitleClock"/>: online only, stopped in the Colosseum).
/// <para>
/// A success also re-sends the owner's 2498 OnPcTrait between the 2592s and the 2582, the
/// sheet recomputed with the titles' stats (KharaTitleTraits); a refusal does not.
/// </para>
/// <para>
/// The stored pair also reaches the character records, as TITLE ids (KharaTitleStore.TitleOf): an accepted select
/// publishes it (KharaTitleStore.Publish) and every login does (KharaTitleStore.PreloadAsync in LoginPCHandler), and
/// the 2310 head (blob+143 A timer, +147 title A, +185 title B; WorldEntryService.WriteCharacterHead205) and the PC
/// record (optMask 0x0008 / 0x0010; PlayerSpawnService.WriteBaseBlock) read it from there.
/// </para>
/// </remarks>
public class KharaTitleSelectHandler : ZoneRmiHandlerBase<KharaTitleSelectHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqKharaTitleSelect;
    public override string Name => "KharaTitleSelect";
    public KharaTitleSelectHandler(IServiceScopeFactory s, ILogger<KharaTitleSelectHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // ReqKharaTitleSelect body (NONE-family, raw): U32 kharaA @0, U32 kharaB @4 (8 B). A shorter body is no
        // request: reading it as A = B = 0 would clear a worn title and start the
        // cooldown, so it is dropped without an answer, before any read or write of the stored state.
        if (!KharaTitleSelectPackets.TryParse1438(payload.Span, out var titleIdA, out var titleIdB))
        {
            Logger.LogWarning("{Name}: 1438 body of {Len} B is shorter than 8; ignored", player.Name, payload.Length);
            return;
        }
        Logger.LogInformation("{Name} selects Khara title A={A} B={B}", player.Name, titleIdA, titleIdB);

        var titles = services.GetRequiredService<KharaTitleStore>();
        KharaTitleDecision decision;
        KharaTitleState state = default;
        DateTime playNow = default;
        try
        {
            state = KharaTitleState.From(await titles.GetAsync(player.CharacterId, ct));
            // The titles the character may wear: the Kharas it completed on this server (the list KharaService keeps,
            // read from its store so the check sees what the 2310 would list) whose Khara_Info block grants a title
            // (KharaTitleStore.TitleGrantingKharas: Khara 410's title is on its second, NationEnable-4, block). This
            // ownership refusal is this server's own guard (KharaTitleStore).
            var khara = await services.GetRequiredService<IKharaStateRepository>().GetAsync(player.CharacterId, ct);
            var completed = (IReadOnlyCollection<uint>?)khara?.Completed ?? Array.Empty<uint>();
            var titleKharas = KharaTitleStore.TitleGrantingKharas(services.GetRequiredService<GameDataStore>().KharaInfo);
            // Both clocks are play time (KharaTitleClock: stopped while offline and on Map_Type 3, resumed at the login
            // and at each 1304 elsewhere), so the decision is taken at the character's play instant, the axis the
            // stored instants are on.
            playNow = KharaTitleClock.PlayNow(player.CharacterId, DateTime.UtcNow);
            decision = KharaTitleStore.Decide(state, titleIdA, titleIdB, playNow,
                id => KharaTitleStore.MayWear(id, completed, titleKharas));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without the stored state there is nothing to decide on: answer with the refusal shape, which
            // changes nothing on the client (no 2592), rather than grant a title the state might not allow.
            Logger.LogError(ex, "{Name}: could not read the Khara title state; refusing the select", player.Name);
            decision = new KharaTitleDecision(false, KharaTitleStore.CooldownMs, KharaTitleStore.ResultRefused, default,
                KharaTitleStore.SlotNotApplied, KharaTitleStore.SlotNotApplied);
        }

        if (decision.Accepted)
        {
            try
            {
                await titles.SaveAsync(player.CharacterId, decision.NewState, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Nothing was stored, so nothing may be published or announced: answer the refusal shape with the
                // timer of the state that is still stored (no 2592, no 2498); the next request works from that state.
                // A failed store has no defined client behavior; the refusal is the safe answer.
                Logger.LogError(ex, "{Name}: could not store the Khara title state; refusing the select", player.Name);
                decision = KharaTitleStore.SaveFailed(state, playNow);
            }
        }

        if (decision.Accepted)
        {
            // What the records of this PC carry from now on (the 2310 head of a later 2336 / 2391 / 2487 and every
            // 2504 / 2464 / 2465 other clients get), the same pair the 2592 tells the clients around, and the pair whose
            // stats the 2498 of AnswerAsync carries (KharaTitleTraits reads it from here).
            KharaTitleStore.Publish(player.CharacterId, decision.NewState, services.GetRequiredService<GameDataStore>().KharaInfo);
        }
        else
        {
            Logger.LogInformation("{Name}: Khara title select A={A} B={B} refused (T={T})",
                player.Name, titleIdA, titleIdB, decision.Timer);
        }

        await AnswerAsync(session, Broadcast, player, services, titleIdA, titleIdB, decision, Logger);
    }

    /// <summary>
    /// The answer to a decided 1438: on success the own 2592, the 2592 to the players around, the
    /// owner's 2498 recomputed with the titles already published (KharaTitleStore.Publish), then the 2582; on a refusal
    /// the 2582 alone.
    /// </summary>
    internal static async Task AnswerAsync(ProudSession session, BroadcastService broadcast, PlayerState player,
        IServiceProvider services, uint titleIdA, uint titleIdB, KharaTitleDecision decision, ILogger logger)
    {
        if (decision.Accepted)
        {
            // NfyKharaTitleChange (2592), 12 bytes: [u32 PC handle][u32 A]
            // [u32 B]. Each slot is the request's id, or 0xFFFFFFFF when that part of the request was not applied: A
            // while the A timer runs (a non-zero A, re-selected or different), B inside the 10 s cooldown
            // (KharaTitleStore.Decide has the rule). It is sent to the selector too, and
            // first, so the requester's own copy goes out before the broadcast and before 2582;
            // BroadcastToNearbyAsync skips the sender, so nobody gets it twice. Both carry the same 12 bytes.
            var titleChange = KharaTitleSelectPackets.Build2592(player.EntityId, decision);
            await session.SendRmiAsync(ZoneS2COpcodes.NfyKharaTitleChange, titleChange);
            await broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.NfyKharaTitleChange, w => w.WriteBytes(titleChange));

            // OnPcTrait (2498), the owner's sheet recomputed with the titles just published: it is sent between
            // the 2592s and the 2582 on every success (2592 -> 2498 -> 2582, a slot-A
            // 0xFFFFFFFF one included) and never on a refusal (the 2582 goes alone).
            // The titles' TraitInfo rows reach it through the sheet builder
            // (KharaTitleTraits); RefreshPcTraitSheet also sets the server max HP/SP and the combat cache from it.
            // Without the worn gear the sheet would drop every gear bonus, so it is skipped then, as
            // EffectService.SendPcTraitSheetAsync does.
            if (player.Character is { } character)
            {
                IReadOnlyList<RO2.Domain.Entities.InventoryItem>? worn = null;
                try
                {
                    worn = await services.GetRequiredService<RO2.Application.Interfaces.IInventoryRepository>()
                        .GetEquipmentAsync(player.CharacterId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "{Name}: could not read the worn equipment; no 2498 with this title select", player.Name);
                }
                if (worn is not null)
                    await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTrait,
                        services.GetRequiredService<EffectService>().RefreshPcTraitSheet(player, character, worn));
            }
        }

        // AnsKharaTitleSelect (2582), 16 bytes, sent last on success and alone on a refusal
        // (a refusal carries no 2592):
        //   [0..3]   U32 kharaA (echo of the request)
        //   [4..7]   U32 on success the A timer's remaining ms, on a refusal the cooldown's (KharaTitleStore.Decide)
        //   [8..11]  U32 kharaB (echo of the request)
        //   [12..15] U32 result: 0 on success, 21751 on a refusal
        await session.SendRmiAsync(ZoneS2COpcodes.AnsKharaTitleSelect, KharaTitleSelectPackets.Build2582(titleIdA, titleIdB, decision));
    }
}

/// <summary>The 2582 AnsKharaTitleSelect and 2592 NfyKharaTitleChange bodies.</summary>
internal static class KharaTitleSelectPackets
{
    /// <summary>1438 body: <c>[u32 kharaA @0][u32 kharaB @4]</c>, 8 bytes.</summary>
    internal const int RequestLength = 2 * sizeof(uint);

    /// <summary>Offset of the u32 kharaB (board id) in a 1438 body.</summary>
    private const int RequestKharaBOffset = sizeof(uint);

    /// <summary>2592 NfyKharaTitleChange body: <c>[u32 PC handle][u32 A slot][u32 B slot]</c>, 12 bytes.</summary>
    internal const int TitleChangeLength = 3 * sizeof(uint);

    /// <summary>2582 AnsKharaTitleSelect body: <c>[u32 A][u32 timer][u32 B][u32 result]</c>, 16 bytes.</summary>
    internal const int SelectAnswerLength = 4 * sizeof(uint);

    /// <summary>
    /// <c>[u32 PC handle][u32 A slot][u32 B slot]</c>, 12 bytes: the 2592 of
    /// an accepted 1438, the slots as <see cref="KharaTitleStore.Decide"/> set them (the request's id, or
    /// <see cref="KharaTitleStore.SlotNotApplied"/>). The handle's low byte is the PC tag 0x11.
    /// </summary>
    internal static byte[] Build2592(uint pcHandle, KharaTitleDecision decision)
    {
        using var w = new PacketWriter(TitleChangeLength);
        w.WriteUInt32(pcHandle);
        w.WriteUInt32(decision.SlotA);
        w.WriteUInt32(decision.SlotB);
        return w.ToArray();
    }

    /// <summary>
    /// The two u32 of a 1438 body, false when it is shorter than 8 bytes (a shorter one is
    /// not a request).
    /// </summary>
    internal static bool TryParse1438(ReadOnlySpan<byte> payload, out uint kharaA, out uint kharaB)
    {
        kharaA = kharaB = 0;
        if (payload.Length < RequestLength) return false;
        kharaA = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        kharaB = BinaryPrimitives.ReadUInt32LittleEndian(payload[RequestKharaBOffset..]);
        return true;
    }

    /// <summary><c>[u32 A][u32 T][u32 B][u32 result]</c>, 16 bytes.</summary>
    internal static byte[] Build2582(uint kharaA, uint kharaB, KharaTitleDecision decision)
    {
        using var w = new PacketWriter(SelectAnswerLength);
        w.WriteUInt32(kharaA);
        w.WriteUInt32(decision.Timer);
        w.WriteUInt32(kharaB);
        w.WriteUInt32(decision.Result);
        return w.ToArray();
    }
}
