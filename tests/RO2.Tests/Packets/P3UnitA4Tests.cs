using System.Buffers.Binary;
using RO2.Domain.GameData.Tables;
using RO2.Network.Packets;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// RMI 2538 OnAckCraftLearn refusals: the 8-byte answers and the result code each server-side check maps to.
/// </summary>
public class CraftLearnRefusal2538Tests
{
    private static readonly Lazy<IReadOnlyList<CraftItemList>> Recipes =
        new(() => BankBagSizeChange2345Tests.Table<CraftItemList>("Craft_ItemList.json"));

    [Theory]
    
    [InlineData(CraftRecipeStore.ResultNoSuchRecipe, "040000005d580000")]
    [InlineData(CraftRecipeStore.ResultAlreadyLearned, "0400000062580000")]
    [InlineData(CraftRecipeStore.ResultProfessionLevelTooLow, "040000005e580000")]
    public void Refusal2538_IsRetailsFrame_ByteForByte(ushort code, string retailHex)
    {
        Assert.Equal(retailHex.ToUpperInvariant(), Convert.ToHexString(CraftRecipeStore.BuildAckCraftLearnRefusal2538(code)));
    }

    [Fact]
    public void Refusal2538_RejectsResultZero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CraftRecipeStore.BuildAckCraftLearnRefusal2538(0));
    }

    [Fact]
    public void Codes_MapToTheClientsStrings()
    {
        
        Assert.Equal(22621, CraftRecipeStore.ResultNoSuchRecipe);
        Assert.Equal(22622, CraftRecipeStore.ResultProfessionLevelTooLow);
        Assert.Equal(22623, CraftRecipeStore.ResultNotEnoughMoney);
        Assert.Equal(22624, CraftRecipeStore.ResultWrongNpc);
        Assert.Equal(22625, CraftRecipeStore.ResultWrongProfession);
        Assert.Equal(22626, CraftRecipeStore.ResultAlreadyLearned);
    }

    [Fact]
    public void RefusalCode_FollowsTheSameChecksAsRefusalReason()
    {
        var table = Recipes.Value;
        var known = CraftRecipeStore.KnownRecipeIds(table, 3, Array.Empty<ushort>());
        CraftItemList Row(ushort id) => table.First(r => r.ID == id);

        
        
        Assert.Equal(CraftRecipeStore.ResultNoSuchRecipe, CraftListLearnHandler.RefusalCode(null, 1700, 3, known, 1_000_000));
        Assert.Equal(0, CraftListLearnHandler.RefusalCode(Row(644), 644, 2, CraftRecipeStore.KnownRecipeIds(table, 2, Array.Empty<ushort>()), 1_000_000));
        
        Assert.Equal(CraftRecipeStore.ResultWrongProfession, CraftListLearnHandler.RefusalCode(Row(142), 142, 3, known, 1_000_000));
        Assert.Equal(CraftRecipeStore.ResultNotEnoughMoney, CraftListLearnHandler.RefusalCode(Row(10), 10, 3, known, Row(10).Learn_NeedMoney - 1));
        
        Assert.Equal(CraftRecipeStore.ResultAlreadyLearned, CraftListLearnHandler.RefusalCode(Row(1), 1, 3, known, 1_000_000));
        
        Assert.Equal(0, CraftListLearnHandler.RefusalCode(Row(10), 10, 3, known, Row(10).Learn_NeedMoney));
        Assert.Null(CraftListLearnHandler.RefusalReason(Row(10), 10, 3, known, Row(10).Learn_NeedMoney));
    }
}

/// <summary>
/// RMI 1333 ReqItemWeaveUse, parsed at the client builder's blob offsets. The layout comes from the client builder
/// alone; no frame of this opcode has been observed, so it is not verified against the reference server.
/// </summary>
public class ItemWeaveUse1333Tests
{
    private static byte[] Body()
    {
        var blob = new byte[34];
        BinaryPrimitives.WriteUInt32LittleEndian(blob, 0xE3160022u);      
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), 0x0011A6E2u);
        blob[8] = 1;                                                        
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(9), 0x11111111u);  
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(13), 0x22222222u); 
        blob[17] = 2;                                                       
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(18), 0x33333333u); 
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(22), 0x44444444u); 
        blob[26] = 3; blob[27] = 4; blob[28] = 5; blob[29] = 6;            
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(30), 0x0707);  
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(32), 0x0808);  
        return [0x01, 34, .. blob];                                         
    }

    [Fact]
    public void Request1333_IsReadAtTheBuildersOffsets()
    {
        var body = Body();
        Assert.True(((ReadOnlySpan<byte>)body).TryGetCompactBlob(CompactLengthCheck.Strict, out var blob));
        Assert.True(ItemWeaveUseHandler.TryParse(blob, out var r));
        Assert.Equal(new ItemWeaveUseHandler.WeaveUseRequest(0x0011A6E2u, 1, 0x11111111u, 0x22222222u, 2, 0x33333333u,
            0x44444444u, 3, 4, 5, 6, 0x0707, 0x0808), r);
    }

    [Fact]
    public void Request1333_WithAnotherSizeOrTag_IsRejected()
    {
        var body = Body();
        Assert.True(((ReadOnlySpan<byte>)body).TryGetCompactBlob(CompactLengthCheck.Strict, out var blob));
        var badTag = blob.ToArray();
        badTag[2] ^= 1;
        Assert.False(ItemWeaveUseHandler.TryParse(badTag, out _));
        Assert.False(ItemWeaveUseHandler.TryParse(blob[..33], out _));
    }
}

/// <summary>
/// RMI 1330 ReqEnforceItem. Only the variable-blob unwrap and the conventional header read are pinned. There is no
/// reference frame, so this layout is not verified.
/// </summary>
public class ReqEnforceItem1330Tests
{
    [Fact]
    public void ConventionalHeader_IsReadFromBlobOffsets()
    {
        byte[] blob = [0x0C, 0x00, 0x34, 0x12, 0x11, 0xA6, 0xE2, 0x00, 0xFF, 0xFF, 0xFF, 0xFF];
        Assert.Equal(((ushort)12, (ushort)0x1234, 0x00E2A611u), ReqEnforceItemHandler.ReadConventionalHeader(blob));
        Assert.Null(ReqEnforceItemHandler.ReadConventionalHeader(blob.AsSpan(0, 7)));
    }
}

/// <summary>
/// RMI 2867 AnsGameMallItemSalesCount: the 10-byte body (U16, U32, U32, no envelope). The consumer ignores the
/// U32 fields on result 0. No frame of this opcode has been observed; not verified.
/// </summary>
public class GameMallItemSalesCount2867Tests
{
    [Fact]
    public void Answer2867_IsTheReadersFixed10Bytes()
    {
        Assert.Equal("00000000000000000000", Convert.ToHexString(GameMallItemSalesCountHandler.BuildAnsGameMallItemSalesCount2867(0, 0, 0)));
        Assert.Equal("3412785634120DF0ADBA",
            Convert.ToHexString(GameMallItemSalesCountHandler.BuildAnsGameMallItemSalesCount2867(0x1234, 0x12345678, 0xBAADF00D)));
    }
}
