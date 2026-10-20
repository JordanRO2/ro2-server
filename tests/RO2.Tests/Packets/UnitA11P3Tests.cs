using RO2.Domain.Entities;
using System.Buffers.Binary;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Unit A11 (P3): 1520 ReqCountOverFieldPvP, 1422 Req_CLZSR_INIT_KEYCONFIG, the refusal of 1477
/// ReqDeletePrivateShopCraft, 2969 NfyEntryPartyError after 1658 ReqTRoomJoin, and 1581 ReqExtendCacheItem
/// and 1703 ReqExtendTimeSetItem.
/// </summary>
/// <remarks>
/// Tests without a reference frame pin the layout that the client's reader or builder defines; they are not
/// verified against retail traffic.
/// </remarks>
public class UnitA11P3Tests
{
    
    [Fact]
    public void CountOverFieldPvP_DumpFrame_EndsTheDuelAndTheLosersAnswerMatchesRetail()
    {
        
        
        var req = Convert.FromHexString("010c0c0056e400000000f1030000");
        Assert.True(CountOverFieldPvPHandler.TryParseDuelId(req, out var duelId));
        Assert.Equal(0x3F1u, duelId);

        const uint sender = 0x046EFF11, other = 0x0462DB11;
        var duel = new FieldPvPService.Duel
        {
            Id = duelId, Challenger = other, Target = sender, State = FieldPvPService.DuelState.Accepted,
        };
        Assert.True(CountOverFieldPvPHandler.EndsDuel(duel, sender));

        
        var toSender = FieldPvPPackets.BuildResult2736(sender, other, sender, FieldPvPPackets.ResultOutOfRange);
        Assert.Equal("011212005ae411ff6e0411db620411ff6e040100", Convert.ToHexString(toSender).ToLowerInvariant());
    }

    [Fact]
    public void CountOverFieldPvP_WinnersCopy_MatchesTheArchiveFrame()
    {
        
        
        var body = FieldPvPPackets.BuildResult2736(0x0A3F7E11, 0x0A3F7E11, 0x0A410111, FieldPvPPackets.ResultOutOfRange);
        Assert.Equal("011212005ae4117e3f0a117e3f0a1101410a0100", Convert.ToHexString(body).ToLowerInvariant());
    }

    [Fact]
    public void CountOverFieldPvP_PendingForeignOrMalformed_DoesNotEndADuel()
    {
        var pending = new FieldPvPService.Duel { Id = 7, Challenger = 1, Target = 2, State = FieldPvPService.DuelState.Pending };
        var accepted = new FieldPvPService.Duel { Id = 7, Challenger = 1, Target = 2, State = FieldPvPService.DuelState.Accepted };
        Assert.False(CountOverFieldPvPHandler.EndsDuel(pending, 2));
        Assert.False(CountOverFieldPvPHandler.EndsDuel(accepted, 3));
        Assert.True(CountOverFieldPvPHandler.EndsDuel(accepted, 1));

        Assert.False(CountOverFieldPvPHandler.TryParseDuelId(Convert.FromHexString("010c0c0056e400000000f103"), out _));
        
        Assert.False(CountOverFieldPvPHandler.TryParseDuelId(Convert.FromHexString("010c0c0055e400000000f1030000"), out _));
    }

    
    [Fact]
    public void InitKeyConfig_BuilderLayout_ModeAtBodyPlus4()
    {
        
        
        Assert.True(InitKeyConfigHandler.TryParseMode(Convert.FromHexString("11ff6e040100"), out var mode));
        Assert.Equal((ushort)1, mode);
        Assert.False(InitKeyConfigHandler.TryParseMode(Convert.FromHexString("11ff6e0401"), out _));

        
        
        Assert.False(InitKeyConfigHandler.ClearsStoredSet(0));
        Assert.True(InitKeyConfigHandler.ClearsStoredSet(1));
    }

