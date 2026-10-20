using System.Globalization;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The rules of 1682 ReqTransferReinforceItem (refine transfer, '제련 전승'): which (ticket, target, source) triples
/// may transfer, the fee, and the grade the target ends with. Used by TransferReinforceItemHandler.
/// </summary>
/// <remarks>
/// <para>
/// <b>The client's preview</b> (the refine pass-down window). With srcG / tgtG the item grades (record byte
/// 29), dLv = ItemInfo Item_Lv(target) - Item_Lv(source), the REFINE_Trans_ITEM row of the ticket (Succ_Rate,
/// DISC_RATE, limit_level, Max_Value) and Config_Def 83 (base fee 5000), 107 (fee per Item_Lv, 300) and 108
/// (grade per Item_Lv, 0.5), the window shows a fee and a '%d ~ %d' grade range:
/// </para>
/// <list type="bullet">
/// <item>fee = max(0, f - f * (DISC_RATE / 10000)), f = (int)(dLv * Config107 + Config83); the division is an
///   INTEGER one, so only DISC_RATE &gt;= 10000 discounts.</item>
/// <item>adj = (float)(srcG - dLv * Config108).</item>
/// <item>high = min((int)(srcG * Max_Value / 10000), srcG).</item>
/// <item>low = min(max((int)(adj * Succ_Rate / 10000), tgtG), high).</item>
/// </list>
/// <para>
/// The start button is enabled only when the job-mastery point total / 1000 &gt;= fee. The client's gates on a
/// dropped source: srcG &gt;= 1 and srcG &gt; tgtG, else 11801 "Please check Refine grade.";
/// |ItemInfo.Grade(target) - Grade(source)| &lt;= 1, else 11795 "Please check you Item grade.";
/// |dLv| &lt;= limit_level, else 11796 "Please check you item @1 level." - so limit_level is an Item_Lv
/// distance, not a grade cap. The ticket must be Item_Type 118 with a REFINE_Trans_ITEM row. The client also
/// expects ItemInfo.Equip_Slot in 1..7 for target and source - weapons and armour; this server enforces that
/// one only for non-card pairs (<see cref="EquipSlotGate"/>, a flagged defensive rule), because transfers
/// between cards (Equip_Slot 0) are also granted.
/// </para>
/// <para>
/// <b>Outcomes</b>: a transfer between two cards of one family grants a grade inside the range (see
/// <see cref="Outcome"/>), and the target takes that grade. A transfer whose Item_Lv distance exceeds
/// limit_level is refused, for example a normal card onto an MVP card, or a high-level armour piece onto a
/// low-level card.
/// </para>
/// </remarks>
internal static class RefineTransferRules
{
    /// <summary>ItemInfo Item_Type of every refine transfer ticket (the seven REFINE_Trans_ITEM rows).</summary>
    internal const byte TicketItemType = 118;

    /// <summary>Config_Def type 83, '제련 전승 기본 수수료값 (단위 1 = 1MP)', Value 5000: the base fee.</summary>
    internal const ushort ConfigBaseFee = 83;

    /// <summary>Config_Def type 107, '제련 전승 MP 수수료 보정용 값', Value 300: fee per Item_Lv of difference.</summary>
    internal const ushort ConfigFeePerLevel = 107;

    /// <summary>Config_Def type 108, '제련 전승 수치 보정용 값', Value 0.5: grade lost per Item_Lv of difference.</summary>
    internal const ushort ConfigGradePerLevel = 108;

    /// <summary>The client's fee and grade range for one transfer (u32 fee, u8 low, u8 high).</summary>
    internal readonly record struct Terms(int Fee, byte Low, byte High);

