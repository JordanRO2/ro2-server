using System;
using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Crash-class gate for the shop / item-operation S2C notifies. Every opcode here is compact-BA:
/// the client's compact-length decoder rejects any body whose first byte is not a marker in
/// {1,2,4,8}, hands the per-opcode stub a NULL ByteArray, and crashes on the deref. Each raw send
/// these builders replaced was a live remote crash.
/// </summary>
/// <remarks>
/// The envelope check below is the assertion that prevents the crash class: a valid marker, a
/// self-consistent compact length and selfLen, and the expected msgTag. The byte-exact
/// tests pin the layouts of the fully-specified bodies.
/// </remarks>
public class ShopCrashSweepTests
{
    private static readonly byte[] Markers = { 1, 2, 4, 8 };

    /// <summary>
    /// Asserts a well-formed compact-BA envelope: valid marker, compactLen == selfLen == blob
    /// length, expected msgTag, and the exact expected total body length. Works for marker 1 and 2.
    /// </summary>
    private static void AssertEnvelope(byte[] body, ushort expectedTag, int expectedLen)
    {
        Assert.Equal(expectedLen, body.Length);
        Assert.Contains(body[0], Markers);

        int marker = body[0];
        var blob = body.AsSpan(1 + marker);

        long compactLen = marker == 1
            ? body[1]
            : BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1));
        Assert.Equal(blob.Length, (int)compactLen);

        // selfLen == 2 + payloadLen, and payloadLen == blob.Length - 2, so selfLen == blob.Length.
        Assert.Equal(blob.Length, BinaryPrimitives.ReadUInt16LittleEndian(blob));
        Assert.Equal(expectedTag, BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(2)));
    }

    // ---- byte-exact layout tests ----

    [Fact]
    public void ChangeHideFlag2380_ReproducesRetailFrame()
    {
        // Expected body: [01 0e][selfLen 0e00][msgTag 21e4=58401][u32 0][handle][hideFlag u16]
        var expected = Convert.FromHexString("010e0e0021e40000000011b3ec063903");
        var body = ShopItemPackets.BuildChangeHideFlag2380(0x06ECB311u, 0x0339);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void AckMakeCraftItem2539_ReproducesRetailFrame()
    {
        // Expected body: [01 0a][selfLen 0a00][msgTag 16e4=58390][handle][u16 0]
        var expected = Convert.FromHexString("010a0a0016e41127f7070000");
        var body = ShopItemPackets.BuildAckMakeCraftItem2539(0x07F72711u);
        Assert.Equal(expected, body);
    }

    // 2999 and 3006 have no byte-exact test here: their layouts, including the request echo and the owner
    // commission they carry, are pinned in CraftAndRefineShopPacketsTests on the two-argument builders the
    // handlers send.

    [Fact]
    public void NfyBeginRefineShop3003_ReproducesRetailFrame()
    {
        // Expected body (flag slot 0): [01 14][selfLen 1400][msgTag 94e4=58516][handle x2][u32 0][u32 0]
        var expected = Convert.FromHexString("0114140094e411c32a0711c32a070000000000000000");
        var body = ShopItemPackets.BuildNfyBeginRefineShop3003(0x072AC311u);
        Assert.Equal(expected, body);
    }

    // ---- envelope + length + marker gate for all 11 opcodes ----

    [Fact]
    public void ChangeHideFlag2380_HasValidEnvelope() =>
        AssertEnvelope(ShopItemPackets.BuildChangeHideFlag2380(0x00100011u, 0x015E),
            ShopItemPackets.MsgTagChangeHideFlag, 16);

    [Fact]
    public void ItemEnforceResult2360_HasValidEnvelope() =>
        AssertEnvelope(ShopItemPackets.BuildItemEnforceResult2360(),
            ShopItemPackets.MsgTagItemEnforceResult, 23);

    [Fact]
    public void BreakDownItem2417_HasValidEnvelope() =>
        AssertEnvelope(ShopItemPackets.BuildBreakDownItem2417(0x00100011u),
            ShopItemPackets.MsgTagBreakDownItem, 25);

    [Fact]
    public void AckMakeCraftItem2539_HasValidEnvelope() =>
        AssertEnvelope(ShopItemPackets.BuildAckMakeCraftItem2539(0x00100011u),
            ShopItemPackets.MsgTagAckMakeCraftItem, 12);

    [Fact]
    public void BeginRefineShop2999_HasValidEnvelope() =>
        AssertEnvelope(ShopItemPackets.BuildBeginRefineShop2999(0x00100011u, 0),
            ShopItemPackets.MsgTagBeginRefineShop, 18);

    [Fact]
    public void NfyBeginRefineShop3003_HasValidEnvelope() =>
        AssertEnvelope(ShopItemPackets.BuildNfyBeginRefineShop3003(0x00100011u),
            ShopItemPackets.MsgTagNfyBeginRefineShop, 22);

    [Fact]
    public void RandomRefineCommission3006_HasValidEnvelope() =>
        AssertEnvelope(ShopItemPackets.BuildRandomRefineCommission3006(0x00100011u, 0),
            ShopItemPackets.MsgTagRandomRefineCommission, 18);

    [Fact]
    public void BuyItem2375_HasValidEnvelope() =>
        AssertEnvelope(ShopItemPackets.BuildBuyItem2375(0x00100011u, 1_000_000L, 22250002u, new byte[126]),
            ShopItemPackets.MsgTagBuyItem, 170);   // body length: 43 empty / 170 one item / 296 two

    [Fact]
    public void CardEnforceItem2899_HasValidEnvelope() =>
        AssertEnvelope(ShopItemPackets.BuildCardEnforceItem2899(0x00100011u),
            ShopItemPackets.MsgTagCardEnforceItem, 141);

    [Fact]
    public void RefineItem2997_HasValidEnvelope() =>
        AssertEnvelope(ShopItemPackets.BuildRefineItem2997(0x00100011u),
            ShopItemPackets.MsgTagRefineItem, 145);

    [Fact]
    public void RandomRefineItem3009_HasValidEnvelope() =>
        AssertEnvelope(ShopItemPackets.BuildRandomRefineItem3009(0x00100011u),
            ShopItemPackets.MsgTagRandomRefineItem, 139);

    [Fact]
    public void EveryBuilderStartsWithAValidCompactMarker()
    {
        // The single invariant: the first body byte is a valid compact marker for every S2C body these
        // handlers can emit.
        var bodies = new[]
        {
            ShopItemPackets.BuildChangeHideFlag2380(0x00100011u, 1),
            ShopItemPackets.BuildItemEnforceResult2360(),
            ShopItemPackets.BuildBreakDownItem2417(0x00100011u),
            ShopItemPackets.BuildAckMakeCraftItem2539(0x00100011u),
            ShopItemPackets.BuildBeginRefineShop2999(0x00100011u, 0),
            ShopItemPackets.BuildNfyBeginRefineShop3003(0x00100011u),
            ShopItemPackets.BuildRandomRefineCommission3006(0x00100011u, 0),
            ShopItemPackets.BuildBuyItem2375(0x00100011u, 1_000_000L, 22250002u, new byte[126]),
            ShopItemPackets.BuildCardEnforceItem2899(0x00100011u),
            ShopItemPackets.BuildRefineItem2997(0x00100011u),
            ShopItemPackets.BuildRandomRefineItem3009(0x00100011u),
        };
        foreach (var b in bodies)
            Assert.Contains(b[0], Markers);
    }
}
