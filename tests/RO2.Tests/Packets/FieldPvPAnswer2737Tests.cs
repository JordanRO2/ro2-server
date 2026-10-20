using System.Buffers.Binary;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Answers to 1518 ReqAcceptFiledPvP that were previously handled incorrectly: the challenger's timeout
/// (answer 4 yields 28), either player's answer 3 on a held duel (14 to both, also once it is accepted),
/// and a late answer about a duel that no longer exists (23 to the sender).
/// </summary>
public class FieldPvPAnswer2737Tests
{
    [Fact]
    public void Timeout_28_MatchesRetail()
    {
        
        var req = Convert.FromHexString("0116160054e40000000004001127e50811aae60814040000");
        var answer = BinaryPrimitives.ReadUInt16LittleEndian(req.AsSpan(10));
        var challenger = BinaryPrimitives.ReadUInt32LittleEndian(req.AsSpan(12));
        var target = BinaryPrimitives.ReadUInt32LittleEndian(req.AsSpan(16));
        var duelId = BinaryPrimitives.ReadUInt32LittleEndian(req.AsSpan(20));
        Assert.Equal(AcceptFieldPvPHandler.AnswerChallengerTimedOut, answer);

        var body = FieldPvPPackets.BuildAccept2737(challenger, duelId, challenger, target, 0f, 0f, 0f,
            FieldPvPPackets.AcceptTimedOut);
        Assert.Equal("0122220058e41127e508140400001127e50811aae6080000000000000000000000001c00",
            Convert.ToHexString(body).ToLowerInvariant());
    }

    [Fact]
    public void LateTargetAnswer_23_MatchesRetail()
    {
        
        
        const uint challenger = 0x0785EC11, target = 0x0784C211, duelId = 0x3FF;
        Assert.True(AcceptFieldPvPHandler.IsLateAnswerFromItsPlayer(AcceptFieldPvPHandler.AnswerRefuse, target, challenger, target));

        var body = FieldPvPPackets.BuildAccept2737(target, duelId, challenger, target, 0f, 0f, 0f,
            FieldPvPPackets.AcceptAcknowledged);
        Assert.Equal("0122220058e411c28407ff03000011ec850711c284070000000000000000000000001700",
            Convert.ToHexString(body).ToLowerInvariant());
    }

    [Fact]
    public void TargetClosingAnAcceptedDuel_14ToBoth_ThenChallengersClose_23_MatchesRetail()
    {
        
        
        
        var req = Convert.FromHexString("0116160054e40000000003001180d00d1184d00d26040000");
        var answer = BinaryPrimitives.ReadUInt16LittleEndian(req.AsSpan(10));
        var challenger = BinaryPrimitives.ReadUInt32LittleEndian(req.AsSpan(12));
        var target = BinaryPrimitives.ReadUInt32LittleEndian(req.AsSpan(16));
        var duelId = BinaryPrimitives.ReadUInt32LittleEndian(req.AsSpan(20));
        Assert.Equal(AcceptFieldPvPHandler.AnswerClose, answer);

        var duel = new FieldPvPService.Duel
        {
            Id = duelId, Challenger = challenger, Target = target, State = FieldPvPService.DuelState.Accepted,
        };
        Assert.True(AcceptFieldPvPHandler.IsPlayerOf(duel, target));

        var toTarget = FieldPvPPackets.BuildAccept2737(target, duelId, challenger, target, 0f, 0f, 0f,
            FieldPvPPackets.AcceptWithdrawn);
        var toChallenger = FieldPvPPackets.BuildAccept2737(challenger, duelId, challenger, target, 0f, 0f, 0f,
            FieldPvPPackets.AcceptWithdrawn);
        Assert.Equal("0122220058e41184d00d260400001180d00d1184d00d0000000000000000000000000e00",
            Convert.ToHexString(toTarget).ToLowerInvariant());
        Assert.Equal("0122220058e41180d00d260400001180d00d1184d00d0000000000000000000000000e00",
            Convert.ToHexString(toChallenger).ToLowerInvariant());

        Assert.True(AcceptFieldPvPHandler.IsLateAnswerFromItsPlayer(answer, challenger, challenger, target));
        var late = FieldPvPPackets.BuildAccept2737(challenger, duelId, challenger, target, 0f, 0f, 0f,
            FieldPvPPackets.AcceptAcknowledged);
        Assert.Equal("0122220058e41180d00d260400001180d00d1184d00d0000000000000000000000001700",
            Convert.ToHexString(late).ToLowerInvariant());
    }

    [Fact]
    public void ChallengerClosingAnAcceptedDuel_14_MatchesRetail()
    {
        
        
        var req = Convert.FromHexString("0116160054e4000000000300115b5a0d115e5a0df3030000");
        var challenger = BinaryPrimitives.ReadUInt32LittleEndian(req.AsSpan(12));
        var target = BinaryPrimitives.ReadUInt32LittleEndian(req.AsSpan(16));
        var duelId = BinaryPrimitives.ReadUInt32LittleEndian(req.AsSpan(20));
        Assert.Equal(AcceptFieldPvPHandler.AnswerClose, BinaryPrimitives.ReadUInt16LittleEndian(req.AsSpan(10)));

        Assert.Equal("0122220058e4115b5a0df3030000115b5a0d115e5a0d0000000000000000000000000e00",
            Convert.ToHexString(FieldPvPPackets.BuildAccept2737(challenger, duelId, challenger, target, 0f, 0f, 0f,
                FieldPvPPackets.AcceptWithdrawn)).ToLowerInvariant());
        Assert.Equal("0122220058e4115e5a0df3030000115b5a0d115e5a0d0000000000000000000000000e00",
            Convert.ToHexString(FieldPvPPackets.BuildAccept2737(target, duelId, challenger, target, 0f, 0f, 0f,
                FieldPvPPackets.AcceptWithdrawn)).ToLowerInvariant());
    }

    [Fact]
    public void IsPlayerOf_OnlyTheDuelsTwoPlayers()
    {
        var duel = new FieldPvPService.Duel { Id = 1, Challenger = 0x0785EC11, Target = 0x0784C211 };
        Assert.True(AcceptFieldPvPHandler.IsPlayerOf(duel, 0x0785EC11));
        Assert.True(AcceptFieldPvPHandler.IsPlayerOf(duel, 0x0784C211));
        Assert.False(AcceptFieldPvPHandler.IsPlayerOf(duel, 0x07000011));
    }

    [Theory]
    [InlineData(AcceptFieldPvPHandler.AnswerAccept, false, true)]
    [InlineData(AcceptFieldPvPHandler.AnswerRefuse, false, true)]
    [InlineData(AcceptFieldPvPHandler.AnswerClose, true, true)]
    [InlineData(AcceptFieldPvPHandler.AnswerChallengerTimedOut, true, false)]
    public void LateAnswer_IsAnsweredOnlyFromThePlayerItBelongsTo(ushort answer, bool fromChallenger, bool fromTarget)
    {
        const uint challenger = 0x0785EC11, target = 0x0784C211, bystander = 0x07000011;
        Assert.Equal(fromChallenger, AcceptFieldPvPHandler.IsLateAnswerFromItsPlayer(answer, challenger, challenger, target));
        Assert.Equal(fromTarget, AcceptFieldPvPHandler.IsLateAnswerFromItsPlayer(answer, target, challenger, target));
        Assert.False(AcceptFieldPvPHandler.IsLateAnswerFromItsPlayer(answer, bystander, challenger, target));
    }
}
