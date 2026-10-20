using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Pins the 70-byte record head of <c>ActorStatePackets.BuildOnPCViewData2315</c> to the expected layout,
/// so the appearance and trailing slots cannot regress to zeros.
/// </summary>
/// <remarks>
/// The builder is fed the values the 2310 carries and the record repeats: the position fields equal the matching
/// 2310 fields, the field near the end equals the 2310 final tail plus 4, and the trailing word equals 2310 bytes
/// 140..141. These relations hold for the reference character.
/// </remarks>
public class OnPcViewData2315Tests
{
    private const string RetailHeadHex =
        "11dd120b138c9ca7b3b6e00d00005c8f3144e10a39446666624202020705ff00080000000404ff0001" +
        "f10000008b17000086070000860700000000a040ab611cc01000010000";

    
    private const string Detail2310AppearanceHex = "02020705ff00080000000404ff0001";
    private const byte Detail2310TailModelClass = 16;
    private const ushort Detail2310Body140 = 1;

    private static float F(byte[] head, int off) => BinaryPrimitives.ReadSingleLittleEndian(head.AsSpan(off, 4));

    private static byte[] RecordHead(byte[] body)
    {
        
        int blobStart = 1 + body[0];
        return body.AsSpan(blobStart + 12, 70).ToArray();
    }

    private static byte[] Build(byte[]? appearanceRaw, byte modelClass, ushort masterLevel)
    {
        var head = Convert.FromHexString(RetailHeadHex);
        return ActorStatePackets.BuildOnPCViewData2315(
            actorHandle: 0x0B12DD11u, characterGid: 0x0DE0B6B3A79C8C13UL, name: "yolines", race: 2, gender: 2,
            x: F(head, 14), y: F(head, 18), z: F(head, 22), yaw: F(head, 61),
            curHp: 241, maxHp: 6027, curSp: 1926, maxSp: 1926, level: 50, job: 4, jobLevel: 50,
            equipped: null, moveSpeed: 5.0f,
            appearanceRaw: appearanceRaw, modelClass: modelClass, masterLevel: masterLevel);
    }

    [Fact]
    public void SelfRecordHead_IsRetailByteForByte_WhenFedTheSession2310Values()
    {
        var body = Build(Convert.FromHexString(Detail2310AppearanceHex), Detail2310TailModelClass, Detail2310Body140);

        Assert.Equal(RetailHeadHex, Convert.ToHexString(RecordHead(body)).ToLowerInvariant());
    }

    [Fact]
    public void CallersThatPassNothing_KeepTheOldZeroFill()
    {
        var rec = RecordHead(Build(appearanceRaw: null, modelClass: 0, masterLevel: 0));

        Assert.All(rec.AsSpan(28, 13).ToArray(), b => Assert.Equal(0, b)); 
        Assert.Equal(0, rec[65]);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(67, 2)));
    }

    [Fact]
    public void AppearanceShorterThanTheBlock_IsNotPartiallyCopied()
    {
        var rec = RecordHead(Build(appearanceRaw: new byte[] { 2, 2, 7, 5 }, modelClass: 0, masterLevel: 0));

        Assert.All(rec.AsSpan(28, 13).ToArray(), b => Assert.Equal(0, b));
    }
}
