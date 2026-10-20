using RO2.Server.Zone.Handlers.Rmi;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// S2C 2750 ViewGroupPCMoveTo: the server relays the mover's own C2S 1526 back to the same client, byte for byte.
/// </summary>
/// <remarks>
/// The reference pair is an exchange in which both bodies are 93 bytes and byte-identical, including the
/// actor handle at the start of the body, which the client supplies itself.
/// </remarks>
public class ViewGroupMove2750Tests
{
    
    private const string RetailMoveBody =
        "11466806000000000000000000000000000000000000000000000000000000" +
        "00000000000000000052d8324433433944d6e75f4252d8324433433944d6e7" +
        "5f4282c1c81c000000000002000200000000001cd9c81cdccb95a3b428dd01";

    private const uint RetailActorHandle = 0x06684611;

 /// <summary>
 /// The transport passes the handler 4 trailing bytes that the wire body does not carry (client input sequence and
 /// stamp), so the inbound payload is 97 bytes against a 93-byte body.
 /// </summary>
    private static byte[] InboundPayload()
    {
        var wire = Convert.FromHexString(RetailMoveBody);
        var payload = new byte[wire.Length + 4];
        wire.CopyTo(payload, 0);
        payload[^4] = 0xDE; payload[^3] = 0xAD; payload[^2] = 0xBE; payload[^1] = 0xEF;
        return payload;
    }

    [Fact]
    public void BuildMoveBody_ReproducesTheCapturedRetailEcho_ByteForByte()
    {
        var body = ViewGroupMoveHandler.BuildMoveBody(RetailActorHandle, InboundPayload());

        Assert.Equal(RetailMoveBody, Convert.ToHexString(body).ToLowerInvariant());
    }

    [Fact]
    public void BuildMoveBody_ProducesThe93ByteWireLength()
    {
        var body = ViewGroupMoveHandler.BuildMoveBody(RetailActorHandle, InboundPayload());

        Assert.Equal(93, body.Length);
    }

    [Fact]
    public void BuildMoveBody_PutsTheActorHandleAtOffsetZero()
    {
        
        
        var body = ViewGroupMoveHandler.BuildMoveBody(0xAABBCC11, InboundPayload());

        Assert.Equal(0xAABBCC11u, BitConverter.ToUInt32(body, 0));
        Assert.Equal(0x11, body[0] & 0xFF); 
    }

    [Fact]
    public void BuildMoveBody_DropsTheClientTrailerAndNotRealPayload()
    {
        var payload = InboundPayload();
        var body = ViewGroupMoveHandler.BuildMoveBody(RetailActorHandle, payload);

        
        Assert.Equal(0x01, body[^1]);
        Assert.DoesNotContain("deadbeef", Convert.ToHexString(body).ToLowerInvariant());
    }
}
