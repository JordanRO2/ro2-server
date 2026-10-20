using RO2.Server.Login.Handlers.Rmi;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// RMI 12011 ReqModifySecondPassword: <c>[u32 oldPin][u32 newPin]</c>, each byte 0x80 | digit.
/// Every change is answered with 12508 = 1. No request has old equal to new, so the "same PIN" answer 4 (string 10424)
/// comes from the client's 12508 handling alone.
/// </summary>
public class ModifySecondPassword12011Tests
{
    
    
    
    
    
    [Theory]
    [InlineData("8189818982868180", "129137129137", "130134129128")]
    [InlineData("8181818181828384", "129129129129", "129130131132")]
    [InlineData("8181818182828282", "129129129129", "130130130130")]
    [InlineData("8182838481818181", "129130131132", "129129129129")]
    public void RetailChanges_AreNotTheSamePin(string bodyHex, string oldDigits, string newDigits)
    {
        var body = Convert.FromHexString(bodyHex);
        var oldPin = SecondPasswordHandler.PinDigits(body.AsSpan(0, 4));
        var newPin = SecondPasswordHandler.PinDigits(body.AsSpan(4, 4));

        
        Assert.Equal(oldDigits, oldPin);
        Assert.Equal(newDigits, newPin);
        Assert.False(SecondPasswordHandler.IsSamePin(oldPin, newPin));
    }

    [Fact]
    public void ANewPinEqualToTheOldOne_IsTheSamePin_AnsweredWithTheClientsCode4()
    {
        
        var body = Convert.FromHexString("8181818181818181");
        var oldPin = SecondPasswordHandler.PinDigits(body.AsSpan(0, 4));
        var newPin = SecondPasswordHandler.PinDigits(body.AsSpan(4, 4));

        Assert.True(SecondPasswordHandler.IsSamePin(oldPin, newPin));
        Assert.Equal(4, SecondPasswordHandler.PinSameAsCurrent);
    }
}
