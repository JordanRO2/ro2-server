using RO2.Domain.Entities;
using System.Text;
using RO2.Network.Packets;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Characters.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-identity gate for the 2310 OnCharacterDetail builder. <see cref="WorldEntryService.WriteCharacterDetail"/>
/// must produce exactly the bytes of the reference template <see cref="WriteCharacterDetailRaw"/>
/// for identical inputs. A single wrong byte on this spawn path breaks the client.
/// </summary>
/// <remarks>
/// The fixture character uses fixed values for UID, entity id, name, appearance, level, experience, class,
/// position, map, Khara points, job, job level and master level, so any structural drift shows up as a byte diff.
/// The shared fixture keeps job, job level and master level at 0 for the no-class fallback tests; the identity
/// test sets its own job block (<see cref="DonorJobBlockCharacter"/>).
/// </remarks>
public class WorldEntry2310Tests
{
    private const ulong DonorCharUid = 0x0DE0B6B3A7F07304UL;
    private const uint DonorEntityId = 0x0137B811u;
    private const uint DonorMapId = 1101u;

    
    
    private static readonly float DonorPosX = BitConverter.Int32BitsToSingle(unchecked((int)0x43514CCD));
    private static readonly float DonorPosY = BitConverter.Int32BitsToSingle(unchecked((int)0x4386E51F));
    private static readonly float DonorPosZ = BitConverter.Int32BitsToSingle(unchecked((int)0x4240EB85));

    private static readonly byte[] DonorAppearance =
    {
        0x01, 0x01, 0x03, 0x0E, 0xFF, 0x00, 0x02, 0x00,
        0x00, 0x00, 0x01, 0x06, 0xFF, 0x00, 0x01
    };

    private static Character BuildDonorCharacter()
    {
        var appearance = new CharacterAppearance { RawBytes = (byte[])DonorAppearance.Clone() };
        return new Character
        {
            Name = "ChefSword",
            Level = 1,
            Exp = 0,                          
            Job = 0,                          
            JobLevel = 0,                     
            JobExp = 0x0000000001010000UL,    
            MasterLevel = 0,                  
            MapId = DonorMapId,
            AppearanceJson = CharacterSerializer.ToJson(appearance),
        };
    }

    private static byte[] BuildViaDonorTemplate(Character c)
    {
        using var w = new PacketWriter();
        WriteCharacterDetailRaw(w, DonorCharUid, DonorEntityId, DonorMapId, c, DonorPosX, DonorPosY, DonorPosZ);
        return w.ToArray();
    }

 /// <summary>Index of the accepted-Khara vector (Vec6) in the 2310 body of a character with no items or quests.</summary>
    private const int Vec6Index = 4 + 205 + 14 + 4 * 2;

 /// <summary>
 /// Reference Vec6 (488 bytes): the nine accepted Khara boards with zero counts. A character without Khara state
 /// writes an empty Vec6 instead.
 /// </summary>
    private const int DonorVec6Length = 488;

 /// <summary>Length of the reference body with its Vec6 replaced by an empty one.</summary>
    private static int DonorLengthWithoutKhara() => BuildViaDonorTemplate(BuildDonorCharacter()).Length - (DonorVec6Length - 2);

    private static byte[] BuildViaFieldByField(Character c)
    {
        using var w = new PacketWriter();
        WorldEntryService.WriteCharacterDetail(w, c, DonorCharUid, DonorEntityId, null, DonorMapId, DonorPosX, DonorPosY, DonorPosZ);
        return w.ToArray();
    }

 /// <summary>
 /// Shared reference character with its job block read from the reference bytes: profession 1, job level 1 and
 /// master level 6. It has no Khara state, so its Khara vectors are empty.
 /// </summary>
    private static Character DonorJobBlockCharacter()
    {
        var c = BuildDonorCharacter();
        c.Id = 0x7FFF_2311;
        c.Job = 1;          
        c.JobLevel = 1;     
        c.JobExp = 0;       
        c.MasterLevel = 6;  
        return c;
    }

