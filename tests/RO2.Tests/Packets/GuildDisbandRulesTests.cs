using RO2.Guilds.Domain;
using RO2.Server.Zone.Handlers.Rmi;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>The pure disband precondition and the two distinct wire mappings of its reasons (2688 vs 2562).</summary>
public class GuildDisbandRulesTests
{
    private const uint Master = 4242;

    private static Guild Lone()
    {
        var g = new Guild { MasterCharId = Master };
        g.Members.Add(new GuildMember { CharId = Master });
        return g;
    }

    [Fact]
    public void Reasons_follow_the_check_order()
    {
        Assert.Equal(DisbandRefusalReason.NoGuild, GuildDisbandRules.DisbandRefusal(null, Master));
        Assert.Equal(DisbandRefusalReason.None, GuildDisbandRules.DisbandRefusal(Lone(), Master));

        var notMaster = Lone();
        notMaster.MasterCharId = Master + 1;
        Assert.Equal(DisbandRefusalReason.NotMaster, GuildDisbandRules.DisbandRefusal(notMaster, Master));

        // Members outrank the vault, storage outranks zeny.
        var g = Lone();
        g.Members.Add(new GuildMember { CharId = Master + 1 });
        g.Storage.Add(new GuildStorageItem { Uid = 1, ItemId = 1 });
        g.Gold = 1;
        Assert.Equal(DisbandRefusalReason.HasOtherMembers, GuildDisbandRules.DisbandRefusal(g, Master));
        g.Members.RemoveAt(1);
        Assert.Equal(DisbandRefusalReason.VaultNotEmpty, GuildDisbandRules.DisbandRefusal(g, Master));
        g.Storage.Clear();
        Assert.Equal(DisbandRefusalReason.FundsNotEmpty, GuildDisbandRules.DisbandRefusal(g, Master));
    }

    [Fact]
    public void Vault_and_funds_refusals_are_45_on_2688_but_41_on_2562()
    {
        foreach (var r in new[] { DisbandRefusalReason.VaultNotEmpty, DisbandRefusalReason.FundsNotEmpty })
            Assert.Equal((ushort)41, GuildDeleteHandler.DeleteResult(r));
        Assert.Equal((ushort)45, GuildDelPossibleHandler.DisbandRefusal(new Guild { MasterCharId = Master, Gold = 1 }
            .With(Master), Master));
        Assert.Equal((ushort)0, GuildDeleteHandler.DeleteResult(DisbandRefusalReason.None));
        Assert.Equal((ushort)7, GuildDeleteHandler.DeleteResult(DisbandRefusalReason.NoGuild));
        Assert.Equal((ushort)19, GuildDeleteHandler.DeleteResult(DisbandRefusalReason.NotMaster));
        Assert.Equal((ushort)9, GuildDeleteHandler.DeleteResult(DisbandRefusalReason.HasOtherMembers));
    }
}

internal static class GuildTestExt
{
    public static Guild With(this Guild g, uint member) { g.Members.Add(new GuildMember { CharId = member }); return g; }
}
