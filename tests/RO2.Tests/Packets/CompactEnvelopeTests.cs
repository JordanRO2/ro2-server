using System.Buffers.Binary;
using RO2.Network.Packets;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Gates for <see cref="CompactEnvelope"/>, the single definition of the compact-length ByteArray envelope
/// <c>[u8 width in {1,2,4,8}][width bytes LE = N][blob]</c>.
/// </summary>
/// <remarks>
/// Replaces the hand-written parses that accepted either strict bodies (declared length equals the remaining
/// bytes) or lenient ones (declared length not read). The tests replay the former implementations against the
/// new one over a generated set of bodies and require identical results and blobs.
/// </remarks>
public class CompactEnvelopeTests
{
    
    
    private const string Retail1310 = "011212000ee411014b0605010000007110000005";

    
    
    private const string Retail2377 =
        "0298009800c5e2118a130a00009ba1081300000000c4d3e90da83d040e757845010000f8041800000000000000"
        + "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
        + "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
        + "000000000000000000000000c4d3e90da83d040e";

    
    
    private const string Request1309WithTransportTail = "010E0E0002E30200100013220000" + "0F00" + "4E0465A6";

    private static byte[] Frame(int width, ulong declared, params byte[] blob)
    {
        var body = new byte[1 + width + blob.Length];
        body[0] = (byte)width;
        for (int i = 0; i < width; i++) body[1 + i] = (byte)(declared >> (8 * i));
        blob.CopyTo(body, 1 + width);
        return body;
    }

    
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void EachWidth_StripsExactlyItsOwnPrefix_InBothModes(int width)
    {
        var body = Frame(width, 3, 0xAA, 0xBB, 0xCC);

        ReadOnlySpan<byte> span = body;
        Assert.True(span.TryReadCompactLength(out ulong value, out int size));
        Assert.Equal(3UL, value);
        Assert.Equal(1 + width, size);

        Assert.True(span.TryGetCompactBlob(CompactLengthCheck.Strict, out var strict));
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, strict.ToArray());
        Assert.True(span.TryGetCompactBlob(CompactLengthCheck.Lenient, out var lenient));
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, lenient.ToArray());
    }

    [Theory]
    [InlineData(10L, "010a")]
    [InlineData(145L, "029100")]                   
    [InlineData(0x8000L, "0400800000")]
    [InlineData(0x80000000L, "080000008000000000")]
    public void ReadsBackWhatTheWriterWrites_AtEveryWidth(long value, string expectedHex)
    {
        using var w = new PacketWriter(16);
        w.WriteCompactLength(value);
        var bytes = w.ToArray();
        Assert.Equal(expectedHex, Convert.ToHexString(bytes).ToLowerInvariant());

        Assert.True(((ReadOnlySpan<byte>)bytes).TryReadCompactLength(out ulong read, out int size));
        Assert.Equal((ulong)value, read);
        Assert.Equal(bytes.Length, size);
    }

    [Fact]
    public void ZeroLengthBlob_IsWellFormed()
    {
        ReadOnlySpan<byte> body = new byte[] { 0x01, 0x00 };
        Assert.True(body.TryGetCompactBlob(CompactLengthCheck.Strict, out var blob));
        Assert.True(blob.IsEmpty);
        Assert.True(body.TryGetCompactBlob(CompactLengthCheck.Lenient, out blob));
        Assert.True(blob.IsEmpty);
    }

    
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void DeclaredShorterThanTheBytes_StrictRejects_LenientKeepsTheTail(int width)
    {
        var body = Frame(width, 2, 0x10, 0x20, 0x30, 0x40);   

        ReadOnlySpan<byte> span = body;
        Assert.False(span.TryGetCompactBlob(CompactLengthCheck.Strict, out var strict));
        Assert.True(strict.IsEmpty);
        Assert.True(span.TryGetCompactBlob(CompactLengthCheck.Lenient, out var lenient));
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x40 }, lenient.ToArray());   
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void DeclaredLongerThanTheBytes_StrictRejects_LenientTakesWhatIsThere(int width)
    {
        var body = Frame(width, 9, 0x10, 0x20);                

        ReadOnlySpan<byte> span = body;
        Assert.False(span.TryGetCompactBlob(CompactLengthCheck.Strict, out _));
        Assert.True(span.TryGetCompactBlob(CompactLengthCheck.Lenient, out var lenient));
        Assert.Equal(new byte[] { 0x10, 0x20 }, lenient.ToArray());
    }

    [Fact]
    public void EightByteLengthWithTheTopBitSet_NeverMatches()
    {
        
        
        var body = Frame(8, 0x8000_0000_0000_0002UL, 0x10, 0x20);
        Assert.False(((ReadOnlySpan<byte>)body).TryGetCompactBlob(CompactLengthCheck.Strict, out _));
        Assert.True(((ReadOnlySpan<byte>)body).TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob));
        Assert.Equal(2, blob.Length);
    }

    [Theory]
    [InlineData("")]                   
    [InlineData("00")]                 
    [InlineData("030e0e0002e3")]       
    [InlineData("4c040000")]           
    [InlineData("ff01")]
    [InlineData("01")]                 
    [InlineData("0291")]               
    [InlineData("04010000")]           
    [InlineData("0801000000000000")]   
    public void MalformedPrefix_IsRejectedInBothModes(string hex)
    {
        ReadOnlySpan<byte> body = Convert.FromHexString(hex);
        Assert.False(body.TryReadCompactLength(out ulong value, out int size));
        Assert.Equal(0UL, value);
        Assert.Equal(0, size);
        Assert.False(body.TryGetCompactBlob(CompactLengthCheck.Strict, out var blob));
        Assert.True(blob.IsEmpty);
        Assert.False(body.TryGetCompactBlob(CompactLengthCheck.Lenient, out blob));
        Assert.True(blob.IsEmpty);
    }

    [Fact]
    public void AnUnknownCheckValue_IsTreatedAsStrict()
    {
        ReadOnlySpan<byte> body = Frame(1, 2, 0x10, 0x20, 0x30);
        Assert.False(body.TryGetCompactBlob((CompactLengthCheck)42, out _));
    }

    [Fact]
    public void WidthMarkers_AreExactlyOneTwoFourEight()
    {
        for (int b = 0; b <= 0xFF; b++)
            Assert.Equal(b is 1 or 2 or 4 or 8, CompactEnvelope.IsWidthMarker((byte)b));
    }

    
    [Fact]
    public void Retail1310_WidthOne_BothModesAgree()
    {
        ReadOnlySpan<byte> body = Convert.FromHexString(Retail1310);

        Assert.True(body.TryGetCompactBlob(CompactLengthCheck.Strict, out var blob));
        Assert.Equal(18, blob.Length);
        Assert.Equal(blob.Length, BinaryPrimitives.ReadUInt16LittleEndian(blob));        
        Assert.Equal((ushort)58382, BinaryPrimitives.ReadUInt16LittleEndian(blob[2..])); 
        Assert.Equal(0x11u, BinaryPrimitives.ReadUInt32LittleEndian(blob[4..]) & 0xFFu); 
        Assert.True(blob == body[2..]);                                                   

        Assert.True(body.TryGetCompactBlob(CompactLengthCheck.Lenient, out var lenient));
        Assert.True(lenient == blob);
    }

    [Fact]
    public void Retail2377_WidthTwo_BlobStartsAtBodyPlusThree()
    {
        ReadOnlySpan<byte> body = Convert.FromHexString(Retail2377);

        Assert.True(body.TryGetCompactBlob(CompactLengthCheck.Strict, out var blob));
        Assert.Equal(152, blob.Length);
        Assert.True(blob == body[3..]);
        Assert.Equal(blob.Length, BinaryPrimitives.ReadUInt16LittleEndian(blob));        
        Assert.Equal((ushort)58053, BinaryPrimitives.ReadUInt16LittleEndian(blob[2..])); 
    }

    [Fact]
    public void Request1309WithTransportTail_OnlyLenientAccepts()
    {
        ReadOnlySpan<byte> body = Convert.FromHexString(Request1309WithTransportTail);

        Assert.False(body.TryGetCompactBlob(CompactLengthCheck.Strict, out _));
        Assert.True(body.TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob));
        Assert.Equal(18, blob.Length);                                                    
        Assert.Equal((ushort)14, BinaryPrimitives.ReadUInt16LittleEndian(blob));         
        Assert.Equal((ushort)58114, BinaryPrimitives.ReadUInt16LittleEndian(blob[2..])); 

        
        Assert.False(PartyAnswerPackets.TryGetCompactBlob(body, out _));
        Assert.True(MailPackets.TryGetBlob(body, out var mail));
        Assert.True(mail == blob);
    }

    
 /// <summary>Former strict parse (PartyAnswerPackets.TryGetCompactBlob and the refine-family helper).</summary>
    private static bool OldStrict(ReadOnlySpan<byte> body, out ReadOnlySpan<byte> blob)
    {
        blob = default;
        if (body.Length < 2) return false;
        int width = body[0];
        if (width is not (1 or 2 or 4 or 8) || body.Length < 1 + width) return false;
        long len = 0;
        for (int i = 0; i < width; i++) len |= (long)body[1 + i] << (8 * i);
        if (len != body.Length - 1 - width) return false;
        blob = body[(1 + width)..];
        return true;
    }

 /// <summary>Former inline lenient parse: a legal marker and room for the length bytes.</summary>
    private static bool OldLenient(ReadOnlySpan<byte> body, out ReadOnlySpan<byte> blob)
    {
        blob = default;
        if (body.Length < 1) return false;
        var width = body[0];
        if (width is not (1 or 2 or 4 or 8) || body.Length < 1 + width) return false;
        blob = body.Slice(1 + width);
        return true;
    }

 /// <summary>Former lenient parse (MailPackets.TryGetBlob).</summary>
    private static bool OldMail(ReadOnlySpan<byte> payload, out ReadOnlySpan<byte> blob)
    {
        blob = default;
        if (payload.Length < 1) return false;
        int n = payload[0];
        int hdr = n == 1 ? 1 : n == 2 ? 2 : n == 4 ? 4 : n == 8 ? 8 : -1;
        if (hdr < 0) return false;
        int start = 1 + hdr;
        if (payload.Length < start + 2) return false;
        blob = payload.Slice(start);
        return true;
    }

 /// <summary>Former length-scanning loop.</summary>
    private static bool OldCompactLen(ReadOnlySpan<byte> b, out ulong n, out int size)
    {
        n = 0; size = 0;
        if (b.Length < 1) return false;
        byte k = b[0];
        if (k is not (1 or 2 or 4 or 8) || 1 + k > b.Length) return false;
        for (int i = 0; i < k; i++) n |= (ulong)b[1 + i] << (8 * i);
        size = 1 + k;
        return true;
    }

 /// <summary>
 /// Bodies of 0..24 bytes under legal and illegal markers, with exact, short, long and random declared lengths,
 /// plus top-bit 8-byte lengths.
 /// </summary>
    private static List<byte[]> SampleBodies()
    {
        var bodies = new List<byte[]>();
        var rng = new Random(1306);
        byte[] markers = { 0, 1, 2, 3, 4, 5, 7, 8, 9, 0x10, 0x4C, 0x80, 0xFF };
        foreach (var marker in markers)
        {
            for (int len = 0; len <= 24; len++)
            {
                for (int variant = 0; variant < 5; variant++)
                {
                    var b = new byte[len];
                    rng.NextBytes(b);
                    if (len > 0) b[0] = marker;
                    int w = marker is 1 or 2 or 4 or 8 ? marker : 1;
                    if (len >= 1 + w && variant < 4)
                    {
                        long remaining = len - 1 - w;
                        ulong declared = variant switch
                        {
                            0 => (ulong)remaining,
                            1 => (ulong)(remaining - 1),                  
                            2 => (ulong)(remaining + 1),
                            _ => 0x8000_0000_0000_0000UL | (ulong)remaining,
                        };
                        for (int i = 0; i < w; i++) b[1 + i] = (byte)(declared >> (8 * i));
                    }
                    bodies.Add(b);
                }
            }
        }
        return bodies;
    }

    private static void AssertSame(bool expected, ReadOnlySpan<byte> expectedBlob, bool actual,
        ReadOnlySpan<byte> actualBlob, byte[] body, string what)
    {
        Assert.True(expected == actual, $"{what}: {Convert.ToHexString(body)} old={expected} new={actual}");
        Assert.True(expectedBlob == actualBlob || expectedBlob.SequenceEqual(actualBlob),
            $"{what}: {Convert.ToHexString(body)} returned a different blob");
    }

    [Fact]
    public void Strict_AcceptsExactlyWhatTheOldStrictParseAccepted()
    {
        foreach (var body in SampleBodies())
        {
            bool expected = OldStrict(body, out var oldBlob);
            bool actual = ((ReadOnlySpan<byte>)body).TryGetCompactBlob(CompactLengthCheck.Strict, out var newBlob);
            AssertSame(expected, oldBlob, actual, newBlob, body, "strict");

            actual = PartyAnswerPackets.TryGetCompactBlob(body, out newBlob);
            AssertSame(expected, oldBlob, actual, newBlob, body, "PartyAnswerPackets.TryGetCompactBlob");
        }
    }

    [Fact]
    public void Lenient_AcceptsExactlyWhatTheOldLenientParsesAccepted()
    {
        foreach (var body in SampleBodies())
        {
            bool expected = OldLenient(body, out var oldBlob);
            bool actual = ((ReadOnlySpan<byte>)body).TryGetCompactBlob(CompactLengthCheck.Lenient, out var newBlob);
            AssertSame(expected, oldBlob, actual, newBlob, body, "lenient");

            expected = OldMail(body, out oldBlob);
            actual = MailPackets.TryGetBlob(body, out newBlob);
            AssertSame(expected, oldBlob, actual, newBlob, body, "MailPackets.TryGetBlob");
        }
    }

    [Fact]
    public void CompactLength_ReadsExactlyWhatTheOldLoopRead()
    {
        foreach (var body in SampleBodies())
        {
            bool expected = OldCompactLen(body, out ulong oldN, out int oldSize);
            bool actual = ((ReadOnlySpan<byte>)body).TryReadCompactLength(out ulong newN, out int newSize);
            Assert.True(expected == actual, $"{Convert.ToHexString(body)} old={expected} new={actual}");
            Assert.Equal(oldN, newN);
            Assert.Equal(oldSize, newSize);
        }
    }
}