    [Fact]
    public void InitKeyConfig_ResetOfTheStoredSet_NextLogin2558CarriesNoBindingsAndTheSameMode()
    {
        var stored = new KeyConfigDocument
        {
            Id = 42, Mode = 1, Entries = Convert.FromHexString("68000100690002006a000300"), UpdatedUtc = DateTime.UnixEpoch,
        };
        var reset = InitKeyConfigHandler.AfterReset(stored, DateTime.UnixEpoch.AddDays(1));
        Assert.NotNull(reset);
        Assert.Equal(42u, reset.Id);
        Assert.Equal((ushort)1, reset.Mode);
        Assert.Empty(reset.Entries);

        
        
        var body = KeyConfigCodec.BuildAccountConfig2558(0x046EFF11, reset);
        Assert.Equal("0700000011ff6e04010000", Convert.ToHexString(body).ToLowerInvariant());

        Assert.Null(InitKeyConfigHandler.AfterReset(null, DateTime.UnixEpoch));
        Assert.Null(InitKeyConfigHandler.AfterReset(new KeyConfigDocument { Id = 42, Mode = 1 }, DateTime.UnixEpoch));
    }

    
    [Fact]
    public void DeletePrivateShopCraft_SecondRemoveOfTheSameRecipe_IsRefusedAsRetailDid()
    {
        
        
        var req = Convert.FromHexString("4305");
        var craftId = BinaryPrimitives.ReadUInt16LittleEndian(req);
        var seller = new PlayerState { EntityId = 0x00100011u };
        PrivateShopRegistry.SetCraft(seller, craftId, 1000);

        var first = PrivateShopRegistry.RemoveCraft(seller, craftId);
        var second = PrivateShopRegistry.RemoveCraft(seller, craftId);
        Assert.True(first);
        Assert.False(second);

        var a1 = DeletePrivateShopCraftHandler.BuildAnswer(
            first ? DeletePrivateShopCraftHandler.ResultRemoved : DeletePrivateShopCraftHandler.ResultNotOffered, craftId);
        var a2 = DeletePrivateShopCraftHandler.BuildAnswer(
            second ? DeletePrivateShopCraftHandler.ResultRemoved : DeletePrivateShopCraftHandler.ResultNotOffered, craftId);
        Assert.Equal("00004305", Convert.ToHexString(a1).ToLowerInvariant());
        Assert.Equal("e4594305", Convert.ToHexString(a2).ToLowerInvariant());

        
        Assert.Equal("00005506", Convert.ToHexString(
            DeletePrivateShopCraftHandler.BuildAnswer(DeletePrivateShopCraftHandler.ResultRemoved, 0x0655)).ToLowerInvariant());
    }

    
    [Fact]
    public void TRoomJoin_WithNoPendingMatch_Answers2969AsRetailDid()
    {
        
        
        Assert.Equal("175f", Convert.ToHexString(
            EntryPartyErrorPackets.Build(EntryPartyErrorPackets.StaleAnswerCode(accept: true))).ToLowerInvariant());
        Assert.Equal("195f", Convert.ToHexString(
            EntryPartyErrorPackets.Build(EntryPartyErrorPackets.StaleAnswerCode(accept: false))).ToLowerInvariant());
    }

    
    [Fact]
    public void ExtendCacheItem_RequestDescriptorsAndRefusal_MatchTheClientsBuilderAndReader()
    {
        
        
        var req = Convert.FromHexString("013d95d13912a83d040e" + "041bc5729212a83d040e");
        Assert.True(ExtendCacheItemHandler.TryParse(req, out var dragged, out var target));
        Assert.Equal(0x0E043DA81239D195ul, dragged);
        Assert.Equal(0x0E043DA8129272C5ul, target);
        Assert.False(ExtendCacheItemHandler.TryParse(req.AsSpan(0, 19), out _, out _));

        
        
        var refusal = ExtendCacheItemHandler.BuildRefusal(ExtendCacheItemHandler.ResultNoItem);
        Assert.Equal(137, refusal.Length);
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(refusal));
        Assert.All(refusal.AsSpan(2).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal((ushort)3, BinaryPrimitives.ReadUInt16LittleEndian(
            ExtendCacheItemHandler.BuildRefusal(ExtendCacheItemHandler.ResultCannotExtend)));
    }

    
    [Fact]
    public void ExtendTimeSetItem_DumpFrame_AllZeroRequestGetsResult1LikeRetail()
    {
        
        
        
        var req = new byte[17];
        Assert.True(ExtendTimeSetItemHandler.TryParse(req, out var flag, out var targetUid, out var ticketUid));
        Assert.Equal(0, flag);
        Assert.Equal(0ul, targetUid);
        Assert.Equal(0ul, ticketUid);

        var retail = Convert.FromHexString("010000000000000098e267160000000000");
        var ours = ExtendTimeSetItemHandler.BuildRefusal(ExtendTimeSetItemHandler.ResultNoItem);
        Assert.Equal(retail.Length, ours.Length);
        Assert.Equal(BinaryPrimitives.ReadUInt16LittleEndian(retail), BinaryPrimitives.ReadUInt16LittleEndian(ours));
        Assert.Equal("0100000000000000000000000000000000", Convert.ToHexString(ours).ToLowerInvariant());
    }

    [Fact]
    public void ExtendTimeSetItem_BuilderLayout_FlagThenTargetThenTicket()
    {
        
        
        var req = Convert.FromHexString("01" + "c5729212a83d040e" + "95d13912a83d040e");
        Assert.True(ExtendTimeSetItemHandler.TryParse(req, out var flag, out var targetUid, out var ticketUid));
        Assert.Equal(1, flag);
        Assert.Equal(0x0E043DA8129272C5ul, targetUid);
        Assert.Equal(0x0E043DA81239D195ul, ticketUid);
        Assert.Equal((ushort)4, BinaryPrimitives.ReadUInt16LittleEndian(
            ExtendTimeSetItemHandler.BuildRefusal(ExtendTimeSetItemHandler.ResultCannotUse)));
    }
}
