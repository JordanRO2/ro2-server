using System;
using System.Collections.Generic;
using System.Text;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-layout tests for the party-roster notify broadcasts (2361 OnPartyNotifyUpdate, 2357 OnPartyNotifyCreate,
/// 2359 OnPartyNotifyJoin), which carry the shared 208-byte member record.
/// The builders assemble frames from a party handle, a fixed header and member records. Each test splits a reference
/// frame into those parts, rebuilds it, and asserts the result matches byte for byte. This covers the compact marker,
/// both length fields, the msgTag, the member count and the party handle position.
/// </summary>
public class PartyPacketsTests
{
    
    private const string EXPECTED_2361 = "02e301e30135e3000000001569020000000202ac9cd4a7b3b6e00d00000000000000000200000000000000000000000000000000000000000000000000ac9cd4a7b3b6e00d021173fb06ac9cd4a7b3b6e00d00000030060004d00f0000c41b00006a030000d10500004c04000000007f46334452303844d27e5e420100000000414051060100000062006f0072006e003200770069006e0000000000000000000000000000000000000000000000000000004e0065006d0065007300690073002700000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000110efc068fd9560dc161a63700000032080001bad00000bad00000b82d0000b82d00004c0400000000d1e13144a4123944d37e5e42010000000041405106020028043700420000000000000000000000000000000000000000000000000000000000000000000000000000004100720074004f006600570061007200730000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";
    private const string EXPECTED_2357 = "02e201e20131e3000000001569020000000202ac9cd4a7b3b6e00d00000000000000000200000000000000000000000000000000000000000000000000ac9cd4a7b3b6e00d1173fb06ac9cd4a7b3b6e00d01000030060004ff0e0000c41b000033030000d10500004c04000000007671324454b43944566c5d420100000000414051060100000062006f0072006e003200770069006e0000000000000000000000000000000000000000000000000000004e0065006d0065007300690073002700000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000110efc068fd9560dc161a63701000032080001bad00000bad00000b82d0000b82d00004c0400000000d1e13144a4123944d37e5e42010000000041405106020028043700420000000000000000000000000000000000000000000000000000000000000000000000000000004100720074004f006600570061007200730000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";
    private const string EXPECTED_2359 = "02dc00dc0033e300000000156902001164fc06ac9cd4a7b3b6e00d00000030060004c41b0000c41b0000d1050000d1050000bb040000000035d9294407c1f5444627d1410105000000414051060100000062006f0072006e003200770069006e0000000000000000000000000000000000000000000000000000004e0065006d0065007300690073002700000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";

    
 /// <summary>
 /// Splits a reference frame into (party handle, fixed header, 208-byte records), inverting the builder's envelope.
 /// The marker is body[0]; the payload starts after the two length fields and the u16 msgTag.
 /// </summary>
    private static (uint handle, byte[] header, List<byte[]> recs) Decompose(byte[] body, int headerLen)
    {
        int m = body[0];
        int payloadStart = 1 + m + 4;
        var payload = body.AsSpan(payloadStart);
        var header = payload.Slice(0, headerLen).ToArray();
        uint handle = BitConverter.ToUInt32(payload.Slice(4, 4));
        var recs = new List<byte[]>();
        for (int off = headerLen; off < payload.Length; off += 208)
            recs.Add(payload.Slice(off, 208).ToArray());
        return (handle, header, recs);
    }

 /// <summary>Asserts the invariants that make a body safe for the client to parse.</summary>
    private static void AssertSafeEnvelope(byte[] body, ushort expectedTag, int? countAtPayload11)
    {
        
        Assert.Contains(body[0], new byte[] { 1, 2, 4, 8 });
        int m = body[0];
        
        long lenField = 0;
        for (int i = 0; i < m; i++) lenField |= (long)body[1 + i] << (8 * i);
        Assert.Equal((long)(body.Length - (1 + m)), lenField);
        
        ushort selfLen = BitConverter.ToUInt16(body, 1 + m);
        Assert.Equal(body.Length - (1 + m), (int)selfLen);
        
        ushort tag = BitConverter.ToUInt16(body, 1 + m + 2);
        Assert.Equal(expectedTag, tag);
        
        var payload = body.AsSpan(1 + m + 4);
        Assert.Equal(0x15, payload[4] & 0xFF);
        if (countAtPayload11 is int c)
            Assert.Equal(c, (int)payload[11]);
    }

    
    [Fact]
    public void BuildUpdate2361_ReproducesRealRetailFrame()
    {
        var expected = Convert.FromHexString(EXPECTED_2361);
        Assert.Equal(486, expected.Length);                 
        var (handle, header, recs) = Decompose(expected, PartyNotifyPackets.UpdateHeaderLen);
        Assert.Equal(2, recs.Count);                        

        var actual = PartyNotifyPackets.BuildPartyNotifyUpdate2361(handle, header, recs);

        Assert.Equal(expected, actual);
        AssertSafeEnvelope(actual, PartyNotifyPackets.MsgTagPartyNotifyUpdate, countAtPayload11: recs.Count);
        
        int m0 = actual[0];
        Assert.Equal(recs.Count, (int)actual.AsSpan(1 + m0 + 4)[PartyNotifyPackets.UpdateHdrClientCountOff]);
    }

    
    [Fact]
    public void BuildCreate2357_ReproducesRealRetailFrame()
    {
        var expected = Convert.FromHexString(EXPECTED_2357);
        Assert.Equal(485, expected.Length);                 
        var (handle, header, recs) = Decompose(expected, PartyNotifyPackets.CreateHeaderLen);
        Assert.Equal(2, recs.Count);

        var actual = PartyNotifyPackets.BuildPartyNotifyCreate2357(handle, header, recs);

        Assert.Equal(expected, actual);
        AssertSafeEnvelope(actual, PartyNotifyPackets.MsgTagPartyNotifyCreate, countAtPayload11: recs.Count);
    }

    
    [Fact]
    public void BuildJoin2359_ReproducesRealRetailFrame()
    {
        var expected = Convert.FromHexString(EXPECTED_2359);
        Assert.Equal(223, expected.Length);                 
        var (handle, header, recs) = Decompose(expected, PartyNotifyPackets.JoinHeaderLen);
        Assert.Single(recs);

        var actual = PartyNotifyPackets.BuildPartyNotifyJoin2359(handle, header, recs[0]);

        Assert.Equal(expected, actual);
        
        AssertSafeEnvelope(actual, PartyNotifyPackets.MsgTagPartyNotifyJoin, countAtPayload11: null);
    }

    
    [Fact]
    public void BuildUpdate2361_PatchesCountAndHandle_ForAnyRosterSize()
    {
        
        
        for (int n = 1; n <= 20; n++)
        {
            var header = new byte[PartyNotifyPackets.UpdateHeaderLen];
            var recs = new List<byte[]>();
            for (int i = 0; i < n; i++) recs.Add(new byte[PartyNotifyPackets.MemberRecordLen]);
            uint handle = 0x00026915u; 

            var body = PartyNotifyPackets.BuildPartyNotifyUpdate2361(handle, header, recs);

            
            int m = body[0];
            Assert.Contains((byte)m, new byte[] { 1, 2, 4, 8 });
            int expectedLen = (1 + m) + 2 + 2 + PartyNotifyPackets.UpdateHeaderLen + n * 208;
            Assert.Equal(expectedLen, body.Length);
            AssertSafeEnvelope(body, PartyNotifyPackets.MsgTagPartyNotifyUpdate, countAtPayload11: n);
            
            
            
            Assert.Equal(n, (int)body.AsSpan(1 + m + 4)[PartyNotifyPackets.UpdateHdrClientCountOff]);
        }
    }

    
    [Fact]
    public void BuildMemberRecord_PlacesConfirmedFields()
    {
        uint handle = 0x06fb7311u;                 
        ulong gid = 0x0de0b6b3a7d49cacUL;          
        var rec = PartyNotifyPackets.BuildMemberRecord(handle, gid, "born2win", "Nemesis'");

        Assert.Equal(PartyNotifyPackets.MemberRecordLen, rec.Length);
        Assert.Equal(handle, BitConverter.ToUInt32(rec, PartyNotifyPackets.RecActorHandleOff));
        Assert.Equal(0x11, rec[PartyNotifyPackets.RecActorHandleOff] & 0xFF);
        Assert.Equal(gid, BitConverter.ToUInt64(rec, PartyNotifyPackets.RecGidOff));
        Assert.Equal("born2win",
            Encoding.Unicode.GetString(rec, PartyNotifyPackets.RecNameOff, 16));
        Assert.Equal("Nemesis'",
            Encoding.Unicode.GetString(rec, PartyNotifyPackets.RecGuildOff, 16));
        
        for (int i = 12; i < 66; i++) Assert.Equal(0, (int)rec[i]);
    }

    
    [Fact]
    public void BuildUpdate2361_RejectsWrongRecordSize()
    {
        var header = new byte[PartyNotifyPackets.UpdateHeaderLen];
        Assert.Throws<ArgumentException>(() =>
            PartyNotifyPackets.BuildPartyNotifyUpdate2361(0x15u, header, new[] { new byte[207] }));
    }

    [Fact]
    public void BuildUpdate2361_RejectsWrongHeaderSize()
    {
        Assert.Throws<ArgumentException>(() =>
            PartyNotifyPackets.BuildPartyNotifyUpdate2361(0x15u, new byte[10],
                new[] { new byte[PartyNotifyPackets.MemberRecordLen] }));
    }
}