 /// <summary>
 /// Reference body with its Vec6 replaced by an empty one and its innerLen corrected, as the field-by-field
 /// writer sends it for a character with no Khara state.
 /// </summary>
    private static byte[] DonorWithoutKhara(Character c)
    {
        var donor = BuildViaDonorTemplate(c);
        var spliced = new byte[donor.Length - (DonorVec6Length - 2)];
        donor.AsSpan(0, Vec6Index).CopyTo(spliced);                                   
        donor.AsSpan(Vec6Index + DonorVec6Length).CopyTo(spliced.AsSpan(Vec6Index + 2)); 
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(spliced, (uint)(spliced.Length - 4));
        return spliced;
    }

    [Fact]
    public void FieldByField_IsByteIdentical_ToDonorTemplate()
    {
        
        
        var c = DonorJobBlockCharacter();

        var donor = DonorWithoutKhara(c);
        var built = BuildViaFieldByField(c);

        
        Assert.Equal(donor.Length, built.Length);

        
        int firstDiff = -1;
        int n = System.Math.Min(donor.Length, built.Length);
        for (int i = 0; i < n; i++)
        {
            if (donor[i] != built[i]) { firstDiff = i; break; }
        }

        Assert.True(firstDiff < 0,
            firstDiff < 0 ? "byte-identical"
                : $"First diff at array index {firstDiff} (body offset {firstDiff - 4}): " +
                  $"donor=0x{donor[firstDiff]:X2} built=0x{built[firstDiff]:X2}\n" +
                  $"  donor[{firstDiff - 2}..{firstDiff + 3}] = {Hex(donor, firstDiff - 2, 6)}\n" +
                  $"  built[{firstDiff - 2}..{firstDiff + 3}] = {Hex(built, firstDiff - 2, 6)}");

        Assert.Equal(donor, built);
    }

    [Fact]
    public void Gold_IsWrittenAtWallet_MasteryTotalAtBody191()
    {
        
        
        
        
        
        
        
        const long gold = 4_294_968_000L; 
        var c = BuildDonorCharacter();
        c.Gold = gold;

        var built = BuildViaFieldByField(c);

        
        ulong wallet = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(built.AsSpan(4 + 112, 8));
        ulong masteryTotal = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(built.AsSpan(4 + 191, 8));

        Assert.Equal((ulong)gold, wallet);
        Assert.Equal((ulong)CharacterHeadRules.JobMasteryPoints(c), masteryTotal);

        
        Assert.Equal(DonorLengthWithoutKhara(), built.Length);
    }

    
    
    
    private static readonly byte[] RetailDualStabUserHead69To80 = Convert.FromHexString("3200000000000000000D0000");

    [Fact]
    public void Head78_IsTheCombatClass_ByteForByteWithRetail()
    {
        
        
        
        var c = BuildDonorCharacter();
        c.Level = 50;
        c.Exp = 0;
        c.Job = 13;

        var built = BuildViaFieldByField(c);

        
        Assert.Equal(RetailDualStabUserHead69To80, built.AsSpan(4 + 69, 12).ToArray());
        Assert.Equal(13, built[4 + 78]);
        Assert.Equal(13, CharacterHeadRules.CombatJobByte(c));
        
        Assert.Equal(DonorLengthWithoutKhara(), built.Length);
    }

 /// <summary>
 /// Builds a character as CharacterCreateHandler stores it: the 23-byte create block is parsed by
 /// <see cref="CharacterSerializer.ParseCreateAppearance"/>, and <paramref name="job"/> is stored in Character.Job.
 /// </summary>
    private static Character CreatedCharacter(string createHex, byte job)
    {
        var appearance = CharacterSerializer.ParseCreateAppearance(new PacketReader(Convert.FromHexString(createHex)));
        var c = BuildDonorCharacter();
        c.Job = job;
        c.AppearanceJson = CharacterSerializer.ToJson(appearance);
        return c;
    }

    
    
    
    public static TheoryData<string, string, byte, byte> RetailCreates => new()
    {
        
        { "0202020fff000c0000000611ff00010000000010000300", "0202020fff000c0000000611ff0001", 16, 3 },
        
        { "0202060dff0006000000060eff00010000000010000200", "0202060dff0006000000060eff0001", 16, 2 },
        
        { "01020103ff00010000000103ff00010000000001000400", "01020103ff00010000000103ff0001", 1, 4 },
        
        { "0101010aff0001000000060fff00010000000004000200", "0101010aff0001000000060fff0001", 4, 2 },
        
        { "01010701000003000000040fff00010000000004000100", "01010701000003000000040fff0001", 4, 1 },
    };

