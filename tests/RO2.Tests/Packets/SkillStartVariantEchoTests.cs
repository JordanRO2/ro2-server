using System.Buffers.Binary;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// 1508 ReqSkillStart answered by 2715 and 2716. The answer echoes the request's target, second position triple and
/// variant fields, read through the handler's ReadAnswerEcho.
/// </summary>
/// <remarks>
/// The three pairs are reference payloads, one for each variant value (0, 1 and 257). In every observed case the
/// variant echoed in 2715 and in 2716 matches the request. The cooldown field is taken from the reference payload and
/// is not what these tests check. Every other byte of the 2715 comes from the request.
/// </remarks>
public class SkillStartVariantEchoTests
{
    public static TheoryData<string, string> RetailPairs => new()
    {
        
        
        {
            "83e74c00978b3f4486a53744ad57434311e81b0701b47774ea097cf30200000000978b3f4486a53744ad5743430000",
            "000083e74c0011e81b070100000000978b3f4486a53744ad5743430000"
        },
        
        {
            "1237030020343744882153449d83b6410000000000afdcc329157cf3020000000071163b44b35f53441447bb410100",
            "00001237030000000000000000403f71163b44b35f53441447bb410100"
        },
        
        {
            "86d701008ffa3444641c0b44f23d5d420000000000cf1947a7ef7bf302c38207402418344405030c449a3e70420101",
            "000086d7010000000000001f8583402418344405030c449a3e70420101"
        },
    };

    [Theory]
    [MemberData(nameof(RetailPairs))]
    public void Ack2715BuiltFromTheRequestReproducesRetail(string requestHex, string retail2715Hex)
    {
        var request = Convert.FromHexString(requestHex);
        var retail = Convert.FromHexString(retail2715Hex);
        Assert.Equal(47, request.Length);

        var echo = SkillStartHandler.ReadAnswerEcho(request);
        var built = SkillPackets.BuildUseSkillStartAck2715(
            BinaryPrimitives.ReadUInt32LittleEndian(request),
            echo.TargetHandle,
            BinaryPrimitives.ReadSingleLittleEndian(retail.AsSpan(11)),
            echo.X, echo.Y, echo.Z,
            variant: echo.Variant);

        Assert.Equal(retail2715Hex, Convert.ToHexString(built), ignoreCase: true);
    }

    [Theory]
    [MemberData(nameof(RetailPairs))]
    public void Notify2716CarriesTheSameTargetAndVariantAsRetail2715(string requestHex, string retail2715Hex)
    {
        var request = Convert.FromHexString(requestHex);
        var retail = Convert.FromHexString(retail2715Hex);
        var echo = SkillStartHandler.ReadAnswerEcho(request);

        var body = SkillPackets.BuildUseSkillStart2716(
            0x071BE811, BinaryPrimitives.ReadUInt32LittleEndian(request), 100, 50,
            echo.TargetHandle, variant: echo.Variant);

        
        
        Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(retail.AsSpan(6)),
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(2 + 49)));
        Assert.Equal(BinaryPrimitives.ReadUInt16LittleEndian(retail.AsSpan(27)),
            BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2 + 53)));
        Assert.Equal(retail[10], body[2 + 48]);
    }
}
