using System.Collections.Generic;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builders for the monster and pet collection packets. Each body is a raw frame without a compact wrapper or message tag, and lengths are fixed so the client reads exactly what is sent.
/// </summary>
/// <remarks>
/// Monster records are keyed by the monster's NPC id and carry its skill, summon level and experience. Pet records carry the pet id, its loyalty effect, loyalty level and loyalty value. The collection header carries the collect-rate bucket of the registered count.
/// </remarks>
public static class CollectionPackets
{
    
 /// <summary>
 /// Builds the monster collection list (2911): the collect-rate header, then one record per registered collection, then a zero trailer.
 /// </summary>
    public static byte[] BuildAnsMCollection(IReadOnlyList<MonsterCollectionState> registered, CollectRateHeader rate)
    {
        using var w = new PacketWriter(32 + registered.Count * 13);
        WriteMcollectionHeader(w, registered.Count, rate);
        w.WriteCompactLength(registered.Count);
        foreach (var r in registered)
        {
            w.WriteUInt32(r.NpcId);
            w.WriteUInt32(r.SkillId);
            w.WriteByte(r.Level);
            w.WriteUInt32(r.Exp);
        }
        w.WriteUInt16(0); 
        return w.ToArray();
    }

 /// <summary>
 /// Builds the monster collection header alone (2913), sent after a collection changes.
 /// </summary>
    public static byte[] BuildNfyMCollection(int count, CollectRateHeader rate)
    {
        using var w = new PacketWriter(10);
        WriteMcollectionHeader(w, count, rate);
        return w.ToArray();
    }

    private static void WriteMcollectionHeader(PacketWriter w, int count, CollectRateHeader rate)
    {
        w.WriteByte(rate.Index);        
        w.WriteByte(rate.SkillMax);     
        w.WriteUInt32((uint)count);     
        w.WriteUInt32(rate.CooltimeMs); 
    }

    
 /// <summary>
 /// Builds the pet collection list (2916): the registered pet count, then one record per pet, then a zero trailer.
 /// </summary>
    public static byte[] BuildAnsPCollection(IReadOnlyList<PetCollectionState> records)
    {
        using var w = new PacketWriter(16 + records.Count * 11);
        w.WriteUInt32((uint)records.Count); 
        w.WriteCompactLength(records.Count);
        foreach (var r in records)
            WritePetRecord(w, r);
        w.WriteUInt16(0); 
        return w.ToArray();
    }

 /// <summary>
 /// Builds the registered pet count (2919), sent at login, on warp and after a pet is added.
 /// </summary>
    public static byte[] BuildNfyPCollection(int count)
    {
        using var w = new PacketWriter(4);
        w.WriteUInt32((uint)count);
        return w.ToArray();
    }

 /// <summary>The 11-byte pet record used in the pet list and in the pet selected-skill packets.</summary>
    private static void WritePetRecord(PacketWriter w, PetCollectionState state)
    {
        w.WriteUInt32(state.PetId);      
        w.WriteUInt32(state.SkillId);    
        w.WriteByte(state.LoyaltyLevel); 
        w.WriteUInt16(state.Loyalty);    
    }

    
 /// <summary>
 /// Writes the 21-byte monster selected-skill record shared by 2912 and 2915: the monster, its skill, summon level, experience and the four DNA piece counts.
 /// </summary>
    private static void WriteMonsterSkillRecord(PacketWriter w, MonsterCollectionState state, PieceCounts pieces)
    {
        w.WriteUInt32(state.NpcId);   
        w.WriteUInt32(state.SkillId); 
        w.WriteByte(state.Level);     
        w.WriteUInt32(state.Exp);     
        w.WriteUInt16(pieces.Piece1); 
        w.WriteUInt16(pieces.Piece2); 
        w.WriteUInt16(pieces.Piece3); 
        w.WriteUInt16(pieces.Piece4); 
    }

 /// <summary>
 /// Builds the answer to a monster selected-skill request (2912): the 21-byte record followed by the result code.
 /// </summary>
    public static byte[] BuildAnsMCollectionSelectedSkill(MonsterCollectionState state, PieceCounts pieces, ushort result)
    {
        using var w = new PacketWriter(23);
        WriteMonsterSkillRecord(w, state, pieces);
        w.WriteUInt16(result);
        return w.ToArray();
    }

 /// <summary>
 /// Builds the unprompted refresh of one monster collection's piece counts (2915). It is sent after a DNA combine and after a box grants DNA pieces.
 /// </summary>
    public static byte[] BuildNfyMCollectionSelectedSkill(MonsterCollectionState state, PieceCounts pieces)
    {
        using var w = new PacketWriter(21);
        WriteMonsterSkillRecord(w, state, pieces);
        return w.ToArray();
    }

