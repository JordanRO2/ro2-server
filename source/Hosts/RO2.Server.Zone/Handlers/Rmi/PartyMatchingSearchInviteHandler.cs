using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1560 ReqPartyMatchingSearchInvite - Search and invite for matching</summary>
public class PartyMatchingSearchInviteHandler : ZoneRmiHandlerBase<PartyMatchingSearchInviteHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyMatchingSearchInvite;
    public override string Name => "PartyMatchingSearchInvite";

    public PartyMatchingSearchInviteHandler(IServiceScopeFactory s, ILogger<PartyMatchingSearchInviteHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        
        if (!PartyMatchingBoard.TryParseSearch(payload.Span, out var filter)) return;

        Logger.LogDebug("Session {Id}: PartyMatchingSearchInvite difficulty={D} category={C} page={P} destination={Dest}",
            session.SessionId, filter.Difficulty, filter.Category, filter.Page, filter.Destination);

        
        
        
        
        
        
        
        
        
        if (!player.MapLoaded || player.PendingGliderDismount)
        {
            Logger.LogDebug("Session {Id}: PartyMatchingSearchInvite before map-load ack, not answered",
                session.SessionId);
            return;
        }

        
        
        
        
        var rows = ResolveListings(services, filter);
        var (page, pages, pageRows) = PartyMatchingBoard.Page(rows, filter.Page);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsPartyMemberSearch,
            writer => writer.WriteBytes(PartyStatePackets.BuildAnsPartyMemberSearch(page, pages, pageRows)));
    }

 /// <summary>
 /// Build the rows from the board and the posters' live state. Only what the poster typed is stored;
 /// the name, the GID (PartyAnswerPackets.MemberGid, the GID the party records carry, so a whisper or
 /// invite aimed at the row names the same character), the party size and the party handle are read
 /// now, so a poster who joined or left a party since posting is shown as they are. A posting whose
 /// poster is no longer in the world is removed: its row could not be whispered or invited.
 /// </summary>
    private List<PartyMatchingListing> ResolveListings(IServiceProvider services, PartyMatchingBoard.SearchFilter filter)
    {
        var board = PartyMatchingBoard.Instance;
        var parties = services.GetRequiredService<PartyService>();
        var rows = new List<PartyMatchingListing>();
        foreach (var posting in board.Snapshot())
        {
            var poster = PlayerManager.GetPlayerByCharacterId(posting.CharacterId);
            if (poster == null)
            {
                
                board.Remove(posting);
                continue;
            }
            if (!PartyMatchingBoard.Matches(posting, filter)) continue;

            
            
            
            var party = parties.GetPlayerParty(poster.SessionId);
            var memberCount = party == null ? 1 : Math.Clamp(party.MemberSessionIds.Count, 1, byte.MaxValue);
            var partyHandle = party == null ? 0u : PartyAnswerPackets.PartyHandle(party);

            rows.Add(new PartyMatchingListing((byte)memberCount, posting.Category, posting.Difficulty, partyHandle,
                posting.Destination, PartyAnswerPackets.MemberGid(poster), poster.Name, posting.Title));
        }
        return rows;
    }
}
