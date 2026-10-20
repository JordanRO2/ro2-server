using System.Buffers.Binary;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// RMI 2336 OnRepairItem, the builder for both repair triggers: the NPC repair window and a repair kit used from the bag.
/// </summary>
/// <remarks>
/// Body layout: [u32 innerLen][i16 result 0][u32 0][205-byte character head][u16 n][n x ItemRecord126]. A refusal
/// carries a result code instead. The character head is the live head: wallet, position, level, and the remaining
/// talent points.
/// </remarks>
internal static class RepairItemPackets
{
 /// <summary>2336 OnRepairItem.</summary>
    internal const ushort OnRepairItem2336 = ZoneS2COpcodes.OnRepairItem;

 /// <summary>Length of the character head that the 2336 body carries.</summary>
    internal const int CharacterHeadLength = 205;

    private const int HeadWalletOffset = 112;     

 /// <summary>
 /// Builds the whole 2336 body: [u32 innerLen][u16 result 0][u32 0][head 205][u16 n][n x 126].
 /// </summary>
 /// <param name="records">The 126-byte item records as they stand after the repair.</param>
 /// <param name="store">The game data, used for the remaining talent points in the head.</param>
    internal static byte[] BuildOnRepairItem2336(PlayerState player, IReadOnlyList<byte[]> records, GameDataStore store)
    {
        var head = BuildCharacterHead205(player, store);
        using var writer = new PacketWriter(4 + 2 + 4 + CharacterHeadLength + 2 + 126 * records.Count);
        var lenPos = writer.Reserve(4);
        var start = writer.Length;
        writer.WriteUInt16(0);                        
        writer.WriteUInt32(0);                        
        writer.WriteBytes(head);                      
        writer.WriteUInt16((ushort)records.Count);
        foreach (var r in records) writer.WriteBytes(r);
        writer.WriteAt(lenPos, (uint)(writer.Length - start));
        return writer.ToArray();
    }

 /// <summary>
 /// The 205-byte character head. With a Character snapshot it is the live head, carrying the live remaining talent
 /// points. Without a snapshot only the wallet is written.
 /// </summary>
    internal static byte[] BuildCharacterHead205(PlayerState player, GameDataStore store)
    {
        if (player.Character is { } ch)
        {
            AnsMapLoadingHandler.SyncLiveLevel(player, ch);
            var talentPoints = StatService.TalentPointsRemaining(store, player.Job, player.Level,
                player.LearnedSkills.Select(kv => (kv.Key, kv.Value)));
            return PcSaveDataPackets.BuildLiveHead205(player, ch, talentPoints);
        }
        var head = new byte[CharacterHeadLength];
        BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(HeadWalletOffset), (ulong)Math.Max(0, player.Gold));
        return head;
    }
}
