using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1611 ReqCardEnforceItem - raise a card's enforce grade (+0..+10) with an enhancement stone, a
/// coating item and a catalyst.</summary>
/// <remarks>
/// <para>
/// BODY. A 2-byte compact prefix over a 56-byte blob with innerSize 56 and msgTag 58490
/// (a fixed literal).
/// </para>
/// <code>
/// body+0 u8 compact width marker (1)
/// body+1 u8 blob length (56)
/// blob+0 u16 innerSize = 56
/// blob+2 u16 msgTag = 58490
/// blob+4 u32 actor handle
/// blob+8 u32
/// blob+12 four 11-byte item descriptors [u8 slot][u8 bag][u8 count][u64 uid]
/// -> uids at blob+15, blob+26, blob+37, blob+48
/// </code>
/// <para>
/// The FIRST descriptor is the card being enhanced; the rest are the materials. The 2899 answer embeds a
/// 126-byte item record whose uid equals the first descriptor's uid. The [slot][bag] byte order matches the
/// answer record: descriptor byte 0 is the slot and byte 1 is the bag.
/// </para>
/// <para>
/// <b>THE ENFORCE, from the game data.</b> The
/// three material descriptors are [enhancement stone][coating][catalyst], the window's three material slots
/// in order (see MATERIALS below for what each one takes). The card's grade
/// is the record's level byte (InventoryItem.Refine, which the 2310/2326 records already
/// carry), and the row is CardEnforce[CardEnforceInfo[card].EnforceID, EnforceGrade == current grade]:
/// </para>
/// <list type="bullet">
/// <item>COST: the row's NeedZenny (deducted from the wallet), MeterialCount of the
/// stone (the row, not the descriptor's count byte, is authoritative) and ONE of each of the other two items. Every rolled
/// attempt consumes, success or not: 2435, then one 2329 per material with useSource 3, then the 2899.</item>
/// <item>CHANCE = EnforceRate + SpecialEnforceRate (100/80/70/60/50/10% for the sum across the grades).</item>
/// <item>SUCCESS is +1, or the lucky jump of a material's REFINE_ITEM row (Card Lucky Powder:
/// 2_GRADE_RATE 5000 / 3_GRADE_RATE 1000, the same columns 1668 uses). Grades stop at 10 (EnforceGrade 0..9 rows,
/// 9 -> 10 is the last step).</item>
/// <item>FAILURE keeps the grade when a Cardfinium is in the coating slot and
/// otherwise applies the row's FailType 1 = back to +0.</item>
/// </list>
/// <para>
/// <b>MATERIALS: the client's slot rule.</b> The window only
/// takes an item into a slot by its ItemInfo Item_Type, and refuses anything else with string_ui 11012
/// "Incorrect item for the selected card reinforcement." For a card of Item_Type 13
/// the client puts Item_Type 143 into the stone slot, and
/// only when the stack holds the row's MeterialCount (11013 "You lack the required item needed." otherwise);
/// 144 or 146 (Card Coating Paper, the Cardfiniums) into the coating slot; and 145 (the Card Lucky Powders) or
/// the Item_Type 21 synthesis stone of the card's grade into the catalyst slot. For an Item_Type 139
/// (legendary) card the client does the same with 140, 141/142 and
/// 145/22300104. The "stone of the card's grade" is a constant per grade in the client (22300101 / 22300102 /
/// 22300103 for grades 2 / 3 / 4, 22300104 for legendary), which is exactly CardCompose.Card_Grade of those
/// stones against CardInfo.Grade_Type of the card. The send button needs all three slots
/// filled. The stone rule is the row's MeterialID here:
/// 22300201 and 66600018 are the only Item_Type 143 and 140 items and the MeterialID of every row.
/// </para>
/// <para>
/// The stock client only sends what its slot rule allows. Anything else is answered with a refusal: 2899
/// result 5 with the target echoed and nothing consumed, which is also the answer to an empty coating slot.
/// A stack of several cards is refused too: every 2899 record has count 1, and enforcing a stack would
/// upgrade all of its cards at once.
/// </para>
/// </remarks>
public class CardEnforceItemHandler : ZoneRmiHandlerBase<CardEnforceItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCardEnforceItem;
    public override string Name => "CardEnforceItem";

 /// <summary>Blob length: 56.</summary>
    private const int BlobLength = 56;

 /// <summary>Blob offset of the descriptor array (blob+12): the target descriptor first, then the materials.</summary>
    private const int DescriptorsBlobOffset = 12;

 /// <summary>Descriptor stride: [u8 slot][u8 bag][u8 count][u64 uid].</summary>
    private const int DescriptorSize = 11;

 /// <summary>Offset of the u8 bag inside a descriptor.</summary>
    private const int DescriptorBagOffset = 1;

 /// <summary>Offset of the u8 count inside a descriptor.</summary>
    private const int DescriptorCountOffset = 2;

 /// <summary>Offset of the u64 uid inside a descriptor (3 bytes in: blob+15 for the target).</summary>
    private const int DescriptorUidOffset = 3;

 /// <summary>Index of the target descriptor, the card being enhanced.</summary>
    private const int TargetDescriptorIndex = 0;

 /// <summary>Index of the first material descriptor: the descriptors after the target are the materials.</summary>
    private const int FirstMaterialDescriptorIndex = 1;

 /// <summary>Number of material descriptors (stone, coating, catalyst).</summary>
    private const int CardDescriptorCount = 3;

 /// <summary>InventoryItem.StorageType of the bag; the window only accepts stacks held there.</summary>
    private const byte StorageBag = 0;

 /// <summary>Stack size the enforced card must have: enforcing a stack would upgrade every card in it.</summary>
    private const int EnforcedCardCount = 1;

 /// <summary>Units of each non-stone material consumed per attempt (the stone takes the row's MeterialCount).</summary>
    private const int MaterialUnitCount = 1;

 /// <summary>CardEnforce rates (EnforceRate, SpecialEnforceRate) and the lucky-jump columns are in 1/10000.</summary>
    private const int RateScale = 10000;

 /// <summary>Rate units per percent, for the log line.</summary>
    private const int RatePerPercent = 100;

 /// <summary>Grade gained by an ordinary success.</summary>
    private const int GradeStepNormal = 1;

 /// <summary>Grade gained by a lucky success rolled against the 2_GRADE_RATE column.</summary>
    private const int GradeStepLucky2 = 2;

 /// <summary>Grade gained by a lucky success rolled against the 3_GRADE_RATE column.</summary>
    private const int GradeStepLucky3 = 3;

 /// <summary>2899 result code of a rolled enforce, whether it succeeded or failed: 0.</summary>
    private const short ResultRolled = 0;

 /// <summary>2329 OnUseItem useSource of every item an enhance consumes: 3.</summary>
    private const byte UseSourceEnhance = 3;

    public CardEnforceItemHandler(IServiceScopeFactory s, ILogger<CardEnforceItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var body = payload.Span;
        // Strict: the declared length must equal the bytes that follow (the blob is BlobLength bytes).
        if (!body.TryGetCompactBlob(CompactLengthCheck.Strict, out var blob) || blob.Length < BlobLength)
        {
            Logger.LogWarning("Session {Id}: CardEnforceItem body is {Len} bytes, not the 58-byte compact frame retail sends",
                session.SessionId, body.Length);
            return;
        }

        var descriptors = new (byte Slot, byte Bag, byte Count, ulong Uid)[FirstMaterialDescriptorIndex + CardDescriptorCount];
        for (int i = 0; i < descriptors.Length; i++)
        {
            int off = DescriptorsBlobOffset + DescriptorSize * i;
            descriptors[i] = (blob[off], blob[off + DescriptorBagOffset], blob[off + DescriptorCountOffset],
                BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(off + DescriptorUidOffset, sizeof(ulong))));
        }
        var targetDesc = descriptors[TargetDescriptorIndex];

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var store = services.GetRequiredService<GameDataStore>();

        // ---- Validation. Every refusal answers 2899 with ResultRefused echoing the target, nothing consumed. ----
        var target = targetDesc.Uid != 0 ? await inventory.GetByIdAsync(targetDesc.Uid) : null;
        if (!InBag(target, player) || target.Count != EnforcedCardCount)
        {
            await RefuseAsync(session, player, targetDesc, target, "the target is not a single card in the bag");
            return;
        }
        uint cardId = target.ItemId;
        int grade = target.Refine;
        var info = store.CardEnforceInfo.FirstOrDefault(r => r.Item_ID == cardId);
        if (info is null)
        {
            await RefuseAsync(session, player, targetDesc, target, $"item {cardId} has no CardEnforceInfo row");
            return;
        }
        ushort enforceId = info.EnforceID;
        var row = store.CardEnforce.FirstOrDefault(r => r.EnforceID == enforceId && r.EnforceGrade == grade);
        if (row is null)
        {
            await RefuseAsync(session, player, targetDesc, target, $"no CardEnforce row {enforceId} for grade {grade}");
            return;
        }
        int maxGrade = store.CardEnforce.Where(r => r.EnforceID == enforceId).Max(r => (int)r.EnforceGrade) + 1;

        // Materials, by the client's slot rule (MaterialFits): StoneSlot the enhancement stone, CoatingSlot a
        // coating item, CatalystSlot a catalyst. Four distinct stacks, so one stack can never be counted, and
        // consumed, twice.
        var seen = new HashSet<ulong> { targetDesc.Uid };
        var materials = new RO2.Domain.Entities.InventoryItem[CardDescriptorCount];
        for (int i = 0; i < CardDescriptorCount; i++)
        {
            var d = descriptors[FirstMaterialDescriptorIndex + i];
            var m = d.Uid != 0 && seen.Add(d.Uid) ? await inventory.GetByIdAsync(d.Uid) : null;
            if (!InBag(m, player) || !MaterialFits(store, cardId, row, i, m.ItemId, m.Count))
            {
                await RefuseAsync(session, player, targetDesc, target, i switch
                {
                    StoneSlot => $"stone slot is not {row.MeterialCount}x item {row.MeterialID}",
                    CoatingSlot => "coating slot is empty, repeated or not a Card Coating Paper / Cardfinium",
                    _ => "catalyst slot is empty, repeated or neither a Lucky Powder nor the synthesis stone of the card's grade",
                });
                return;
            }
            materials[i] = m;
        }
        // Pay: the zeny is taken in the same atomic step that checks it.
        if (!player.TryDebit(row.NeedZenny))
        {
            await RefuseAsync(session, player, targetDesc, target, $"needs {row.NeedZenny} zeny, has {player.Gold}");
            return;
        }

        // ---- Consume: zeny (2435), then each material (2329 with useSource UseSourceEnhance, the value every
        // enhance consumption uses). The 2329 echoes the descriptor's (slot, bag). ----
        await services.GetRequiredService<ICharacterRepository>().UpdateGoldAsync(player.CharacterId, player.Gold);
        var zenyBody = ActorVitalsPackets.BuildZenyChange2435(player.EntityId, player.Gold);
        await session.SendRmiAsync(ZoneS2COpcodes.OnZenyChange, writer => writer.WriteBytes(zenyBody));
        for (int i = 0; i < CardDescriptorCount; i++)
        {
            var m = materials[i];
            var d = descriptors[FirstMaterialDescriptorIndex + i];
            int remaining = m.Count - (i == StoneSlot ? row.MeterialCount : MaterialUnitCount);
            if (remaining <= 0) { await inventory.RemoveItemAsync(m.Id); remaining = 0; }
            else { m.Count = remaining; await inventory.UpdateItemAsync(m); }
            var useBody = CombatPackets.BuildUseItem2329(player.EntityId, d.Slot, d.Bag, m.Id,
                (byte)Math.Clamp(remaining, 0, byte.MaxValue), useSource: UseSourceEnhance);
            await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem, writer => writer.WriteBytes(useBody));
        }

        // ---- Roll (the rules are in the class remarks). The coating and catalyst slots are the ones that can
        // protect a failure or add a lucky jump. ----
        var coating = materials.Skip(CoatingSlot).ToArray();
        bool protectedFail = coating.Any(m =>
            store.GetItem(m.ItemId)?.Item_Type is CardfiniumType or LegendaryCardfiniumType);
        int g2 = 0, g3 = 0;
        foreach (var m in coating)
        {
            var lucky = store.REFINEITEM.FirstOrDefault(r => r.ITEM_ID == m.ItemId);
            if (lucky is null) continue;
            g2 += lucky._2_GRADE_RATE;
            g3 += lucky._3_GRADE_RATE;
        }
        int chance = Math.Min(RateScale, row.EnforceRate + row.SpecialEnforceRate);
        var rng = Random.Shared;
        int newGrade;
        string outcome;
        if (rng.Next(RateScale) < chance)
        {
            int r = rng.Next(RateScale);
            int jump = r < g3 ? GradeStepLucky3 : r < g3 + g2 ? GradeStepLucky2 : GradeStepNormal;
            newGrade = Math.Min(maxGrade, grade + jump);
            outcome = newGrade - grade > GradeStepNormal ? $"success +{newGrade - grade}" : "success";
        }
        else if (protectedFail || row.FailType != FailTypeResetToZero)
        {
            // Only FailType 1 has a defined meaning; any other value keeps the grade rather than guess a loss.
            newGrade = grade;
            outcome = protectedFail ? "failed, kept by Cardfinium" : $"failed (FailType {row.FailType}), kept";
        }
        else
        {
            newGrade = 0;
            outcome = "failed, back to +0";
        }

        target.Refine = (byte)newGrade;
        await inventory.UpdateItemAsync(target);

        // OnCardEnforceItem (2899), compact-BA msgTag 58491, blob 138: result ResultRolled, the OLD grade at
        // blob+10 and the card's record AFTER the roll at blob+11. The record's uid is non-zero so the client
        // finds the card; blob+10 against the record's level byte selects the success or failure text.
        var cardBody = ShopItemPackets.BuildCardEnforceItem2899(player.EntityId, ResultRolled, (byte)grade,
            WorldEntryService.BuildItemRecord126(target));
        await session.SendRmiAsync(ZoneS2COpcodes.OnCardEnforceItem,
            writer => writer.WriteBytes(cardBody));

        Logger.LogInformation("{Name} card-enforces '{Item}' ({Uid}) +{Old} -> +{New}: {Outcome} (chance {Chance}%, {Zeny} zeny)",
            player.Name, store.GetItem(target.ItemId)?.Name ?? target.ItemId.ToString(), target.Id, grade, newGrade,
            outcome, chance / RatePerPercent, row.NeedZenny);
    }

 /// <summary>ItemInfo Item_Type of Cardfinium (66600000/66600001): the item that keeps the grade on a
 /// failure. The client takes it into the coating slot.</summary>
    private const byte CardfiniumType = 146;

 /// <summary>ItemInfo Item_Type of Legendary Cardfinium (66600012..14), the legendary cards' version of the
 /// same protection. By name only.</summary>
    private const byte LegendaryCardfiniumType = 142;

 /// <summary>ItemInfo Item_Type of Card Coating Paper (70000164): the client's other coating-slot item for an
 /// Item_Type 13 card.</summary>
    private const byte CoatingPaperType = 144;

 /// <summary>ItemInfo Item_Type of Legendary Card Coating Paper (66600015), its legendary counterpart.</summary>
    private const byte LegendaryCoatingPaperType = 141;

 /// <summary>ItemInfo Item_Type of the Card Lucky Powders: the catalyst slot takes any of them, for both kinds
 /// of card.</summary>
    private const byte LuckyPowderType = 145;

 /// <summary>ItemInfo Item_Type of the card synthesis stones: the catalyst slot takes only the one whose
 /// CardCompose.Card_Grade is the card's CardInfo.Grade_Type.</summary>
    private const byte SynthesisStoneType = 21;

 /// <summary>ItemInfo Item_Type of a legendary card, whose materials are the 140/141/142 set
 /// (the client routes 13 and 139 to the two slot rules).</summary>
    private const byte LegendaryCardItemType = 139;

 /// <summary>Material slot 0: the enhancement stone.</summary>
    internal const int StoneSlot = 0;

 /// <summary>Material slot 1: the coating item.</summary>
    internal const int CoatingSlot = 1;

 /// <summary>Material slot 2: the catalyst.</summary>
    internal const int CatalystSlot = 2;

 /// <summary>
 /// Whether material slot <paramref name="slot"/> (<see cref="StoneSlot"/>, <see cref="CoatingSlot"/>,
 /// <see cref="CatalystSlot"/>) takes a stack of <paramref name="count"/> x <paramref name="materialId"/> when
 /// card <paramref name="cardId"/> is enforced with <paramref name="row"/>: the stock client's slot rule (class
 /// remarks). The stone is the row's MeterialID, at least MeterialCount of it; the coating is a Card Coating
 /// Paper or a Cardfinium (their legendary versions for a legendary card); the catalyst is a Card Lucky Powder
 /// or the synthesis stone whose CardCompose.Card_Grade is the card's CardInfo.Grade_Type.
 /// </summary>
    internal static bool MaterialFits(GameDataStore store, uint cardId, RO2.Domain.GameData.Tables.CardEnforce row, int slot,
        uint materialId, int count)
    {
        if (slot == StoneSlot) return materialId == row.MeterialID && count >= row.MeterialCount;
        if (count < 1) return false;
        byte type = store.GetItem(materialId)?.Item_Type ?? 0;
        if (slot == CoatingSlot)
            return store.GetItem(cardId)?.Item_Type == LegendaryCardItemType
                ? type is LegendaryCoatingPaperType or LegendaryCardfiniumType
                : type is CoatingPaperType or CardfiniumType;
        if (type == LuckyPowderType) return true;
        if (type != SynthesisStoneType) return false;
        byte cardGrade = store.CardInfo.FirstOrDefault(c => c.Card_ID == cardId)?.Grade_Type ?? 0;
        return cardGrade != 0 && store.CardCompose.Any(c => c.ItemID == materialId && c.Card_Grade == cardGrade);
    }

 /// <summary>CardEnforce.FailType on every row: an unprotected failure returns the card to +0.</summary>
    private const byte FailTypeResetToZero = 1;

 /// <summary>Item-record offset of the u64 uid (record+0).</summary>
    private const int EchoUidOffset = 0;

 /// <summary>Item-record offset of the u8 count (record+14).</summary>
    private const int EchoCountOffset = 14;

 /// <summary>Item-record offset of the u8 bag (record+15).</summary>
    private const int EchoBagOffset = 15;

 /// <summary>Item-record offset of the u8 slot (record+16).</summary>
    private const int EchoSlotOffset = 16;

 /// <summary>2899 result code for a refused enforce: the only non-zero code sent for a 1611.</summary>
    private const short ResultRefused = 5;

    private static bool InBag([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] RO2.Domain.Entities.InventoryItem? it,
        PlayerState player) =>
        it is not null && it.CharacterId == player.CharacterId && it.StorageType == StorageBag;

 /// <summary>
 /// Refusal: 2899 result 5 with the target echoed and nothing consumed. The refusal carries, at blob+11,
 /// a record holding ONLY the request's target descriptor (uid, count, bag, slot) with the item id and
 /// durability zero, and flag 0 at blob+137. On a non-zero result the client shows the notification and
 /// resets the window's slots without reading the record, so the echo is written as-is, even for a card this
 /// server may not hold.
 /// </summary>
    private async Task RefuseAsync(ProudSession session, PlayerState player,
        (byte Slot, byte Bag, byte Count, ulong Uid) target, RO2.Domain.Entities.InventoryItem? held, string why)
    {
        Logger.LogInformation("{Name} card-enforce of uid {Uid} refused: {Why}", player.Name, target.Uid, why);
        var echo = new byte[ItemPackets.ItemRecordLength];
        BinaryPrimitives.WriteUInt64LittleEndian(echo.AsSpan(EchoUidOffset), target.Uid);
        echo[EchoCountOffset] = target.Count;
        echo[EchoBagOffset] = target.Bag;
        echo[EchoSlotOffset] = target.Slot;
        byte grade = InBag(held, player) ? held.Refine : (byte)0;
        var body = ShopItemPackets.BuildCardEnforceItem2899(player.EntityId, ResultRefused, grade, echo);
        await session.SendRmiAsync(ZoneS2COpcodes.OnCardEnforceItem, writer => writer.WriteBytes(body));
    }
}