    [Theory]
    [MemberData(nameof(RetailCreates))]
    public void Head78IsCreate19_Head130IsCreate21_LikeRetail(string createHex, string retailHead54To68,
        byte retailHead78, byte retailHead130)
    {
        
        
        
        var create = Convert.FromHexString(createHex);
        foreach (var storedJob in new[] { create[21], create[19] })
        {
            var c = CreatedCharacter(createHex, storedJob);
            var built = BuildViaFieldByField(c);

            Assert.Equal(Convert.FromHexString(retailHead54To68), built.AsSpan(4 + 54, 15).ToArray());
            Assert.Equal(retailHead78, built[4 + 78]);
            Assert.Equal(retailHead130, built[4 + 130]);
            Assert.Equal(DonorLengthWithoutKhara(), built.Length);
        }
    }

    [Fact]
    public void Head78_LocalAlterRow_IsAlterNotThief()
    {
        
        
        var c = CreatedCharacter("02010703ff00050000000603ff00010000000010000400", job: 4);

        var built = BuildViaFieldByField(c);

        Assert.Equal(16, built[4 + 78]);
        Assert.Equal(4, built[4 + 130]);
        Assert.Equal(16, CharacterHeadRules.CombatJobByte(c));
        Assert.Equal(4, CharacterHeadRules.CreationProfessionByte(c));
    }

