using System.Buffers.Binary;
using RO2.Application.Interfaces;
using RO2.Server.Zone.Handlers.Rmi;
using Xunit;
using RO2.Characters.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// 1578 ReqCancelReservedDeletePC and 2843 AnsCancelReservedDeletePC. The reply body is the request's own
/// u64 followed by u16 0.
/// </summary>
public class CancelReserveDelete2843Tests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Theory]
    
    [InlineData("57ac0fa8b3b6e00d")]
    
    [InlineData("cb8a0ea8b3b6e00d")]
    
    [InlineData("44c00ea8b3b6e00d")]
    
    [InlineData("7e270fa8b3b6e00d")]
    
    [InlineData("535f0fa8b3b6e00d")]
    
    [InlineData("3d7e0fa8b3b6e00d")]
    
    [InlineData("b49c0fa8b3b6e00d")]
    public void Restored_MatchesRetailFrame(string requestBodyHex)
    {
        
        var uid = BinaryPrimitives.ReadUInt64LittleEndian(Convert.FromHexString(requestBodyHex));

        var body = CancelReserveDeleteHandler.BuildAnsCancelReservedDeletePC(uid, CancelReserveDeleteHandler.ResultRestored);

        Assert.Equal(requestBodyHex + "0000", Hex(body));
    }

    [Fact]
    public void Refusal_Is15314_FromTheClientReaderAlone()
    {
        
        
        var body = CancelReserveDeleteHandler.BuildAnsCancelReservedDeletePC(0x0DE0B6B3A80FAC57UL,
            CancelReserveDeleteHandler.ResultNotRestored);

        Assert.Equal("57ac0fa8b3b6e00dd23b", Hex(body));
    }

    [Theory]
    [InlineData(CharacterRestoreResult.Restored, 0)]
    [InlineData(CharacterRestoreResult.AlreadyLive, 0)]
    [InlineData(CharacterRestoreResult.NotFound, 15314)]
    [InlineData(CharacterRestoreResult.Expired, 15314)]
    [InlineData(CharacterRestoreResult.NameTaken, 15314)]
    [InlineData(CharacterRestoreResult.AccountFull, 15314)]
    public void Result_IsZeroOnlyWhenTheCharacterIsLive(CharacterRestoreResult outcome, int expected)
    {
        Assert.Equal((ushort)expected, CancelReserveDeleteHandler.ResultFor(outcome));
    }

    [Fact]
    public void Standby_IsTheClientsSevenDays()
    {
        
        
        Assert.Equal(TimeSpan.FromDays(7), CancelReserveDeleteHandler.DeletionStandby);
    }
}
