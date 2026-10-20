using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Close-range chat room notifications: opening a room sign (2763), removing an actor's room state (2765), marking a member (2764), moving a room sign to a new master (2766), and updating the member count (2770).
/// </summary>
/// <remarks>
/// All five are sent unencrypted in the compact inner-body format (a compact length, then the inner size, the message tag and the payload).
/// </remarks>
public static class CloseRangeChatPackets
{
 /// <summary>Message tag of the open sign notification (2763).</summary>
    public const ushort MsgTagOpenSign = 58466;

 /// <summary>Message tag of the exit notification (2765).</summary>
    public const ushort MsgTagExitSign = 58468;

 /// <summary>Message tag of the join notification (2764).</summary>
    public const ushort MsgTagJoin = 58467;

 /// <summary>Message tag of the member count notification (2770).</summary>
    public const ushort MsgTagMemberCount = 58471;

 /// <summary>Message tag of the new master notification (2766).</summary>
    public const ushort MsgTagNewMaster = 58469;

 /// <summary>Length in characters of the room name buffer.</summary>
    public const int NameBufferChars = 51;

 /// <summary>Password flag value for a room without a password.</summary>
    public const byte SignNoPassword = 0;

 /// <summary>
 /// Password flag value for a room with a password. When set, clicking the sign asks the player for the password before joining.
 /// </summary>
    public const byte SignHasPassword = 1;

 /// <summary>
 /// Builds the open sign notification (2763). The sign shows the room name, owner, capacity and whether a password is required.
 /// </summary>
 /// <param name="ownerHandle">The owner's actor handle.</param>
 /// <param name="roomName">The room name, as a zero-padded UTF-16 buffer of <see cref="NameBufferChars"/> characters.</param>
 /// <param name="capacity">The room's capacity.</param>
 /// <param name="roomGuid">The 16-byte room identifier.</param>
 /// <param name="hasPassword">Whether the room has a password.</param>
    public static byte[] BuildNfyOpenCloseRangeChatRoom2763(uint ownerHandle, ReadOnlySpan<byte> roomName,
        byte capacity, ReadOnlySpan<byte> roomGuid, bool hasPassword = false)
    {
        if (roomName.Length != NameBufferChars * 2)
            throw new ArgumentException($"the room name is a wchar[{NameBufferChars}] ({NameBufferChars * 2} bytes); got {roomName.Length}", nameof(roomName));
        if (roomGuid.Length != 16)
            throw new ArgumentException($"a room GUID is 16 bytes; got {roomGuid.Length}", nameof(roomGuid));

        var name = roomName.ToArray();
        var guid = roomGuid.ToArray();
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagOpenSign);  
            p.WriteUInt32(ownerHandle);     
            p.WriteBytes(name);             
            p.WriteByte(hasPassword ? SignHasPassword : SignNoPassword); 
            p.WriteUInt32(ownerHandle);     
            p.WriteByte(capacity);          
            p.WriteBytes(guid);             
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds the exit notification (2765). It clears the room state of the given actor, which is the owner when the owner leaves.
 /// </summary>
    public static byte[] BuildNfyExitCloseRangeChatRoom2765(uint actorHandle)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagExitSign);  
            p.WriteUInt32(actorHandle);     
            p.WriteUInt32(actorHandle);     
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds the join notification (2764). It marks the given actor as a member of a room.
 /// </summary>
    public static byte[] BuildNfyJoinCloseRangeChatRoom2764(uint joinerHandle)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagJoin);       
            p.WriteUInt32(joinerHandle);     
            p.WriteUInt32(joinerHandle);     
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds the new master notification (2766). It places the room sign on its new owner after the previous owner left with members still in the room.
 /// </summary>
 /// <param name="newMasterHandle">The new owner's actor handle.</param>
 /// <param name="roomName">The room name, as a zero-padded UTF-16 buffer of <see cref="NameBufferChars"/> characters.</param>
 /// <param name="hasPassword">Whether the room has a password.</param>
 /// <param name="roomGuid">The 16-byte room identifier.</param>
    public static byte[] BuildNfyNewMasterCloseRangeChatRoom2766(uint newMasterHandle, ReadOnlySpan<byte> roomName,
        bool hasPassword, ReadOnlySpan<byte> roomGuid)
    {
        if (roomName.Length != NameBufferChars * 2)
            throw new ArgumentException($"the room name is a wchar[{NameBufferChars}] ({NameBufferChars * 2} bytes); got {roomName.Length}", nameof(roomName));
        if (roomGuid.Length != 16)
            throw new ArgumentException($"a room GUID is 16 bytes; got {roomGuid.Length}", nameof(roomGuid));

        var name = roomName.ToArray();
        var guid = roomGuid.ToArray();
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagNewMaster);   
            p.WriteUInt32(newMasterHandle);   
            p.WriteBytes(name);               
            p.WriteByte(hasPassword ? SignHasPassword : SignNoPassword); 
            p.WriteUInt32(newMasterHandle);   
            p.WriteBytes(guid);               
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds the member count notification (2770), which sets the capacity and member count shown on the owner's sign.
 /// </summary>
    public static byte[] BuildNfyUpdateJoinMemberCount2770(uint ownerHandle, byte capacity, byte memberCount)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagMemberCount); 
            p.WriteUInt32(ownerHandle);       
            p.WriteUInt32(ownerHandle);       
            p.WriteByte(capacity);            
            p.WriteByte(memberCount);         
        });
        return pw.ToArray();
    }
}