    [Fact]
    public void Head78_FollowsAJobChange_Head130DoesNot()
    {
        
        
        
        var c = CreatedCharacter("0202060dff0006000000060eff00010000000010000200", job: 2); 
        c.Level = 25;
        var before = BuildViaFieldByField(c);
        c.Job = 17;
        var after = BuildViaFieldByField(c);

        Assert.Equal(16, before[4 + 78]);
        Assert.Equal(17, after[4 + 78]);
        Assert.Equal(2, before[4 + 130]);
        Assert.Equal(2, after[4 + 130]);
        Assert.Equal(before.Length, after.Length);
        
        var changed = System.Linq.Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]).ToHashSet();
        Assert.Equal(new HashSet<int> { 4 + 78 }, changed);
    }

    [Fact]
    public void Head130_WithoutACreateBlock_KeepsCharacterJob()
    {
        
        var c = BuildDonorCharacter();
        c.Job = 3;

        var built = BuildViaFieldByField(c);

        Assert.Equal(3, built[4 + 130]);
        Assert.Equal(3, built[4 + 78]);
    }

    [Fact]
    public void Head78_WithoutAClass_KeepsTheByteTheDonorCarried()
    {
        
        
        var c = BuildDonorCharacter();
        Assert.Equal(0, c.Job);

        var built = BuildViaFieldByField(c);
        var donor = BuildViaDonorTemplate(c);

        Assert.Equal(0x01, built[4 + 78]);
        Assert.Equal(donor[4 + 78], built[4 + 78]);
    }

    [Fact]
    public void DonorTemplate_HasExpected781ByteLength()
    {
        
        var donor = BuildViaDonorTemplate(BuildDonorCharacter());
        Assert.Equal(781, donor.Length);
        Assert.Equal(777u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(donor));
    }

    [Fact]
    public void KharaVectors_WithoutState_AreEmpty_AndTheDonorBoardsAreGone()
    {
        
        
        
        
        
        var c = BuildDonorCharacter();
        c.Id = 0x7FFF_2310;   

        var donor = BuildViaDonorTemplate(c);
        var built = BuildViaFieldByField(c);

        Assert.Equal(9, donor[Vec6Index]);                          
        Assert.Equal(0, donor[Vec6Index + 1]);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, built.AsSpan(Vec6Index, 4).ToArray()); 
        
        Assert.Equal(donor.AsSpan(Vec6Index + DonorVec6Length + 2).ToArray(), built.AsSpan(Vec6Index + 4).ToArray());
        Assert.Equal(DonorLengthWithoutKhara(), built.Length);
        Assert.Equal(0u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(built.AsSpan(4 + 126, 4)));
        Assert.Equal(built.Length - 4, (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(built));
    }

    private static string Hex(byte[] data, int start, int count)
    {
        if (start < 0) { count += start; start = 0; }
        count = System.Math.Min(count, data.Length - start);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < count; i++) sb.Append(data[start + i].ToString("X2")).Append(' ');
        return sb.ToString().TrimEnd();
    }

 /// <summary>
 /// Builds the 2310 character detail payload from the fixed byte layout. Superseded by <see cref="WriteCharacterDetail"/>.
 /// </summary>
 /// <remarks>
 /// Kept only as the byte-identity oracle for the verification tests. Do not use it to send.
 /// </remarks>
    private static void WriteCharacterDetailRaw(PacketWriter writer, ulong charUid, uint entityId,
        uint mapId, Character character, float posX, float posY, float posZ)
    {
        var raw = new byte[] {
            0x09, 0x03, 0x00, 0x00, 0x04, 0x73, 0xF0, 0xA7, 0xB3, 0xB6, 0xE0, 0x0D, 0x11, 0xB8, 0x37, 0x01,
            // name region (offset 16, 42 bytes): zeroed here, filled from character.Name at build time below.
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01, 0x03, 0x0E, 0xFF, 0x00,
            0x02, 0x00, 0x00, 0x00, 0x01, 0x06, 0xFF, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x01, 0x00, 0x00, 0xE0, 0x00, 0x00, 0x00, 0x0F, 0x00, 0x00, 0x00, 0xCD, 0x4C, 0x51,
            0x43, 0x1F, 0xE5, 0x86, 0x43, 0x85, 0xEB, 0x40, 0x42, 0x4D, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x49, 0x14, 0x00, 0x00,
            0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x03, 0x00, 0x79, 0xAE, 0x01, 0x00, 0x99, 0xB1, 0x01, 0x00, 0xE1, 0xBE, 0x01, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x09, 0x00, 0x1C, 0x01, 0x00, 0x00, 0x02, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1E, 0x01, 0x00, 0x00, 0x02,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20, 0x01, 0x00,
            0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x12, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x21, 0x01, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x09,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x22, 0x01, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x09, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x23, 0x01, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x24, 0x01, 0x00, 0x00, 0x02, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF3, 0x01, 0x00, 0x00, 0x02,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF4, 0x01, 0x00,
            0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x1C, 0x00, 0x00, 0x00, 0x11, 0xB8, 0x37, 0x01, 0x11, 0xB8, 0x37, 0x01, 0xE0, 0x00, 0x00,
            0x00, 0x0F, 0x00, 0x00, 0x00, 0xE0, 0x00, 0x00, 0x00, 0x0F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x10, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        };
        BitConverter.TryWriteBytes(raw.AsSpan(4), charUid);   
        BitConverter.TryWriteBytes(raw.AsSpan(12), entityId); 
        var nameBytes = Encoding.Unicode.GetBytes(character.Name ?? "");
        Array.Clear(raw, 16, 42);
        Buffer.BlockCopy(nameBytes, 0, raw, 16, Math.Min(nameBytes.Length, 40));

        
        
        
        System.Diagnostics.Debug.Assert(raw[85] == 0xE0 && raw[89] == 0x0F, "2310 position anchor moved");
        BitConverter.TryWriteBytes(raw.AsSpan(93), posX);
        BitConverter.TryWriteBytes(raw.AsSpan(97), posY);
        BitConverter.TryWriteBytes(raw.AsSpan(101), posZ);
        BitConverter.TryWriteBytes(raw.AsSpan(105), (uint)mapId);

        
        
        BitConverter.TryWriteBytes(raw.AsSpan(725), entityId);
        BitConverter.TryWriteBytes(raw.AsSpan(729), entityId);

        
        
        
        
        
        
        System.Diagnostics.Debug.Assert(raw[62] == 0xFF && raw[70] == 0xFF, "2310 appearance block moved");
        var ap = CharacterSerializer.ParseAppearancePublic(character.AppearanceJson);
        if (ap.RawBytes != null && ap.RawBytes.Length >= 15)
            Buffer.BlockCopy(ap.RawBytes, 0, raw, 58, 15);

        writer.WriteBytes(raw);
    }

}
