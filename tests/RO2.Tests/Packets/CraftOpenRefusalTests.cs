using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// RMI 1347 ReqCraftListOpen -> 2537 OnAckCraftOpen, for both request forms.
/// </summary>
/// <remarks>
/// The 2537 echoes the requested handle. NPC-tag (0x13) handles get result 0. Handles the server never spawned
/// get 20001, which the client shows as string_ui 2511 before it stops.
/// </remarks>
public class CraftOpenRefusalTests
{
 /// <summary>Reference 1347 request body and its expected 2537 answer.</summary>
    private const string RetailOpenHex = "010e0e0013e41133180c0000132f1100";

 /// <summary>Reference 1347 request body and its expected 2537 answer.</summary>
    private const string RetailRefusalHex = "010e0e0013e411eced0e214e62000050";

    [Fact]
    public void SuccessFormMatchesRetailByteForByte()
    {
        var body = CraftOpenHandler.BuildAckCraftOpen2537(0x0C183311u, 0, 0x00112F13u);
        Assert.Equal(Convert.FromHexString(RetailOpenHex), body);
    }

    [Fact]
    public void RefusalFormMatchesRetailByteForByte()
    {
        var body = CraftOpenHandler.BuildAckCraftOpen2537(0x0EEDEC11u, CraftOpenHandler.ResultNpcNotFound, 0x50000062u);
        Assert.Equal(Convert.FromHexString(RetailRefusalHex), body);
    }

    [Fact]
    public void AHandleThatIsNoNpcIsRefusedWithTheClientsNpcNotFoundCode()
    {
        
        Assert.Equal(CraftOpenHandler.ResultNpcNotFound, CraftOpenHandler.OpenResult(0x50000062u, null));
        Assert.Equal(CraftOpenHandler.ResultNpcNotFound,
            CraftOpenHandler.OpenResult(0x50000062u, new NpcRuntimeState { EntityOid = 0x50000062u }));
        Assert.Equal((short)20001, CraftOpenHandler.ResultNpcNotFound);
    }

    [Fact]
    public void AnNpcHandleOpensOnlyWhenThisServerSpawnedIt()
    {
        Assert.Equal(CraftOpenHandler.ResultNpcNotFound, CraftOpenHandler.OpenResult(0x00112F13u, null));
        Assert.Equal((short)0,
            CraftOpenHandler.OpenResult(0x00112F13u, new NpcRuntimeState { EntityOid = 0x00112F13u }));
    }
}