 /// <summary>
 /// Builds the new state of one monster collection (2914) after a DNA is used: NPC id, skill, summon level and experience.
 /// </summary>
    public static byte[] BuildNfyMCollectionSkill(MonsterCollectionState state)
    {
        using var w = new PacketWriter(13);
        w.WriteUInt32(state.NpcId);
        w.WriteUInt32(state.SkillId);
        w.WriteByte(state.Level);
        w.WriteUInt32(state.Exp);
        return w.ToArray();
    }

    
 /// <summary>
 /// Writes the 19-byte pet selected-skill record shared by 2917 and 2921: the pet, its loyalty effect, level, loyalty and the four DNA piece counts.
 /// </summary>
    private static void WritePetSkillRecord(PacketWriter w, PetCollectionState state, PieceCounts pieces)
    {
        WritePetRecord(w, state);          
        w.WriteUInt16(pieces.Piece1);      
        w.WriteUInt16(pieces.Piece2);      
        w.WriteUInt16(pieces.Piece3);      
        w.WriteUInt16(pieces.Piece4);      
    }

 /// <summary>
 /// Builds the answer to a pet selected-skill request (2917): the 19-byte record followed by the result code.
 /// </summary>
    public static byte[] BuildAnsPCollectionSelectedSkill(PetCollectionState state, PieceCounts pieces, ushort result)
    {
        using var w = new PacketWriter(21);
        WritePetSkillRecord(w, state, pieces);
        w.WriteUInt16(result);
        return w.ToArray();
    }

 /// <summary>
 /// Builds the unprompted refresh of one pet collection's piece counts (2921). It is sent after an egg is crafted.
 /// </summary>
    public static byte[] BuildNfyPCollectionSelectedSkill(PetCollectionState state, PieceCounts pieces)
    {
        using var w = new PacketWriter(19);
        WritePetSkillRecord(w, state, pieces);
        return w.ToArray();
    }

 /// <summary>
 /// Builds the state of one pet (2920), sent when a hatched egg changes that pet's record.
 /// </summary>
    public static byte[] BuildNfyPCollectionSkill(PetCollectionState state)
    {
        using var w = new PacketWriter(11);
        WritePetRecord(w, state);
        return w.ToArray();
    }

    
 /// <summary>
 /// Builds the answer to a request to set the active pet (2918): the pet id, its loyalty level and the result code. No active pet is sent as id 0.
 /// </summary>
    public static byte[] BuildAnsPCollectionSetActivePet(uint petCollectionId, byte loyaltyLevel, ushort result)
    {
        using var w = new PacketWriter(7);
        w.WriteUInt32(petCollectionId);
        w.WriteByte(loyaltyLevel);
        w.WriteUInt16(result);
        return w.ToArray();
    }

    
 /// <summary>
 /// Builds a result acknowledgement with an id and result code (2922, 2923, 2924). These are used for combining proteins, using a protein and combining an egg.
 /// </summary>
    public static byte[] BuildCollectionIdResultAck(uint id, ushort result)
    {
        using var w = new PacketWriter(6);
        w.WriteUInt32(id);
        w.WriteUInt16(result);
        return w.ToArray();
    }

 /// <summary>Builds the answer to a pet egg use request (2925): the id, a flag and the result code.</summary>
    public static byte[] BuildAnsPCollectionUsePetEgg(uint id, byte flag, ushort result)
    {
        using var w = new PacketWriter(7);
        w.WriteUInt32(id);
        w.WriteByte(flag);
        w.WriteUInt16(result);
        return w.ToArray();
    }
}

/// <summary>State of one monster collection: NPC id, the skill of its current summon level, the level, and the experience toward the next level.</summary>
public readonly record struct MonsterCollectionState(uint NpcId, uint SkillId, byte Level, uint Exp);

/// <summary>State of one pet collection: pet id, the loyalty effect of the reached loyalty level, that level, and the loyalty value.</summary>
public readonly record struct PetCollectionState(uint PetId, uint SkillId, byte LoyaltyLevel, ushort Loyalty);

/// <summary>Bag counts of a collection's four DNA pieces.</summary>
public readonly record struct PieceCounts(ushort Piece1, ushort Piece2, ushort Piece3, ushort Piece4);

/// <summary>Collect-rate bucket carried by the monster collection header.</summary>
public readonly record struct CollectRateHeader(byte Index, byte SkillMax, uint CooltimeMs);
