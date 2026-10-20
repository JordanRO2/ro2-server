using System.Buffers.Binary;
using System.Text;
using RO2.Domain.Entities;
using RO2.Network.Packets;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Asserts that <see cref="PlayerSpawnService.WriteOnPc2504"/> emits the OnPC_In (2504) record in the variable
/// wire form the client's deserializer reads: a compact-length framed list of records, each with a base block
/// followed by buff, skill and cooldown lists.
/// </summary>
/// <remarks>
/// The wire is not the flat in-memory record; that is the output of the deserializer. The test reads the emitted
/// bytes back in deserializer field order and checks that the wire is self-consistent and that the compact-length
/// value matches the inner byte count.
/// </remarks>
public class OnPcIn2504Tests
{
    private const uint SampleEntityId = 0x00100007u;
    private const ulong SampleCharUid = 0x0DE0B6B3A7F07304UL;
    private const int HeaderSize = 70;

    private static (PlayerState, Character) BuildSample()
    {
        var character = new Character
        {
            Name = "Remoto",
            Level = 12,
            Gender = 1,
            Race = 2,
            Job = 3,
            AppearanceJson = null, 
        };
        var player = new PlayerState
        {
            EntityId = SampleEntityId,
            CharacterUid = SampleCharUid,
            Name = "Remoto",
            Character = character,
            MapId = 1101,
            X = 209.34f,
            Y = 268.79f,
            Z = 48.23f,
            Rotation = 1.5f,
        };
        return (player, character);
    }

    private static byte[] Build(Character? overrideChar = null)
    {
        var (player, character) = BuildSample();
        if (overrideChar != null) { character = overrideChar; player.Character = character; player.Name = character.Name; }
        using var w = new PacketWriter();
        PlayerSpawnService.WriteOnPc2504(w, player, character, TestEmblems.Create());
        return w.ToArray();
    }

