using RO2.Server.Login.Handlers.Rmi;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests the body layout of RMI 12013 ReqAeriaGamesLogin: [WStr code][WStr second] (<see cref="AeriaLoginHandler"/>).
/// Each WStr is [u8 widthTag][CompactLen n][n * widthTag]; the body uses narrow strings (widthTag 1).
/// No reference frame exists, so this layout is unverified.
/// </summary>
public class AeriaLogin12013Tests
{
    [Fact]
    public void TwoNarrowStrings_AreConsumedWholly()
    {
        
        var body = Convert.FromHexString("01" + "0105" + "4142434445" + "01" + "0102" + "7879");
        int pos = 0;
        Assert.True(AeriaLoginHandler.TryReadWStrLength(body, ref pos, out int first));
        Assert.True(AeriaLoginHandler.TryReadWStrLength(body, ref pos, out int second));
        Assert.Equal(5, first);
        Assert.Equal(2, second);
        Assert.Equal(body.Length, pos);
    }

    [Fact]
    public void WideString_CountsCharactersNotBytes()
    {
        var body = Convert.FromHexString("02" + "0102" + "41004200");
        int pos = 0;
        Assert.True(AeriaLoginHandler.TryReadWStrLength(body, ref pos, out int chars));
        Assert.Equal(2, chars);
        Assert.Equal(body.Length, pos);
    }

    [Theory]
    [InlineData("")]               
    [InlineData("030101")]         
    [InlineData("01010541")]       
    public void Malformed_IsRejectedWithoutMoving(string hex)
    {
        var body = Convert.FromHexString(hex);
        int pos = 0;
        Assert.False(AeriaLoginHandler.TryReadWStrLength(body, ref pos, out _));
        Assert.Equal(0, pos);
    }
}
