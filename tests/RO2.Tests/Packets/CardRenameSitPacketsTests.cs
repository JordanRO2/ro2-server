using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-exact checks for the card-status, rename, present-password and sit paths. Expected values are
/// reference frame bodies.
/// </summary>
public class CardRenameSitPacketsTests
{
    [Fact]
    public void UpdateCardStatus2589_TakeOffRecord_ReproducesRetailFrame()
    {
        
        var expected = Convert.FromHexString("0c00000000000100ec6f5401011b0a00");
        var body = CardStatusPackets.BuildUpdateCardStatus2589(new[]
        {
            new CardStatusRecord(0x01546FECu, 1, 0x1B, 0x0A, 0),
        });
        Assert.Equal(expected, body);
    }

    [Fact]
    public void UpdateCardStatus2589_Empty_ReproducesRetailAnswerTo1441()
    {
        var expected = Convert.FromHexString("0400000000000000");
        var body = CardStatusPackets.BuildUpdateCardStatus2589(Array.Empty<CardStatusRecord>());
        Assert.Equal(expected, body);
    }

    [Fact]
    public void ChangeState2398_Sit_ReproducesRetailFrameAfter1308()
    {
        var expected = Convert.FromHexString("011b1b00eae211ee2c0711ee2c07090000000000000200000000000000");
        var body = CombatPackets.BuildChangeState2398(0x072CEE11u, ActorStateNotifyPackets.StateEnum(1), trailing: 2);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void RenameRequest_ParsesRetail1563()
    {
        var body = Convert.FromHexString(
            "ec142c6ea73d040e02010a6900700069006e0065006100700070006c0065000201084a006100790061004e006f0076006100");
        Assert.True(CharacterRename.TryParse(body, out var req));
        Assert.Equal(0x0E043DA76E2C14ECul, req.ScrollUid);
        Assert.Equal("ipineapple", req.CurrentName);
        Assert.Equal("JayaNova", req.NewName);
    }

    [Fact]
    public void ProudWStr_ReadsRetail1600Body()
    {
        var body = Convert.FromHexString("0201000201043000300030003000");
        int pos = 0;
        Assert.True(ProudWStr.TryRead(body, ref pos, out var oldPw));
        Assert.True(ProudWStr.TryRead(body, ref pos, out var newPw));
        Assert.Empty(oldPw);
        Assert.Equal("0000", newPw);
        Assert.Equal(body.Length, pos);
    }

    [Theory]
    [InlineData("0000", true)]
    [InlineData("1234", true)]
    [InlineData("123", false)]
    [InlineData("12a4", false)]
    [InlineData("", false)]
    public void PresentPassword_IsFourDigits(string password, bool valid) =>
        Assert.Equal(valid, RO2.Persistence.Mongo.Repositories.PresentPasswordRepository.IsValidPassword(password));
}
