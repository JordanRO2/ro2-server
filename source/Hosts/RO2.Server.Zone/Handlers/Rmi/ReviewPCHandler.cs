using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1696 (ReqReviewPC), which lets a player inspect another online player. The reply is 3033
/// AnsReviewPC.
/// </summary>
/// <remarks>
/// The request body is the target's u32 actor handle. When reviews are allowed for the target, the reply
/// carries its character record, active effects, worn gear and stat sheet. Otherwise the reply has no data.
/// No reply is sent when the target is not an online player.
/// </remarks>
public class ReviewPCHandler : ZoneRmiHandlerBase<ReviewPCHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqReviewPC;
    public override string Name => "ReviewPC";

    public ReviewPCHandler(IServiceScopeFactory s, ILogger<ReviewPCHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>Length of the stat block that ends a full 3033 reply.</summary>
    private const int StatBlockLength = 112;

 /// <summary>Maps each field of the 112-byte stat block to its offset and width in the 2498 trait blob.</summary>
    private static readonly (int Block, int Blob, int Width)[] StatBlockFromPcTrait =
    {
        (36, 86, 2),                                        
        (38, 26, 2), (40, 28, 2), (42, 30, 2), (44, 32, 2), (46, 34, 2), 
        (48, TraitPackets.PcTraitMaxHpOffset, 4),           
        (56, TraitPackets.PcTraitMaxSpOffset, 2),           
        (60, 40, 2),                                        
        (62, 44, 2),                                        
        (64, 98, 2), (66, 96, 2), (68, 88, 2), (70, 92, 2), 
        (72, 128, 2),                                       
        (74, 50, 2),                                        
        (76, 90, 2), (78, 94, 2),                           
        (80, 132, 2),                                       
        (82, 100, 2), (84, 102, 2),                         
        (86, 14, 2), (88, 16, 2), (90, 18, 2), (92, 20, 2), (94, 22, 2), 
        (96, 58, 2), (98, 84, 2),
        (102, 62, 2), (106, 60, 2), (110, 130, 2),
    };

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 4)
        {
            Logger.LogWarning("{Name}: short 1696 body ({Len} B, retail is 4)", player.Name, payload.Length);
            return;
        }
        var targetHandle = new PacketReader(payload.Span).ReadUInt32();
        var target = PlayerManager.GetAllPlayers().FirstOrDefault(p => p.EntityId == targetHandle);

        
        if (target is null || target.Character is null)
        {
            Logger.LogDebug("{Name} reviews {Handle:X8}: no such player online, no answer", player.Name, targetHandle);
            return;
        }

        byte[]? data = null;
        if (!ReviewPermission.IsAllowed(target.CharacterId))
            Logger.LogDebug("{Name} reviews {Target}: reviews not allowed", player.Name, target.Name);
        else
            data = await BuildReviewDataAsync(target, services);

        var body = BuildAnsReviewPC3033(data);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsReviewPC, writer => writer.WriteBytes(body));
    }

 /// <summary>
 /// Builds the 3033 body: <c>[u32 length = 3 + data length][u16 result 0][u8 hasData]</c> followed by
 /// <paramref name="data"/>. With null data the body has no payload.
 /// </summary>
    internal static byte[] BuildAnsReviewPC3033(byte[]? data)
    {
        using var w = new PacketWriter();
        w.WriteUInt32((uint)(3 + (data?.Length ?? 0))); 
        w.WriteUInt16(0);                               
        w.WriteByte(data is null ? (byte)0 : (byte)1);  
        if (data is not null) w.WriteBytes(data);
        return w.ToArray();
    }

 /// <summary>
 /// Assembles the data part of a full 3033 reply in the order the client reads it: the PC record, the active
 /// effects <c>[u16 n][n x 23]</c>, the worn gear <c>[u16 m][m x 126]</c>, then the stat block.
 /// </summary>
    internal static byte[] AssembleReviewData(ReadOnlySpan<byte> pcRecord, PlayerState target,
        IReadOnlyList<byte[]> gearRecords126, ReadOnlySpan<byte> statBlock, DateTime nowUtc)
    {
        using var w = new PacketWriter();
        w.WriteBytes(pcRecord);
        PlayerSpawnService.WriteEffectList(w, target, nowUtc);
        w.WriteUInt16((ushort)gearRecords126.Count);
        foreach (var rec in gearRecords126)
            w.WriteBytes(rec);
        w.WriteBytes(statBlock);
        return w.ToArray();
    }

 /// <summary>Offset of the 2310 final tail inside the stat block.</summary>
    internal const int StatBlockFinalTailOffset = 6;

 /// <summary>Length of the 2310 final tail repeated in the stat block.</summary>
    internal const int FinalTailLength = 30;

 /// <summary>
 /// Builds the 112-byte stat block of a full 3033 reply from the target's 2498 trait blob and its current HP
 /// and MP, clamped to the sheet's maximums.
 /// </summary>
 /// <remarks>
 /// Bytes +6 to +35 repeat the target's 2310 final tail, whose model bytes carry the model class. The bytes
 /// with no known source are zero.
 /// </remarks>
    internal static byte[] BuildStatBlock(ReadOnlySpan<byte> pcTraitBlob, long hp, int mp)
    {
        var stats = new byte[StatBlockLength];
        foreach (var (at, src, width) in StatBlockFromPcTrait)
            pcTraitBlob.Slice(src, width).CopyTo(stats.AsSpan(at));
        
        stats[StatBlockFinalTailOffset + 4] = ActorStatePackets.DetailTailModelClass;
        stats[StatBlockFinalTailOffset + 5] = ActorStatePackets.DetailTailModelClass;
        uint maxHp = BinaryPrimitives.ReadUInt32LittleEndian(pcTraitBlob[TraitPackets.PcTraitMaxHpOffset..]);
        ushort maxSp = BinaryPrimitives.ReadUInt16LittleEndian(pcTraitBlob[TraitPackets.PcTraitMaxSpOffset..]);
        BinaryPrimitives.WriteUInt32LittleEndian(stats.AsSpan(52), (uint)Math.Clamp(hp, 0, maxHp));
        BinaryPrimitives.WriteUInt16LittleEndian(stats.AsSpan(58), (ushort)Math.Clamp(mp, 0, maxSp));
        return stats;
    }

 /// <summary>Builds the data part of a full 3033 reply for a target, or null when it cannot be built.</summary>
    private async Task<byte[]?> BuildReviewDataAsync(PlayerState target, IServiceProvider services)
    {
        var character = target.Character!;
        var worn = await services.GetRequiredService<IInventoryRepository>().GetEquipmentAsync(target.CharacterId);
        var store = services.GetRequiredService<GameDataStore>();

        
        
        
        
        using var rec = new PacketWriter();
        PlayerSpawnService.WriteBaseBlock(rec, target, character, services.GetRequiredService<IGuildEmblemRepository>(), mountId: null);

        
        
        var trait = services.GetRequiredService<EffectService>().BuildInWorldPcTrait(target, character, worn);
        var blob = trait.AsSpan(3); 
        if (blob.Length < TraitPackets.PcTraitBlobLen)
        {
            Logger.LogError("ReviewPC: 2498 blob is {Len} bytes, expected {Want}", blob.Length, TraitPackets.PcTraitBlobLen);
            return null;
        }

        var stats = BuildStatBlock(blob, target.Hp, target.Mp);

        
        var gear = worn.Where(i => i.StorageType == 1).Select(WorldEntryService.BuildItemRecord126).ToList();
        return AssembleReviewData(rec.GetWrittenSpan(), target, gear, stats, DateTime.UtcNow);
    }
}
