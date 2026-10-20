using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-layout gate for RMI 2471 OnLootingAuthority, the message that grants a kill's corpse to the player allowed to loot it.
/// </summary>
/// <remarks>
/// Body layout (17 bytes, S2C, unencrypted):
/// [0..1] compact-length width marker and value (the value, 15, is the blob length)
/// [2..3] u16 selfLen, 15
/// [4..5] u16 msgTag, 0xE3DE
/// [6..9] u32, always zero
/// [10..11] u16 count, 1
/// [12..15] actor handle of the corpse (low byte 0x13)
/// [16] granted flag: 1 grants the corpse, 0 revokes it
///
/// The client reads the blob through a nested reader: an 8-byte prologue, then a u16 count followed by count entries of 5 bytes.
/// Every reference frame satisfies len(blob) == 10 + 5 * count.
///
/// The handle identifies the corpse: it is the dead actor named by the 2392 OnDeadNotify that follows a grant.
/// A body whose first byte is outside {1,2,4,8} is rejected by the decoder, and the consumer then receives a null blob,
/// so the marker must be validated. The u16 count is one of the few length reads the client checks, so a body that ends
/// before it is rejected as a whole.
/// </remarks>
public class LootingAuthority2471Tests
{
 /// <summary>
 /// A grant: the corpse's handle is granted to the player allowed to loot it. The 2392 OnDeadNotify that follows carries the same handle
 /// in its dead-actor slot and a PC handle in its killer slot.
 /// </summary>
    private static readonly byte[] RetailGrant =
        Convert.FromHexString("010f0f00dee300000000010013e99d0101");

 /// <summary>
 /// A revoke of the grant, sent as the corpse is emptied. It follows a C2S 1339 for the same handle, and item-transfer notifies
 /// follow it in the same millisecond.
 /// </summary>
    private static readonly byte[] RetailRevoke =
        Convert.FromHexString("010f0f00dee300000000010013e79d0100");

    private const uint GrantedCorpse = 0x019DE913u;
    private const uint RevokedCorpse = 0x019DE713u;

    private static ReadOnlySpan<byte> Blob(byte[] body) => body.AsSpan(1 + body[0]);

    [Fact]
    public void Grant_ReproducesARealRetailFrameByteForByte()
    {
        Assert.Equal(RetailGrant, CombatPackets.BuildLootingAuthority2471(GrantedCorpse));
    }

    [Fact]
    public void Revoke_ReproducesARealRetailFrameByteForByte()
    {
        Assert.Equal(RetailRevoke,
            CombatPackets.BuildLootingAuthority2471(RevokedCorpse, granted: false));
    }

    [Fact]
    public void FirstBodyByte_IsALegalCompactLengthMarker()
    {
        
        
        foreach (var body in new[]
                 {
                     CombatPackets.BuildLootingAuthority2471(GrantedCorpse),
                     CombatPackets.BuildLootingAuthority2471(RevokedCorpse, granted: false),
                     CombatPackets.BuildLootingAuthority2471(new[]
                     {
                         new CombatPackets.LootAuthorityEntry(GrantedCorpse, true),
                         new CombatPackets.LootAuthorityEntry(RevokedCorpse, false),
                     }),
                     CombatPackets.BuildLootingAuthority2471(
                         Array.Empty<CombatPackets.LootAuthorityEntry>()),
                 })
        {
            Assert.Contains(body[0], new byte[] { 1, 2, 4, 8 });
        }
    }

    [Fact]
    public void BlobPlus4_IsZeroAsInEveryRetailFrame()
    {
        
        
        var blob = Blob(CombatPackets.BuildLootingAuthority2471(GrantedCorpse));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(4)));
    }

    [Fact]
    public void CorpseHandleKeepsTheNpcTypeTag()
    {
        
        var blob = Blob(CombatPackets.BuildLootingAuthority2471(GrantedCorpse));
        Assert.Equal(GrantedCorpse, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(10)));
        Assert.Equal(0x13, blob[10]);
    }

    [Fact]
    public void GrantAndRevoke_DifferOnlyInTheFinalByte()
    {
        
        
        var grant = CombatPackets.BuildLootingAuthority2471(GrantedCorpse);
        var revoke = CombatPackets.BuildLootingAuthority2471(GrantedCorpse, granted: false);
        Assert.Equal(grant.Length, revoke.Length);
        Assert.Equal(grant.AsSpan(0, grant.Length - 1).ToArray(),
            revoke.AsSpan(0, revoke.Length - 1).ToArray());
        Assert.Equal(1, Blob(grant)[14]);
        Assert.Equal(0, Blob(revoke)[14]);
    }

    [Theory]
    [InlineData(1, 17)]
    [InlineData(2, 22)]
    [InlineData(3, 27)]
    public void CountAtBlobPlus8_DrivesTheBodyLength(int count, int expectedBodyLength)
    {
        
        
        
        var entries = Enumerable.Range(0, count)
            .Select(i => new CombatPackets.LootAuthorityEntry((uint)((i << 8) | 0x13), true))
            .ToArray();
        var body = CombatPackets.BuildLootingAuthority2471(entries);

        Assert.Equal(expectedBodyLength, body.Length);
        var blob = Blob(body);
        Assert.Equal(count, BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(8)));
        Assert.Equal(10 + 5 * count, blob.Length);
        Assert.Equal(blob.Length, BinaryPrimitives.ReadUInt16LittleEndian(blob));
        for (var i = 0; i < count; i++)
        {
            Assert.Equal((uint)((i << 8) | 0x13),
                BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(10 + 5 * i)));
            Assert.Equal(1, blob[10 + 5 * i + 4]);
        }
    }
}
