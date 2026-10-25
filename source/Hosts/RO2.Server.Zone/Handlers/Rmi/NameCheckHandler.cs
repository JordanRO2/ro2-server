using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Services;

using NetworkZoneRmiBase = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.NameCheckHandler>;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1444 (ReqCheckPCName): checks whether a character name is available.
/// </summary>
/// <remarks>
/// <para>
/// Replies with S2C RMI 2596 (OnCheckPCName), echoing the checked name. Result 0 means the name is available,
/// 7 means it contains a prohibited word, and 10 means it is already in use or fails the format check.
/// </para>
/// <para>
/// Each refusal has its own code: 7 for a prohibited name (a name held by another character and a format
/// failure both use 10).
/// </para>
/// </remarks>
public class NameCheckHandler : NetworkZoneRmiBase
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCheckPCName;
    public override string Name => "NameCheck";

 /// <summary>Result code: the name is available.</summary>
    internal const ushort ResultAvailable = 0;

 /// <summary>Result code: the name contains a prohibited word.</summary>
    internal const ushort ResultProhibited = 7;

 /// <summary>Result code: the name is already in use, or fails the format check.</summary>
    internal const ushort ResultUnavailable = 10;

    public NameCheckHandler(IServiceScopeFactory scopeFactory, ILogger<NameCheckHandler> logger)
        : base(scopeFactory, logger)
    {
    }

    protected override async Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        IServiceProvider services,
        CancellationToken ct)
    {
        string characterName = "";
        if (payload.Length >= 8)
        {
            var reader = new PacketReader(payload.Span);
            reader.Skip(8);
            characterName = reader.ReadWideString();
        }

        Logger.LogInformation("Session {SessionId}: NameCheck for '{Name}'", session.SessionId, characterName);

        
        var (refusal, reason) = NameValidator.Check(characterName, services.GetService<GameDataStore>()?.NameFilter);
        var result = ResultFor(refusal);

        
        if (refusal == NameRefusal.None)
        {
            var characters = services.GetRequiredService<ICharacterRepository>();
            if (await characters.CharacterNameExistsAsync(characterName))
                (result, reason) = (ResultUnavailable, "already taken");
        }

        
        
        
        if (session.AccountId is null)
            Logger.LogWarning("Session {SessionId}: NameCheck before the session has an account", session.SessionId);
        var body = BuildOnCheckPCName2596(session.AccountId ?? 0, result, characterName);
        await session.SendRmiAsync(ZoneS2COpcodes.NameCheckResult, writer => writer.WriteBytes(body));

        Logger.LogInformation("Session {SessionId}: NameCheck '{Name}' → {Result}{Reason}",
            session.SessionId, characterName, result,
            reason != null ? $" ({reason})" : "");
    }

 /// <summary>Result code for a name rejected by the validator; taken names are checked after the database.</summary>
    internal static ushort ResultFor(NameRefusal refusal) => refusal switch
    {
        NameRefusal.None => ResultAvailable,
        NameRefusal.Prohibited => ResultProhibited,
        _ => ResultUnavailable,
    };

 /// <summary>
 /// Body of 2596: <c>[u32 len][u32 accountId][u16 result][WStr16 name][u8 0]</c>, where len counts the bytes
 /// after itself.
 /// </summary>
    internal static byte[] BuildOnCheckPCName2596(uint accountId, ushort result, string name)
    {
        using var writer = new PacketWriter();
        var lengthPos = writer.Reserve(4);
        var dataStart = writer.Length;
        writer.WriteUInt32(accountId);
        writer.WriteUInt16(result);
        writer.WriteUnicodeString(name);
        writer.WriteByte(0);
        writer.WriteAt(lengthPos, (uint)(writer.Length - dataStart));
        return writer.ToArray();
    }
}
