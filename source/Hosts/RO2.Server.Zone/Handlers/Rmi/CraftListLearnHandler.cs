using RO2.Economy.Domain;
using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1348 ReqCraftListLearn: the player learns a crafting recipe at a craft NPC. Answered with S2C 2538
/// OnAckCraftLearn (the complete learned list), followed by S2C 2435 OnZenyChange for the learning fee.
/// </summary>
/// <remarks>
/// <para>
/// The client checks the selection, the profession, the craft level and the fee before it sends the request. The
/// server repeats the checks and answers a refusal with a 2538 that carries the result code and no list change.
/// </para>
/// <para>
/// A refused request gets one 2538 with the refusal code and no 2435 (<see cref="CraftRecipeStore.BuildAckCraftLearnRefusal2538"/>).
/// The codes are those of <see cref="RefusalReason"/> (<see cref="RefusalCode"/>): an unknown recipe, a recipe that is
/// already known, a profession that does not match, a profession level that is too low, and a fee above the wallet.
/// </para>
/// <para>
/// Recipes are taught by type, regardless of Craft_LearnType. A recipe is valid only for the character's profession.
/// The learned list grows by the new recipe id, and the fee is Craft_ItemList.Learn_NeedMoney. Type-2 recipes have no
/// fee, so they send no 2435.
/// </para>
/// <para>
/// The craft level is compared with <see cref="RO2.Characters.Domain.Character.JobLevel"/>. This server does not model
/// ProJob levels.
/// </para>
/// <para>
/// Request body: <c>[01 0f][blob 15]</c>. The blob holds the sender handle, the craft NPC handle, and the recipe id as
/// a u16 at blob offset 13.
/// </para>
/// </remarks>
public class CraftListLearnHandler : ZoneRmiHandlerBase<CraftListLearnHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCraftListLearn;
    public override string Name => "CraftListLearn";
    public CraftListLearnHandler(IServiceScopeFactory s, ILogger<CraftListLearnHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>Blob offset of the recipe id (u16), the last field of the 15-byte blob.</summary>
    internal const int RecipeIdOffset = 13;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        if (!payload.Span.TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob) || blob.Length < RecipeIdOffset + 2)
        {
            Logger.LogWarning("{Name}: ReqCraftListLearn too short ({Len} bytes); not answered", player.Name, payload.Length);
            return;
        }
        ushort recipeId = BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(RecipeIdOffset));
        uint npcHandle = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(9));
        if (player.Character is not { } c)
        {
            Logger.LogDebug("{Name}: 1348 with no character snapshot; not answered", player.Name);
            return;
        }

        var table = services.GetRequiredService<GameDataStore>().CraftItemList;
        var recipes = services.GetRequiredService<RO2.Economy.Domain.ICraftRecipeRepository>();
        byte profession = WorldEntryService.CreationProfessionByte(c);
        var learned = await recipes.GetAsync(player.CharacterId, ct);
        var row = table.FirstOrDefault(r => r.ID == recipeId);

        var knownBefore = CraftRecipeStore.KnownRecipeIds(table, profession, learned);
        if (RefusalReason(row, recipeId, profession, knownBefore, player.Gold, c.JobLevel) is { } why)
        {
            ushort code = RefusalCode(row, recipeId, profession, knownBefore, player.Gold, c.JobLevel);
            Logger.LogInformation("{Name} learn recipe {Recipe} at NPC {Npc:X8} refused with {Code}: {Why}",
                player.Name, recipeId, npcHandle, code, why);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnAckCraftLearn, CraftRecipeStore.BuildAckCraftLearnRefusal2538(code), ct);
            return;
        }
        if (!await recipes.AddAsync(player.CharacterId, recipeId, ct))
        {
            
            Logger.LogInformation("{Name} learn recipe {Recipe}: already stored (repeated request), refused with {Code}",
                player.Name, recipeId, CraftRecipeStore.ResultAlreadyLearned);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnAckCraftLearn,
                CraftRecipeStore.BuildAckCraftLearnRefusal2538(CraftRecipeStore.ResultAlreadyLearned), ct);
            return;
        }

        var known = CraftRecipeStore.KnownRecipeIds(table, profession, learned.Append(recipeId));
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnAckCraftLearn, CraftRecipeStore.BuildAckCraftLearn2538(known));
        if (row!.Learn_NeedMoney > 0)
        {
            var zenyBody = ActorVitalsPackets.ApplyZenyDeltaAndBuild(player, -(long)row.Learn_NeedMoney);
            await session.SendRmiAsync(ZoneS2COpcodes.OnZenyChange, writer => writer.WriteBytes(zenyBody));
        }

        Logger.LogInformation("{Name} learned recipe {Recipe} ({Item}) for {Cost} zeny; {Count} recipes known",
            player.Name, recipeId, row.Craft_ItemName, row.Learn_NeedMoney, known.Count);
    }

 /// <summary>
 /// Returns why a learn of <paramref name="recipeId"/> is refused, or null when it is allowed. Checks, in order: the
 /// recipe row exists; the recipe belongs to the character's profession; it is not already known
 /// (<paramref name="known"/>); its craft level is not above <paramref name="jobLevel"/>; the fee is covered by the
 /// wallet. Craft_LearnType is not checked. The default of <paramref name="jobLevel"/>, byte.MaxValue, disables the
 /// level check.
 /// </summary>
    internal static string? RefusalReason(CraftItemList? row, ushort recipeId, byte profession, IReadOnlyCollection<ushort> known, long gold,
        byte jobLevel = byte.MaxValue)
    {
        if (row is null) return $"no Craft_ItemList row {recipeId}";
        if (row.ProType_ID != profession) return $"recipe of profession {row.ProType_ID}, character is {profession}";
        if (known.Contains(recipeId)) return "already known";
        if (row.Craft_Level > jobLevel) return $"Craft_Level {row.Craft_Level} above the profession level {jobLevel}";
        if (gold < row.Learn_NeedMoney) return $"fee {row.Learn_NeedMoney} above the wallet {gold}";
        return null;
    }

 /// <summary>
 /// Returns the 2538 result code for the refusal that <see cref="RefusalReason"/> reports, or 0 when the learn is
 /// allowed. The codes are the <see cref="CraftRecipeStore"/> Result constants.
 /// </summary>
    internal static ushort RefusalCode(CraftItemList? row, ushort recipeId, byte profession, IReadOnlyCollection<ushort> known, long gold,
        byte jobLevel = byte.MaxValue)
    {
        if (row is null) return CraftRecipeStore.ResultNoSuchRecipe;
        if (row.ProType_ID != profession) return CraftRecipeStore.ResultWrongProfession;
        if (known.Contains(recipeId)) return CraftRecipeStore.ResultAlreadyLearned;
        if (row.Craft_Level > jobLevel) return CraftRecipeStore.ResultProfessionLevelTooLow;
        if (gold < row.Learn_NeedMoney) return CraftRecipeStore.ResultNotEnoughMoney;
        return 0;
    }
}
