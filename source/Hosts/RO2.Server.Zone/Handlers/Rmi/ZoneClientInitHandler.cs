using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Services;

using NetworkZoneRmiBase = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ZoneClientInitHandler>;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1414 (Packet_CLMSR_SLOT_LIST), the character-select slot-list request. The client sends it
/// after zone auth and after character creation or deletion.
/// </summary>
/// <remarks>
/// The body is a compact length prefix followed by a 10-byte blob: inner size, message tag, and a u16
/// selector at blob offset 8. The selector is 99 to ask for every character, or a page index + 1.
/// The reply is S2C 2852 AnsPCPageCount followed by S2C 2307 ZoneCharacterList. This handler needs no
/// PlayerState, because the request arrives at character select, before a character exists.
/// </remarks>
public class ZoneClientInitHandler : NetworkZoneRmiBase
{
 /// <summary>Number of characters shown on one character-select page.</summary>
    internal const int CharactersPerPage = 5;

 /// <summary>The selector that asks for every character.</summary>
    internal const ushort AllPagesSelector = 99;

    public override ushort Opcode => ZoneC2SOpcodes.Packet_CLMSR_SLOT_LIST;
    public override string Name => "ZoneClientInit";

    public ZoneClientInitHandler(IServiceScopeFactory scopeFactory, ILogger<ZoneClientInitHandler> logger)
        : base(scopeFactory, logger)
    {
    }

 /// <summary>
 /// Reads the page selector from a request body. Returns <see cref="AllPagesSelector"/> when the body is too short.
 /// </summary>
    internal static ushort ReadSelector(ReadOnlySpan<byte> body)
    {
        if (body.Length < 1) return AllPagesSelector;
        int selectorAt = 1 + body[0] + 8;
        return body.Length >= selectorAt + 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(selectorAt, 2))
            : AllPagesSelector;
    }

 /// <summary>
 /// Returns the characters a selector asks for, in list order: 99 is every character, and page k is
 /// characters 5*(k-1) through 5*k-1. Selectors that name no page of the account return the whole list.
 /// </summary>
    internal static IReadOnlyList<T> SelectPage<T>(IReadOnlyList<T> all, ushort selector)
    {
        if (selector == AllPagesSelector || selector == 0) return all;
        int start = (selector - 1) * CharactersPerPage;
        if (start >= all.Count) return all;
        return all.Skip(start).Take(CharactersPerPage).ToList();
    }

 /// <summary>Builds the 2820 body sent after a pre-occupied name check: <c>[u32 6][u32 accountId][u16 0]</c>.</summary>
    internal static byte[] BuildPreOccupyNameAck2820(uint accountId)
    {
        var ack = new byte[10];
        BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(0, 4), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(4, 4), accountId);
        return ack;
    }

    protected override async Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        IServiceProvider services,
        CancellationToken ct)
    {
        ushort selector = ReadSelector(payload.Span);
        Logger.LogInformation("Session {SessionId}: ZoneClientInit (RMI 1414) received ({Size} bytes, selector {Selector})",
            session.SessionId, payload.Length, selector);

        
        
        
        
        
        
        
        IReadOnlyList<Character> charList = [];
        if (session.AccountId is uint accountId)
            charList = await services.GetRequiredService<ICharacterRepository>().GetCharactersByAccountAsync(accountId);
        int charCount = charList.Count;
        int pageCount = Math.Max(1, (charCount + CharactersPerPage - 1) / CharactersPerPage);

        var pageBody = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(pageBody.AsSpan(0, 2), (ushort)pageCount);
        BinaryPrimitives.WriteUInt16LittleEndian(pageBody.AsSpan(2, 2), (ushort)charCount);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPCPageCount, pageBody);

        var page = SelectPage(charList, selector);
        if (page.Count == charList.Count)
        {
            
            
            await ZoneAuthHandler.SendZoneCharacterListAsync(session, services, Logger, ct);
            return;
        }

        
        
        var equipByChar = new Dictionary<uint, IReadOnlyList<InventoryItem>>();
        var inventory = services.GetRequiredService<IInventoryRepository>();
        foreach (var c in page)
            equipByChar[c.Id] = await inventory.GetEquipmentAsync(c.Id);

        var listAccountId = session.AccountId ?? 1;
        await session.SendRmiAsync(ZoneS2COpcodes.ZoneCharacterList, writer =>
        {
            CharacterSerializer.WriteCharacterList(writer, listAccountId, page, equipByChar);
        });

        
        
        
        
        
        if (page.Count > 0)
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPreOccupyCharacterNameACK, BuildPreOccupyNameAck2820(listAccountId));

        Logger.LogInformation("Session {SessionId}: ZoneCharacterList (RMI 2307) sent with page {Selector}: {Count} of {Total} character(s)",
            session.SessionId, selector, page.Count, charCount);
    }
}