    /// <summary>The three Config_Def values the preview reads, or null when a row is missing or unparseable
    /// (the preview is then all-zero, and the server refuses rather than guess).</summary>
    internal static (float BaseFee, float FeePerLevel, float GradePerLevel)? Config(IReadOnlyList<ConfigDef> config)
    {
        float? Read(ushort type)
        {
            var row = config.FirstOrDefault(c => c.type == type);
            return row != null && float.TryParse(row.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        return Read(ConfigBaseFee) is float b && Read(ConfigFeePerLevel) is float f && Read(ConfigGradePerLevel) is float g
            ? (b, f, g)
            : null;
    }

    /// <summary>The preview computation, operation for operation (see the class remarks).</summary>
    internal static Terms Compute(REFINETransITEM rule, byte sourceGrade, byte targetGrade, int targetItemLv,
        int sourceItemLv, (float BaseFee, float FeePerLevel, float GradePerLevel) config)
    {
        int dLv = targetItemLv - sourceItemLv;
        int gross = (int)(dLv * (double)config.FeePerLevel + config.BaseFee);
        int fee = Math.Max(0, gross - gross * (rule.DISC_RATE / 10000));
        float adj = (float)(sourceGrade - dLv * (double)config.GradePerLevel);
        int highInt = sourceGrade * rule.Max_Value / 10000;
        byte high = highInt > sourceGrade ? sourceGrade : (byte)highInt;
        int lowInt = (int)(adj * (double)rule.Succ_Rate / 10000.0);
        byte low = lowInt > targetGrade ? (byte)Math.Min(lowInt, byte.MaxValue) : targetGrade;
        if (high < low) low = high;
        return new Terms(fee, low, high);
    }

    /// <summary>
    /// The grade the target is given: <see cref="Terms.High"/> when the range is one value, otherwise
    /// max(low, high - 1). Chosen to fit the known transfer results rather than derived from a formula: a range of
    /// one value gives that value, and floor((low + high) / 2) fits the wider ranges just as well. The client
    /// shows only the range; the roll, if there is one, is the server's.
    /// </summary>
    internal static byte Outcome(Terms terms)
        => terms.Low >= terms.High ? terms.High : (byte)Math.Max(terms.Low, terms.High - 1);

    /// <summary>
    /// Why the client would not have sent this transfer, with the string it shows, or null when its gates pass
    /// (the Equip_Slot gate is <see cref="EquipSlotGate"/>, see the class remarks).
    /// </summary>
    internal static string? ClientGate(REFINETransITEM rule, byte sourceGrade, byte targetGrade,
        byte targetItemGrade, byte sourceItemGrade, int targetItemLv, int sourceItemLv)
    {
        if (sourceGrade < 1 || sourceGrade <= targetGrade)
            return $"source +{sourceGrade} is not above target +{targetGrade} (11801)";
        if (Math.Abs(targetItemGrade - sourceItemGrade) > 1)
            return $"item grades {targetItemGrade} / {sourceItemGrade} are more than 1 apart (11795)";
        if (Math.Abs(targetItemLv - sourceItemLv) > rule.limit_level)
            return $"Item_Lv {targetItemLv} / {sourceItemLv} are more than limit_level {rule.limit_level} apart (11796)";
        return null;
    }

    /// <summary>Lowest and highest ItemInfo.Equip_Slot the client accepts for a transfer target/source (weapons, armour).</summary>
    internal const int ClientEquipSlotMin = 1, ClientEquipSlotMax = 7;

    /// <summary>
    /// Defensive rule: why a NON-card pair is refused for an Equip_Slot outside 1..7, or null when it passes.
    /// A server-side defensive rule: the client's Equip_Slot gate (1..7, class remarks) is an inference for the
    /// server. A pair that has a CardEnforceInfo row (a card, Equip_Slot 0) is exempt, because card-onto-card
    /// transfers are granted and this gate would refuse them. So this only refuses what the client could not
    /// have sent: a potion or material with a grade.
    /// </summary>
    internal static string? EquipSlotGate(GameDataStore store, uint targetId, uint sourceId)
    {
        if (store.CardEnforceInfo.Any(r => r.Item_ID == targetId || r.Item_ID == sourceId))
            return null;
        var t = store.GetItem(targetId)?.Equip_Slot ?? 0;
        var s = store.GetItem(sourceId)?.Equip_Slot ?? 0;
        return t is >= ClientEquipSlotMin and <= ClientEquipSlotMax && s is >= ClientEquipSlotMin and <= ClientEquipSlotMax
            ? null
            : $"Equip_Slot {t} / {s} is outside {ClientEquipSlotMin}..{ClientEquipSlotMax} (UNVERIFIED defensive rule, no retail source)";
    }

    /// <summary>ItemInfo Item_Lv of a template, or null when the table could not be read or has no row for it.</summary>
    internal static int? ItemLevel(uint templateId)
    {
        var table = ItemLevelTable.Value;
        return table is not null && table.TryGetValue(templateId, out var lv) ? lv : null;
    }

    /// <summary>ItemInfo columns ItemData does not carry, read once from the same "data" directory Program.cs hands
    /// GameDataStore (the pattern InitGrindingOptHandler.LoadPricing uses).</summary>
    private sealed record ItemLevelRow(uint ID, int Item_Lv);

    private static readonly Lazy<IReadOnlyDictionary<uint, int>?> ItemLevelTable =
        new(() => LoadItemLevels(Path.Combine("data", "ItemInfo.json")));

    /// <summary>
    /// ItemInfo Item_Lv of every row of <paramref name="path"/>, the first row per ID as GameDataStore keeps it
    /// (the 'Item_Lv' column).
    /// Null when the file is missing or unreadable, and every transfer is then refused.
    /// </summary>
    internal static IReadOnlyDictionary<uint, int>? LoadItemLevels(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var rows = System.Text.Json.JsonSerializer.Deserialize<List<ItemLevelRow>>(File.ReadAllBytes(path));
            var byId = new Dictionary<uint, int>();
            foreach (var r in rows ?? new List<ItemLevelRow>()) byId.TryAdd(r.ID, r.Item_Lv);
            return byId;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="sourceId"/> may give its grade to <paramref name="targetId"/>: two cards of one
    /// CardEnforceInfo EnforceID, or two items that have no CardEnforceInfo row (weapons and armour). A
    /// conservative server rule: transfers pair two cards of one family or two pieces of equipment, never
    /// different families (a normal EnforceID-450 card onto an MVP EnforceID-550 card, or an armour source onto a
    /// card). Those pairs also fail the Item_Lv gate, so the two rules overlap. Equipment onto equipment must
    /// pass: it is all this client build can send (the Equip_Slot 1..7 gate, class remarks).
    /// </summary>
    internal static bool SameFamily(GameDataStore store, uint targetId, uint sourceId)
    {
        var t = store.CardEnforceInfo.FirstOrDefault(r => r.Item_ID == targetId);
        var s = store.CardEnforceInfo.FirstOrDefault(r => r.Item_ID == sourceId);
        return (t, s) switch
        {
            (null, null) => true,
            ({ } tt, { } ss) => tt.EnforceID == ss.EnforceID,
            _ => false,
        };
    }
}
