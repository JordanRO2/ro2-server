using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Services;

using NetworkZoneRmiBase = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.CharacterCreateHandler>;
using RO2.Characters.Domain;
using RO2.Characters.Application;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handler for C2S RMI 1415, character create.
/// Payload after the 5-byte RMI header:
/// [flag:1][marker:2][padding:1][sequence:4][padding:2] 10-byte header
/// [name: fixed 42-byte field, wchar_t[21], null-terminated]
/// [appearance: 23 bytes: gender, race, face, hair, combat class, profession and related fields]
/// [trailing: padding, ignored]
/// The name field has a fixed width and no slider block is present.
/// The handler answers with S2C RMI 2309 NameCheckResult. The character list follows in response to ZoneClientInit.
/// </summary>
public class CharacterCreateHandler : NetworkZoneRmiBase
{
    public override ushort Opcode => ZoneC2SOpcodes.Packet_CLMSR_SLOT_CREATEPC;
    public override string Name => "CreateCharacter";

    public CharacterCreateHandler(IServiceScopeFactory scopeFactory, ILogger<CharacterCreateHandler> logger)
        : base(scopeFactory, logger)
    {
    }

    protected override async Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        IServiceProvider services,
        CancellationToken ct)
    {
        Logger.LogInformation("Session {SessionId}: CreateCharacter (RMI 1415) received ({Size} bytes)",
            session.SessionId, payload.Length);

        if (payload.Length < 12 || session.AccountId == null)
        {
            Logger.LogWarning("Session {SessionId}: Invalid create request (too short or no account)",
                session.SessionId);
            return;
        }

        var reader = new PacketReader(payload.Span);


        reader.Skip(10);




        var characterName = reader.ReadWideStringFixed(21);

        // Name check runs BEFORE the appearance parse: a short payload can throw in ParseCreateAppearance, and a
        // rejected name must never reach it.
        var (refusal, nameReason) = CreateCharacterFlow.CheckName(
            characterName, services.GetRequiredService<INameFilter>());
        if (refusal != NameRefusal.None)
        {
            Logger.LogWarning("Session {SessionId}: Name rejected: {Reason}", session.SessionId, nameReason);
            await SendCreateResult(session, 1);
            return;
        }




        var appearance = CharacterSerializer.ParseCreateAppearance(reader);









        var job = appearance.EyeColor;
        var gender = appearance.Gender;

        Logger.LogInformation(
            "Session {SessionId}: Creating '{Name}' - Job={Job}, Gender={Gender}, Face={Face}, Hair={Hair}",
            session.SessionId, characterName, job, gender, appearance.Face, appearance.HairStyle);


        var appearanceJson = CharacterSerializer.ToJson(appearance);

        var data = new CharacterGameData(services.GetRequiredService<RO2.GameData.GameDataStore>());
        var repo = services.GetRequiredService<ICharacterRepository>();
        var result = await CreateCharacterFlow.CreateAsync(session.AccountId.Value, characterName, job, gender,
            appearanceJson, appearance.Race, data, repo);

        if (result.Outcome == CreateCharacterFlow.CreateOutcome.Failed)
        {
            Logger.LogWarning("Session {SessionId}: Character creation failed: {Error}",
                session.SessionId, result.Error);
            await SendCreateResult(session, 1);
            return;
        }

        Logger.LogInformation("Session {SessionId}: Character '{Name}' created (ID: {Id})",
            session.SessionId, characterName, result.Id);


        await SendCreateResult(session, 0);
    }

    private async Task SendCreateResult(ProudSession session, ushort result)
    {
        
        await session.SendRmiAsync(ZoneS2COpcodes.OnCreateCharacterACK, writer =>
        {
            writer.WriteUInt32(session.AccountId ?? 1);
            writer.WriteUInt16(result);
        });
    }
}