 /// <summary>Minimal cursor that reads little-endian primitives in the order the client's readers consume them.</summary>
    private sealed class Cursor
    {
        private readonly byte[] _b;
        public int Pos;
        public Cursor(byte[] b, int pos) { _b = b; Pos = pos; }
        public byte U8() => _b[Pos++];
        public ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(_b.AsSpan(Pos)); Pos += 2; return v; }
        public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(_b.AsSpan(Pos)); Pos += 4; return v; }
        public ulong U64() { var v = BinaryPrimitives.ReadUInt64LittleEndian(_b.AsSpan(Pos)); Pos += 8; return v; }
        public float F32() { var v = BinaryPrimitives.ReadSingleLittleEndian(_b.AsSpan(Pos)); Pos += 4; return v; }
        public ReadOnlySpan<byte> Raw(int n) { var s = _b.AsSpan(Pos, n); Pos += n; return s; }
        public string WStr(int chars) { var s = Encoding.Unicode.GetString(_b.AsSpan(Pos, chars * 2)); Pos += chars * 2; return s; }
    }

 /// <summary>Reads the outer compact length (a u8 count, then the little-endian value) and returns the value and the header bytes consumed.</summary>
    private static (uint value, int prefixLen) ReadCompactLength(byte[] blob)
    {
        int n = blob[0];
        uint value = n switch
        {
            1 => blob[1],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(1)),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(1)),
            _ => throw new Xunit.Sdk.XunitException($"unexpected compact-length N={n}"),
        };
        return (value, 1 + n);
    }

    [Fact]
    public void Emits_CompactLength_Preamble_Count_And_VariableRecord_SelfConsistent()
    {
        var blob = Build();

        
        var (declaredLen, prefixLen) = ReadCompactLength(blob);
        
        Assert.Equal((uint)(blob.Length - prefixLen), declaredLen);

        var cur = new Cursor(blob, prefixLen);

        
        
        
        
        Assert.Equal(blob.Length - prefixLen, cur.U16());        
        Assert.Equal(0xE3CE, cur.U16());                         
        Assert.Equal(SampleEntityId, cur.U32());                 

        
        
        Assert.Equal(1, cur.U16());

        
        int headerStart = cur.Pos;
        Assert.Equal(SampleEntityId, cur.U32());                 
        Assert.Equal(SampleCharUid, cur.U64());                  
        
        
        
        Assert.Equal(0, cur.U8());                               
        Assert.Equal(0, cur.U8());                               
        Assert.Equal(209.34f, cur.F32());                        
        Assert.Equal(268.79f, cur.F32());                        
        Assert.Equal(48.23f, cur.F32());                         
        Assert.Equal(1, cur.U8());                               
        Assert.Equal(2, cur.U8());                               
        Assert.Equal(0u, cur.U32());                             
        Assert.Equal(0u, cur.U32());                             
        Assert.Equal(0u, cur.U32());                             
        Assert.Equal(1, cur.U8());                               
        
        
        
        
        cur.Pos = headerStart + 57;
        Assert.Equal(6.0f, cur.F32());                           
        Assert.Equal(1.5f, cur.F32());                           
        
        
        Assert.Equal(ActorStatePackets.DetailTailModelClass, cur.U8());   
        Assert.Equal(0, cur.U8());                               
        Assert.Equal(Character.DefaultEquipHideFlag, cur.U16()); 
        
        cur.Pos = headerStart + HeaderSize;

        
        int nameLen = cur.U8();
        Assert.Equal("Remoto".Length, nameLen);
        Assert.Equal("Remoto", cur.WStr(nameLen));

        
        
        
        
        
        uint expectedJobBits = ((12u & 0x3Fu) << 21) | ((3u & 0x1Fu) << 16)
                             | ((3u & 0x0Fu) << 12) | ((1u & 0x3Fu) << 6);
        Assert.Equal(25374784u, expectedJobBits);
        Assert.Equal(expectedJobBits, cur.U32());

        
        
        
        
        
        const ushort OptRelation = 0x0200;
        Assert.Equal(OptRelation, cur.U16());                    
        Assert.Equal(2u, cur.U32());                             

        
        Assert.Equal(0, cur.U8());

        
        Assert.Equal(0, cur.U8());

        
        
        
        Assert.Equal(0, cur.U16());                              
        Assert.Equal(0, cur.U16());                              
        Assert.True(cur.Raw(4).ToArray().All(b => b == 0));      
        Assert.Equal(0, cur.U16());                              

        
        Assert.Equal(blob.Length, cur.Pos);
    }

    [Fact]
    public void Name_IsWireLengthPrefixed_NoTerminator()
    {
        var blob = Build();
        var (_, prefixLen) = ReadCompactLength(blob);
        
        int nameLenOff = prefixLen + 8 + 2 + HeaderSize;
        Assert.Equal((byte)"Remoto".Length, blob[nameLenOff]);
        var name = Encoding.Unicode.GetString(blob.AsSpan(nameLenOff + 1, "Remoto".Length * 2));
        Assert.Equal("Remoto", name);
        
        
        Assert.Equal(0x40, blob[nameLenOff + 1 + "Remoto".Length * 2]);
    }

    [Fact]
    public void NameLonger_Than21_IsClamped()
    {
        var (player, character) = BuildSample();
        character.Name = new string('X', 30);
        player.Name = character.Name;
        using var w = new PacketWriter();
        PlayerSpawnService.WriteOnPc2504(w, player, character, TestEmblems.Create());
        var blob = w.ToArray();

        var (declaredLen, prefixLen) = ReadCompactLength(blob);
        Assert.Equal((uint)(blob.Length - prefixLen), declaredLen);

        int nameLenOff = prefixLen + 8 + 2 + HeaderSize;
        Assert.Equal((byte)21, blob[nameLenOff]); 
        var written = Encoding.Unicode.GetString(blob.AsSpan(nameLenOff + 1, 21 * 2));
        Assert.Equal(new string('X', 21), written);
    }

    [Fact]
    public void Gender_Zero_IsCoercedToValidKharaValue()
    {
        var (player, character) = BuildSample();
        character.Gender = 0;
        using var w = new PacketWriter();
        PlayerSpawnService.WriteOnPc2504(w, player, character, TestEmblems.Create());
        var blob = w.ToArray();

        var (_, prefixLen) = ReadCompactLength(blob);
        int genderOff = prefixLen + 8 + 2 + 26; 
        Assert.Equal(1, blob[genderOff]);
    }

    [Fact]
    public void Appearance_IsTheStoredCreateBlock_Verbatim()
    {
        
        
        
        
        
        
        
        var raw = new byte[23];
        Convert.FromHexString("01020907ff00010000000906ff0002").CopyTo(raw, 0);
        raw[15] = 0x33; raw[19] = 0x44;   
        var (player, character) = BuildSample();
        character.Gender = 1;
        character.Race = 2;
        character.AppearanceJson = CharacterSerializer.ToJson(new CharacterAppearance { RawBytes = raw });

        using var w = new PacketWriter();
        PlayerSpawnService.WriteOnPc2504(w, player, character, TestEmblems.Create());
        var blob = w.ToArray();

        var (_, prefixLen) = ReadCompactLength(blob);
        int recStart = prefixLen + 8 + 2; 
        Assert.Equal("01020907ff00010000000906ff0002",
            Convert.ToHexString(blob.AsSpan(recStart + 26, 15)).ToLowerInvariant());
    }

    [Fact]
    public void EquipHideFlag_IsAtHeaderPlus67()
    {
        
        
        var (player, character) = BuildSample();
        character.EquipHideFlag = 882; 
        using var w = new PacketWriter();
        PlayerSpawnService.WriteOnPc2504(w, player, character, TestEmblems.Create());
        var blob = w.ToArray();
        var (_, prefixLen) = ReadCompactLength(blob);
        int recStart = prefixLen + 8 + 2;
        Assert.Equal((ushort)882, BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(recStart + 67)));
        Assert.Equal(0, blob[recStart + 69]);
    }

    [Fact]
    public void ReviewRefused_SetsOptBit8000()
    {
        
        
        var (player, character) = BuildSample();
        character.AllowAnyoneReview = false;
        using var w = new PacketWriter();
        PlayerSpawnService.WriteOnPc2504(w, player, character, TestEmblems.Create());
        var blob = w.ToArray();
        var (_, prefixLen) = ReadCompactLength(blob);
        int maskOff = prefixLen + 8 + 2 + HeaderSize + 1 + "Remoto".Length * 2 + 4;
        Assert.Equal(0x8200, BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(maskOff)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(maskOff + 2)));   
    }

    [Fact]
    public void EffectList_MatchesRetailRecords()
    {
        
        
        
        
        
        
        var now = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
        var player = new PlayerState { EntityId = SampleEntityId };
        player.ActiveEffects.Add(new ActiveEffect
        {
            EffectId = 40000, EffectValue = 1, StackCount = 1, SourceSkillId = 4000001,
            CasterHandle = 0x0b494011, ExpiresAt = null,
        });
        player.ActiveEffects.Add(new ActiveEffect
        {
            EffectId = 1117, EffectValue = 1000, StackCount = 1, SourceSkillId = 111705,
            CasterHandle = 0x0b494211, ExpiresAt = now.AddMilliseconds(1_790_896),
        });
        
        player.ActiveEffects.Add(new ActiveEffect { EffectId = 500, ExpiresAt = now.AddSeconds(-1) });

        using var w = new PacketWriter();
        PlayerSpawnService.WriteEffectList(w, player, now);
        Assert.Equal(
            "0200"
            + "5d040000e80301b0531b000000000059b401001142490b"
            + "409c0000010001000000000000000001093d001140490b",
            Convert.ToHexString(w.ToArray()).ToLowerInvariant());
    }

    [Fact]
    public void CompactLength_And_SelfLength_Both_Match_ActualBlobLength()
    {
        
        
        
        
        
        
        
        var blob = Build();
        var (declaredLen, prefixLen) = ReadCompactLength(blob);

        Assert.Equal((uint)(blob.Length - prefixLen), declaredLen);   
        var selfLen = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(prefixLen));
        Assert.Equal(blob.Length - prefixLen, selfLen);               
        Assert.Equal(declaredLen, (uint)selfLen);                     

        Assert.Equal(115u, declaredLen);
    }

    [Fact]
    public void Envelope_CarriesRetailsMsgTagAndActorHandle()
    {
        
        
        
        
        var blob = Build();
        var (_, prefixLen) = ReadCompactLength(blob);
        Assert.Equal(0xE3CE, BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(prefixLen + 2)));
        Assert.Equal(PlayerSpawnService.MsgTagOnPcIn,
            BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(prefixLen + 2)));
        Assert.Equal(SampleEntityId,
            BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(prefixLen + 4)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(prefixLen + 8)));
    }
}
