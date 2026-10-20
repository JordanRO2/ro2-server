using System.Buffers.Binary;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builders and parsers for the dungeon finder: the 1555 and 1556 search requests and their answers
/// (2804, 2805), the in-progress state (2806), the match prompt (2808), the join notice (2824), the join failure
/// (2810) and the completion reward (2816). The messages are fixed-size and carry no msgTag.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DungeonFinderQueue"/> decides who is queued and matched, and
/// <see cref="DungeonFinderReward"/> handles the completion reward.
/// </para>
/// <para>
/// Role flags form a bit set: 1 tank, 2 dealer, 4 healer.
/// </para>
/// </remarks>
public static class DungeonFinderPackets
{
 /// Role flag for a tank.
    public const byte RoleTank = 1;

 /// Role flag for a damage dealer.
    public const byte RoleDealer = 2;

 /// Role flag for a healer.
    public const byte RoleHealer = 4;

 /// <summary>Result 0: the search was accepted.</summary>
    public const ushort ResultAccepted = 0;

 /// <summary>Result 9: the player already has a search in the queue.</summary>
    public const ushort ResultAlreadyRegistered = 9;

 /// <summary>Result 17: the character's level is not valid for the dungeon list.</summary>
    public const ushort ResultLevel = 17;

 /// <summary>
 /// Parses 1555 ReqPartyMatchingFindDungeon: <c>[u32 blobLen][u8 role flags][u16 count][count x u32 List_ID]</c>.
 /// Returns false for any other length.
 /// </summary>
    public static bool TryParseFindDungeon1555(ReadOnlySpan<byte> body, out byte roleFlags, out uint[] listIds)
    {
        roleFlags = 0;
        listIds = [];
        if (body.Length < 7) return false;
        var blobLen = BinaryPrimitives.ReadUInt32LittleEndian(body);
        if (blobLen != body.Length - 4) return false;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(body[5..]);
        if (count == 0 || blobLen != 3 + 4u * count) return false;
        roleFlags = body[4];
        listIds = new uint[count];
        for (int i = 0; i < count; i++)
            listIds[i] = BinaryPrimitives.ReadUInt32LittleEndian(body[(7 + 4 * i)..]);
        return true;
    }

 /// <summary>
 /// 2804 AnsPartyMatchingFindDungeon: a single u16 result code. Result 0 is silent on the client.
 /// </summary>
    public static byte[] BuildFindDungeonAnswer2804(ushort result)
    {
        var body = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        return body;
    }

 /// <summary>
 /// Parses 1556 ReqPartyMatchingFindDungeonRandom: <c>[u8 role flags][u8 Category_ID]</c>. Returns false for any
 /// other length.
 /// </summary>
    public static bool TryParseFindDungeonRandom1556(ReadOnlySpan<byte> body, out byte roleFlags, out byte categoryId)
    {
        roleFlags = 0;
        categoryId = 0;
        if (body.Length != 2) return false;
        roleFlags = body[0];
        categoryId = body[1];
        return true;
    }

 /// <summary>
 /// 2805 AnsPartyMatchingFindDungeonRandom: a single u16 result code, with the same codes as 2804.
 /// </summary>
    public static byte[] BuildFindDungeonRandomAnswer2805(ushort result) => BuildFindDungeonAnswer2804(result);

 /// Reason 3: the join did not go through.
    public const byte JoinFailMoveFailed = 3;

 /// <summary>
 /// 2810 NfyPartyMatchingFindDungeonJoin_Fail: a single byte <c>[u8 reason]</c>.
 /// </summary>
 /// <remarks>
 /// Clears the join prompt and the finding state on the client. This server forms only solo matches, so the
 /// group re-queue reason is never sent.
 /// </remarks>
    public static byte[] BuildJoinFail2810(byte reason) => [reason];

 /// <summary>
 /// 2806 AnsPartyMatchingInProgressInfo: <c>[u16 result 0][u32 List_ID][u8 healers filled][u8 dealers filled]
 /// [u8 tanks filled][u8 receiver's own role flag]</c>. Puts the client into the finding state and shows the
 /// filled role slots of the queue. Only result 0 is sent.
 /// </summary>
    public static byte[] BuildInProgressInfo2806(uint listId, DungeonFinderQueue.RoleCounts filled, byte ownRole)
    {
        using var pw = new PacketWriter(10);
        pw.WriteUInt16(0);             
        pw.WriteUInt32(listId);        
        pw.WriteByte(filled.Healers);  
        pw.WriteByte(filled.Dealers);  
        pw.WriteByte(filled.Tanks);    
        pw.WriteByte(ownRole);         
        return pw.ToArray();
    }

 /// <summary>
 /// 2808 NfyPartyMatchingFindDungeonStartJoinMessage: <c>[u32 List_ID][u8 assigned role flag][u8 reward-preview
 /// flag]</c>. The reward preview flag is always 0.
 /// </summary>
    public static byte[] BuildStartJoinMessage2808(uint listId, byte assignedRole)
    {
        using var pw = new PacketWriter(6);
        pw.WriteUInt32(listId);
        pw.WriteByte(assignedRole);
        pw.WriteByte(0);
        return pw.ToArray();
    }

 /// <summary>
 /// 2824 NfyDungeonFind_InProgress_JoinMsg: <c>[u32 List_ID][u8 healers][u8 dealers][u8 tanks]</c>, the members of
 /// the group that is going in.
 /// </summary>
    public static byte[] BuildInProgressJoinMsg2824(uint listId, DungeonFinderQueue.RoleCounts group)
    {
        using var pw = new PacketWriter(7);
        pw.WriteUInt32(listId);
        pw.WriteByte(group.Healers);
        pw.WriteByte(group.Dealers);
        pw.WriteByte(group.Tanks);
        return pw.ToArray();
    }

 /// Completion state: the reward is in the bag.
    public const byte CompleteGiftInBag = 2;

 /// Completion state: the reward did not fit the bag and was mailed.
    public const byte CompleteGiftMailed = 3;

 /// <summary>
 /// 2816 NfyRandomDungeonCompleteGift: <c>[u32 List_ID][u8 state]</c>. The state is
 /// <see cref="CompleteGiftInBag"/> or <see cref="CompleteGiftMailed"/>.
 /// </summary>
    public static byte[] BuildCompleteGift2816(uint listId, byte state = CompleteGiftInBag)
    {
        using var pw = new PacketWriter(5);
        pw.WriteUInt32(listId);
        pw.WriteByte(state);
        return pw.ToArray();
    }
}
