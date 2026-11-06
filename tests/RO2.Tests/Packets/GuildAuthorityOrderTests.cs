using System.Buffers.Binary;
using System.Collections.Generic;
using RO2.Guilds.Domain;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

public class GuildAuthorityOrderTests
{
    // Retail's 2628 AnsGuildAuthority sends the rank records descending by index (lowest default rank first,
    // master last); our stored table is master-first. The builder sorts for the wire without reordering storage.
    [Fact]
    public void BuildAnsGuildAuthority_EmitsRanksDescendingByIndex()
    {
        var g = new Guild { Id = 1 };
        g.Ranks = new List<GuildRank>
        {
            new GuildRank { Index = 0, Name = "Master", Permissions = 0x0F },
            new GuildRank { Index = 1, Name = "Officer", Permissions = 0x01 },
            new GuildRank { Index = 9, Name = "Member", Permissions = 0x00 },
        };

        var body = GuildRosterPackets.BuildAnsGuildAuthority(g);

        Assert.Equal((ushort)3, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4)));
        const int rec = 6, len = GuildRosterPackets.RankRecordLen;   // record index byte sits at record+0
        Assert.Equal(9, body[rec]);              // highest index first
        Assert.Equal(1, body[rec + len]);
        Assert.Equal(0, body[rec + 2 * len]);
    }
}
