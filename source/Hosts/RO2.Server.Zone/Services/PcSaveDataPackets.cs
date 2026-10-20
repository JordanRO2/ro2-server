using System.Buffers.Binary;
using RO2.Domain.Entities;
using RO2.Network.Packets;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// RMI 2487 (OnUpdatePCSaveData): re-sends the character's base record so the client's copy matches the
/// server after a change, such as a rename.
/// </summary>
internal static class PcSaveDataPackets
{
 /// <summary>Message tag of RMI 2487.</summary>
    internal const ushort MsgTagPcSaveData = 58261;

 /// <summary>Length of the character base record carried by RMI 2487, in bytes.</summary>
    internal const int CharacterHeadLength = 205;

    private const int HeadLevelOffset = 69;     
    private const int HeadBaseExpOffset = 70;   
    private const int HeadCurHpOffset = 81;     
    private const int HeadCurSpOffset = 85;     
    private const int HeadWalletOffset = 112;   
    private const int HeadBindIndexOffset = 105; 
    private const int HeadFacingOffset = 107;   

 /// <summary>Builds a 2487 body for the given PC handle and character base record.</summary>
    internal static byte[] BuildOnUpdatePCSaveData2487(uint pcHandle, byte[] head205)
    {
        if (head205.Length != CharacterHeadLength)
            throw new ArgumentException($"2487 carries the {CharacterHeadLength}-byte 2310 head; got {head205.Length}.", nameof(head205));
        using var pw = new PacketWriter(3 + 4 + 4 + CharacterHeadLength);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagPcSaveData); 
            p.WriteUInt32(pcHandle);         
            p.WriteBytes(head205);           
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds the character base record from the player's live state: the base character fields with the live map,
 /// position, level, experience, HP, SP and wallet.
 /// </summary>
    internal static byte[] BuildLiveHead205(PlayerState player, Character character, byte talentPointsRemaining)
    {
        using var writer = new PacketWriter(CharacterHeadLength + 16);
        WorldEntryService.WriteCharacterHead205(writer, character, player.CharacterUid, player.EntityId,
            player.MapId, player.X, player.Y, player.Z, talentPointsRemaining);
        var head = writer.ToArray();
        var span = head.AsSpan();
        if (player.Level != 0) head[HeadLevelOffset] = player.Level;
        BinaryPrimitives.WriteUInt64LittleEndian(span[HeadBaseExpOffset..], PlayerDeathPackets.BaseExpForHead(player));
        BinaryPrimitives.WriteUInt32LittleEndian(span[HeadCurHpOffset..], (uint)Math.Max(0, player.Hp));
        BinaryPrimitives.WriteUInt32LittleEndian(span[HeadCurSpOffset..], (uint)Math.Max(0, player.Mp));
        BinaryPrimitives.WriteUInt64LittleEndian(span[HeadWalletOffset..], (ulong)Math.Max(0, player.Gold));
        
        
        
        BinaryPrimitives.WriteUInt16LittleEndian(span[HeadBindIndexOffset..], player.BindIndex);
        head[HeadFacingOffset] = WorldEntryService.FacingByte(player.FacingYaw);
        return head;
    }
}
