using System.Text;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builders for the monster threat notifications: 2630 NfyAggroChanged and 2629 AnsAggroList.
/// </summary>
/// <remarks>
/// <para>
/// Neither packet has a compact-length envelope. The first body field is the monster handle, read directly as a u32.
/// </para>
/// <para>
/// Both packets share one body layout: [u32 monsterHandle][u32 currentTargetHandle][CompactLen count], followed by count entries of [u8 flag][u32 pcHandle][WStr name][u32 threat].
/// </para>
/// <para>
/// Names are sent as a Proud WStr: a width tag byte value 2, a compact character count, then UTF-16LE characters.
/// </para>
/// <para>
/// The current target is the monster's top threat target, or 0 when the list is empty. 2630 carries exactly one entry, and 2629 carries zero or more.
/// </para>
/// <para>
/// Both packets are broadcast to the players engaged with the monster, so every attacker's aggro meter stays in sync.
/// </para>
/// </remarks>
public static class AggroPackets
{
 /// <summary>
 /// One row of a monster's threat table: a player, the name shown on the aggro meter, and the accumulated threat.
 /// </summary>
 /// <param name="PcHandle">The player's actor handle.</param>
 /// <param name="PcName">The character name, sent as UTF-16LE.</param>
 /// <param name="Threat">The accumulated threat, which the client sorts the meter by.</param>
 /// <param name="Flag">Per-entry flag: 1 for almost all entries, 2 otherwise. Defaults to 1.</param>
    public readonly record struct AggroEntry(uint PcHandle, string PcName, uint Threat, byte Flag = 1);

 /// <summary>
 /// Writes a Proud WStr: a width tag value 2, the compact character count, then the UTF-16LE bytes.
 /// </summary>
 /// <remarks>
 /// The width tag 2 is used for every aggro entry name. The count is the number of characters, so the byte count is twice that.
 /// </remarks>
    private static void WriteProudWString(PacketWriter p, string value)
    {
        value ??= string.Empty;
        p.WriteByte(2);                       
        p.WriteCompactLength(value.Length);   
        if (value.Length > 0)
            p.WriteBytes(Encoding.Unicode.GetBytes(value));
    }

 /// <summary>
 /// Writes the shared aggro body: the monster handle, the current target, and the entries.
 /// </summary>
    private static void WriteAggroBody(PacketWriter p, uint monsterHandle,
        uint currentTargetHandle, IReadOnlyList<AggroEntry> entries)
    {
        p.WriteUInt32(monsterHandle);          
        p.WriteUInt32(currentTargetHandle);    
        p.WriteCompactLength(entries.Count);   
        foreach (var e in entries)
        {
            p.WriteByte(e.Flag);
            p.WriteUInt32(e.PcHandle);
            WriteProudWString(p, e.PcName);
            p.WriteUInt32(e.Threat);
        }
    }

 /// <summary>
 /// Gets the handle of the highest-threat entry, or 0 for an empty list.
 /// </summary>
 /// <remarks>
 /// Use this only when the caller has no better source. A current target that differs from the highest threat, such as one locked by taunt, should be passed explicitly when it is known.
 /// </remarks>
 /// <param name="entries">Threat rows.</param>
    public static uint TopThreatTarget(IReadOnlyList<AggroEntry> entries)
    {
        uint best = 0;
        uint bestThreat = 0;
        var first = true;
        foreach (var e in entries)
        {
            if (first || e.Threat > bestThreat)
            {
                best = e.PcHandle;
                bestThreat = e.Threat;
                first = false;
            }
        }
        return best;
    }

    
 /// <summary>
 /// Builds 2630 NfyAggroChanged, a single threat change for a monster.
 /// </summary>
 /// <remarks>
 /// Carries exactly one entry: the threat of one player on one monster. It is broadcast to the players engaged with the monster. The body has no compact envelope; the first byte is the low byte of the monster handle.
 /// </remarks>
 /// <param name="monsterHandle">The monster whose threat table changed.</param>
 /// <param name="currentTargetHandle">The monster's current top-threat target, or 0 if it has none.</param>
 /// <param name="pcHandle">The player whose threat changed.</param>
 /// <param name="pcName">That player's character name.</param>
 /// <param name="threat">The player's accumulated threat after the change.</param>
 /// <param name="changeFlag">Per-entry flag. Defaults to 1.</param>
    public static byte[] BuildAggroChanged2630(uint monsterHandle, uint currentTargetHandle,
        uint pcHandle, string pcName, uint threat, byte changeFlag = 1)
    {
        using var pw = new PacketWriter();
        WriteAggroBody(pw, monsterHandle, currentTargetHandle,
            new[] { new AggroEntry(pcHandle, pcName, threat, changeFlag) });
        return pw.ToArray();
    }

    
 /// <summary>
 /// Builds 2629 AnsAggroList, a monster's full threat table, as the answer to a client request.
 /// </summary>
 /// <remarks>
 /// The entries are written in the order given. The first entry is the current target, followed by the others in descending threat order. An empty list is valid, with a count of zero and a target of zero. The body has no compact envelope.
 /// </remarks>
 /// <param name="monsterHandle">The monster whose threat table this is.</param>
 /// <param name="currentTargetHandle">The monster's current top-threat target, or 0 when the list is empty.</param>
 /// <param name="entries">The threat rows, ideally in descending threat order.</param>
    public static byte[] BuildAggroList2629(uint monsterHandle, uint currentTargetHandle,
        IReadOnlyList<AggroEntry> entries)
    {
        using var pw = new PacketWriter();
        WriteAggroBody(pw, monsterHandle, currentTargetHandle, entries);
        return pw.ToArray();
    }

 /// <summary>
 /// Builds 2629 AnsAggroList, deriving the current target from the entries: the top-threat handle, or 0 when the list is empty.
 /// </summary>
 /// <param name="monsterHandle">The monster whose threat table this is.</param>
 /// <param name="entries">The threat rows.</param>
    public static byte[] BuildAggroList2629(uint monsterHandle, IReadOnlyList<AggroEntry> entries)
        => BuildAggroList2629(monsterHandle, TopThreatTarget(entries), entries);
}
