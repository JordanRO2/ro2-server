using RO2.Network.Protocol;
using RO2.PartyDice.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>Turns a dice round's announcing effects into the 2468, 2469 and 2470 frames. The Domain knows no opcodes.</summary>
internal static class PartyDiceFrames
{
    /// <summary>The opcode and body for an effect about <paramref name="item"/>, or null for one that sends no frame.</summary>
    internal static (ushort Opcode, byte[] Body)? ToFrame(DiceEffect effect, GroundItemService.CorpseItem item) => effect switch
    {
        Opened => (ZoneS2COpcodes.OnLootingBoxRouletteOpen,
            PartyDicePackets.BuildRouletteOpen2468(item.Uid, item.ItemTemplateId, item.Count)),
        Answered a => (ZoneS2COpcodes.OnLootingBoxRouletteResult,
            PartyDicePackets.BuildRouletteResult2469(0, a.Roll, a.Gid, item.ItemTemplateId, item.Uid)),
        Refused r => (ZoneS2COpcodes.OnLootingBoxRouletteRefuse,
            PartyDicePackets.BuildRouletteRefuse2470(r.Gid, item.ItemTemplateId, item.Uid)),
        AllPassed => (ZoneS2COpcodes.OnLootingBoxRouletteResult,
            PartyDicePackets.BuildRouletteResult2469(PartyDicePackets.ResultAllPassed, 0, 0, item.ItemTemplateId, item.Uid)),
        _ => null,
    };
}
