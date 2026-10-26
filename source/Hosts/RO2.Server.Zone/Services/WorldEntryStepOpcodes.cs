using RO2.Network.Protocol;
using RO2.Worlds.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The opcode of each <see cref="WorldEntryStep"/>. The plan holds the order of the steps; the wire ids stay in the host.
/// </summary>
internal static class WorldEntryStepOpcodes
{
 /// <summary>The server opcode the host sends for <paramref name="step"/>.</summary>
    public static ushort Of(WorldEntryStep step) => step switch
    {
        WorldEntryStep.AccountConfig => ZoneS2COpcodes.Notify_ZSCLN_DB_ACCOUNT_CONFIG,
        WorldEntryStep.DependentDungeonInfo => ZoneS2COpcodes.OnSendDependentDungeonInfo,
        WorldEntryStep.PcInitTrait => ZoneS2COpcodes.OnPcInitTrait,
        WorldEntryStep.PcTrait => ZoneS2COpcodes.OnPcTrait,
        WorldEntryStep.ServerTime => ZoneS2COpcodes.NfyServerTimeToLoginPC,
        WorldEntryStep.CraftIndex => ZoneS2COpcodes.OnNotifyCraftIndex,
        WorldEntryStep.CharacterDetail => ZoneS2COpcodes.OnCharacterDetailACK,
        WorldEntryStep.LoginPcTrait => ZoneS2COpcodes.OnPcTrait,
        WorldEntryStep.UseSkillList => ZoneS2COpcodes.OnUseSkillList,
        WorldEntryStep.CardBookCharge => ZoneS2COpcodes.NfyCardBookChargedTime,
        WorldEntryStep.RankingTopList => ZoneS2COpcodes.NfyRankingTopList,
        WorldEntryStep.MapLoad => ZoneS2COpcodes.OnReceiveMapLoad,
        WorldEntryStep.QuickSlots => ZoneS2COpcodes.OnQuickSlotSaveData,
        WorldEntryStep.VehicleQuickSlot => ZoneS2COpcodes.OnVehicleQuickSlot,
        WorldEntryStep.AllVehicleList => ZoneS2COpcodes.OnAllVehicleList,
        WorldEntryStep.MonsterCollection => ZoneS2COpcodes.NfyMCollection,
        WorldEntryStep.PetCollection => ZoneS2COpcodes.NfyPCollection,
        WorldEntryStep.ActivePet => ZoneS2COpcodes.AnsPCollectionSetActivePet,
        WorldEntryStep.RegisterCardBook => ZoneS2COpcodes.AnsRegistCardBook,
        WorldEntryStep.MoveSpeed => ZoneS2COpcodes.OnChangeActorMoveSpeed,
        WorldEntryStep.ZenyBalance => ZoneS2COpcodes.OnZenyChange,
        WorldEntryStep.DepartureMarker => ZoneS2COpcodes.AnsSiegeArmsUseEnd,
        WorldEntryStep.SelfDespawn => ZoneS2COpcodes.OnPC_Out,
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, "no opcode for this world-entry step"),
    };
}
