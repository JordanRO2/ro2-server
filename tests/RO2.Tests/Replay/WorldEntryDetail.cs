using System.Buffers.Binary;
using System.Text;
using RO2.Server.Zone.Services;

namespace RO2.Tests.Replay;

/// <summary>
/// A field-by-field reading of the two big world-entry frames (2310 OnPcInfo, 2307 character list) for the Tier C offsets that
/// only say "byte N differs". Offsets and names are the ones <c>WorldEntryPackets.HeadOffset</c> and
/// <c>ItemPackets.WriteItemInstance</c> write, so a line here names the builder field that disagrees with retail. Identity (uid,
/// actor handle) is not reported: it cannot match.
/// </summary>
internal static class WorldEntryDetail
{
    private static readonly (int Start, string Name)[] Head =
    [
        (WorldEntryPackets.HeadOffset.CharUid, "uid"), (WorldEntryPackets.HeadOffset.EntityId, "entity"),
        (WorldEntryPackets.HeadOffset.Name, "name"), (WorldEntryPackets.HeadOffset.Appearance, "look"),
        (WorldEntryPackets.HeadOffset.Level, "level"), (WorldEntryPackets.HeadOffset.BaseExp, "baseExp"),
        (WorldEntryPackets.HeadOffset.CombatClass, "class"), (79, "zeros79"),
        (WorldEntryPackets.HeadOffset.CurHp, "curHp"), (WorldEntryPackets.HeadOffset.CurSp, "curSp"),
        (WorldEntryPackets.HeadOffset.PosX, "pos"), (WorldEntryPackets.HeadOffset.MapId, "map"),
        (WorldEntryPackets.HeadOffset.BindIndex, "bindIndex"), (WorldEntryPackets.HeadOffset.Facing, "facing"),
        (108, "zeros108"), (WorldEntryPackets.HeadOffset.Wallet, "wallet"),
        (WorldEntryPackets.HeadOffset.Constant120, "const120"), (WorldEntryPackets.HeadOffset.BankSlots, "bankSlots"),
        (WorldEntryPackets.HeadOffset.Constant122, "const122"), (WorldEntryPackets.HeadOffset.KharaPoints, "kharaPoints"),
        (WorldEntryPackets.HeadOffset.CreationProfession, "creationProfession"), (WorldEntryPackets.HeadOffset.JobLevel, "jobLevel"),
        (WorldEntryPackets.HeadOffset.JobExp, "jobExp"), (WorldEntryPackets.HeadOffset.EquipHideFlag, "equipHide"),
        (WorldEntryPackets.HeadOffset.TalentPoints, "talentPoints"), (WorldEntryPackets.HeadOffset.TitleATimer, "titleATimer"),
        (WorldEntryPackets.HeadOffset.TitleA, "titleA"), (WorldEntryPackets.HeadOffset.BonusStats, "bonusStats(STR..VIT)"),
        (161, "zeros161"), (WorldEntryPackets.HeadOffset.UnspentBonusPoints, "unspentBonus"),
        (WorldEntryPackets.HeadOffset.VisualSlots, "visualSlots"), (WorldEntryPackets.HeadOffset.Union, "union"),
        (WorldEntryPackets.HeadOffset.TitleB, "titleB"), (WorldEntryPackets.HeadOffset.ProJobLevel, "proJobLevel"),
        (WorldEntryPackets.HeadOffset.ProJobId, "proJobId"), (WorldEntryPackets.HeadOffset.JobMasteryPoints, "jobMastery"),
        (WorldEntryPackets.HeadOffset.Reserved, "reserved"), (WorldEntryPackets.HeadOffset.Length, "end"),
    ];

    private const int IdentityEnd = 12; // uid (8) + entity handle (4)

