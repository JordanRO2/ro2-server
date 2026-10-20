using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// S2C body builders of the random-box roulette (1514 ReqRandomItemBatting -> 2713 AnsRandomItemInBoxList +
/// 2714 AnsRandomItemSelected, RandomItemBattingHandler).
/// </summary>
public static class RandomBoxPackets
{
 /// <summary>2714 result: the box was opened and <c>wonItemId</c> landed in the bag.</summary>
    public const ushort ResultOk = 0;

 /// <summary>2714 result 4: the player is flying, so the box cannot be used.</summary>
    public const ushort ResultFlying = 4;

 /// <summary>2714 result 23041: the bag has no empty space.</summary>
    public const ushort ResultBagFull = 23041;

 /// <summary>
 /// Server-defined 2714 result for a box that cannot be opened, such as a missing bag stack or an empty set. The client
 /// has no message for it, and the window closes.
 /// </summary>
    public const ushort ResultCannotOpen = 1;

 /// <summary>
 /// RMI 2714 AnsRandomItemSelected, 10 bytes, NONE envelope: [u32 won ItemInfo ID][u32 0][u16 result].
 /// </summary>
 /// <remarks>
 /// On result 0 the client starts the roulette spin on the won item. Every non-zero result closes the window. The
 /// second u32 is not read by the client.
 /// </remarks>
    public static byte[] Build2714(uint wonItemId, ushort result)
    {
        using var w = new PacketWriter(10);
        w.WriteUInt32(wonItemId); 
        w.WriteUInt32(0);         
        w.WriteUInt16(result);    
        return w.ToArray();
    }

 /// <summary>Bytes of one 2713 list element per element.</summary>
    public const int InBoxListElementBytes = 8;

 /// <summary>
 /// RMI 2713 AnsRandomItemInBoxList, U32 envelope: [u32 inner length = 9 + 8n][u16 result][5 zero bytes][u16 n]
 /// [n x {u32 ItemInfo ID, u32 0}].
 /// </summary>
 /// <remarks>
 /// The client fills the roulette with the listed items. The item won by 2714 must be one of them, since the spin
 /// stops on it.
 /// </remarks>
    public static byte[] Build2713(IReadOnlyList<uint> itemIds)
    {
        var n = (ushort)Math.Min(itemIds.Count, ushort.MaxValue);
        int inner = 2 + 5 + 2 + InBoxListElementBytes * n;
        using var w = new PacketWriter(4 + inner);
        w.WriteUInt32((uint)inner); 
        w.WriteUInt16(ResultOk);    
        w.WriteZeros(5);            
        w.WriteUInt16(n);           
        for (int i = 0; i < n; i++)
        {
            w.WriteUInt32(itemIds[i]); 
            w.WriteUInt32(0);          
        }
        return w.ToArray();
    }
}