    /// <summary>The 2310 retail sent against ours, section by section.</summary>
    public static string PcInfo(byte[] retail, byte[] ours)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"2310 OnPcInfo detail: retail {retail.Length} B, ours {ours.Length} B ({ours.Length - retail.Length:+#;-#;0} B)");
        if (RetailPc.Parse(retail) is not { } r || RetailPc.Parse(ours) is not { } o)
        {
            sb.AppendLine("  (a body did not walk to its stat block; no section detail)");
            return sb.ToString();
        }

        sb.AppendLine("  head fields that differ (offset from the byte after the length word):");
        var any = false;
        for (var f = 0; f + 1 < Head.Length; f++)
        {
            var (start, name) = Head[f];
            var end = Head[f + 1].Start;
            if (end <= IdentityEnd) continue;
            var a = r.Head.AsSpan(start, end - start);
            var b = o.Head.AsSpan(start, end - start);
            if (a.SequenceEqual(b)) continue;
            any = true;
            sb.AppendLine($"    head+{start} {name}: retail {Convert.ToHexString(a)} ours {Convert.ToHexString(b)}");
        }
        if (!any) sb.AppendLine("    none");

        sb.AppendLine($"  skills: retail {r.Skills.Count}, ours {o.Skills.Count}; only retail [{string.Join(' ', r.Skills.Except(o.Skills).Take(12))}] only ours [{string.Join(' ', o.Skills.Except(r.Skills).Take(12))}]");
        sb.AppendLine($"  quests: retail {r.ActiveQuests.Count}, ours {o.ActiveQuests.Count}; same ids {r.ActiveQuests.SequenceEqual(o.ActiveQuests)}");
        sb.AppendLine($"  khara accepted: retail {r.KharaActive.Count}, ours {o.KharaActive.Count}; identical {r.KharaActive.Select(k => (k.Id, k.State, string.Join(',', k.Counts))).SequenceEqual(o.KharaActive.Select(k => (k.Id, k.State, string.Join(',', k.Counts))))}; "
            + $"completed: retail {r.KharaDone.Count}, ours {o.KharaDone.Count}; identical {r.KharaDone.SequenceEqual(o.KharaDone)}");
        sb.AppendLine($"  u32 list after the quests (our builder writes it empty): retail {r.QuestDoneCount}, ours {o.QuestDoneCount}");
        sb.AppendLine($"  list after the stat block (our builder writes it empty): retail {r.AfterStatEntries} entries x 13 B (exact: {r.AfterStatExact}), ours {o.AfterStatEntries}");
        sb.AppendLine($"  fixed tail: retail {Convert.ToHexString(r.Tail)}");
        sb.AppendLine($"              ours   {Convert.ToHexString(o.Tail)}");
        sb.Append(Items("item", r.Items, o.Items));
        return sb.ToString();
    }

    /// <summary>The 2307 character list: headers (identity masked) and worn records, character by character.</summary>
    public static string CharList(byte[] retail, byte[] ours)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"2307 OnCharacterList detail: retail {retail.Length} B, ours {ours.Length} B");
        if (RetailListChar.ParseList(retail, out _) is not { } r || RetailListChar.ParseList(ours, out _) is not { } o || r.Length != o.Length)
        {
            sb.AppendLine("  (a body did not parse, or the character counts differ; no detail)");
            return sb.ToString();
        }

        var headerOffsets = new SortedDictionary<int, int>();
        var worn = new List<byte[]>[2] { [], [] };
        for (var c = 0; c < r.Length; c++)
        {
            for (var i = 8; i < 102; i++)    // [0..7] is the uid: ours is built from the character id, retail's is its own
                if (r[c].Header[i] != o[c].Header[i]) headerOffsets[i] = headerOffsets.GetValueOrDefault(i) + 1;
            worn[0].AddRange(r[c].Worn);
            worn[1].AddRange(o[c].Worn);
        }
        sb.AppendLine($"  {r.Length} characters; header offsets that differ (offset: characters): "
            + (headerOffsets.Count == 0 ? "none" : string.Join(", ", headerOffsets.Select(kv => $"{kv.Key}: {kv.Value}"))));
        sb.Append(Items("worn record", worn[0], worn[1]));
        return sb.ToString();
    }

    /// <summary>Records matched by uid; which record offsets disagree and in how many records.</summary>
    private static string Items(string label, List<byte[]> retail, List<byte[]> ours)
    {
        var sb = new StringBuilder();
        var byUid = ours.GroupBy(x => BinaryPrimitives.ReadUInt64LittleEndian(x)).ToDictionary(g => g.Key, g => g.ToList());
        int paired = 0, identical = 0;
        var offsets = new SortedDictionary<int, int>();
        foreach (var rec in retail)
        {
            if (!byUid.TryGetValue(BinaryPrimitives.ReadUInt64LittleEndian(rec), out var list) || list.Count == 0) continue;
            var mine = list[0]; list.RemoveAt(0);
            paired++;
            var bad = Enumerable.Range(0, RetailPc.RecordLength).Where(i => rec[i] != mine[i]).ToList();
            if (bad.Count == 0) identical++;
            foreach (var i in bad) offsets[i] = offsets.GetValueOrDefault(i) + 1;
        }
        sb.AppendLine($"  {label}s: retail {retail.Count}, ours {ours.Count}, paired by uid {paired}, byte-identical {identical}, same order {retail.Select(x => BinaryPrimitives.ReadUInt64LittleEndian(x)).SequenceEqual(ours.Select(x => BinaryPrimitives.ReadUInt64LittleEndian(x)))}");
        if (offsets.Count > 0)
            sb.AppendLine($"    {label} offsets that differ (offset: records): " + string.Join(", ", offsets.Select(kv => $"+{kv.Key}: {kv.Value}")));
        return sb.ToString();
    }
}
