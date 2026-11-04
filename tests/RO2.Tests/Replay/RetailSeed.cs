using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using RO2.Characters.Domain;
using RO2.GameData;
using RO2.Inventory.Domain;
using RO2.Kharas.Domain;
using RO2.Network.Packets;
using RO2.Quests.Domain;
using RO2.Server.Zone.Services;
using RO2.Skills.Domain;

namespace RO2.Tests.Replay;

/// <summary>
/// The character a retail capture logged in, recovered from retail's own world-entry bytes: the 2310 OnPcInfo (offsets are the ones
/// <c>WorldEntryPackets.HeadOffset</c> writes, counted from the byte after the 4-byte length word) and the 2307 character list
/// (102-byte headers, offsets from <c>CharacterSerializer.WriteCharacterHeader</c>). Parsing reads the same layout our builder writes,
/// so what the replay then diffs is the builder against the retail bytes it was modelled on.
/// </summary>
internal sealed class RetailPc
{
    public const int HeadLength = 205;
    public const int RecordLength = 126;

    public required byte[] Head { get; init; }
    public required List<uint> Skills { get; init; }
    public required List<byte[]> Items { get; init; }
    public required List<uint> ActiveQuests { get; init; }
    public required List<(uint Id, uint State, List<uint> Counts)> KharaActive { get; init; }
    public required List<uint> KharaDone { get; init; }
    /// <summary>Entries of the u32 list after the quests (retail fills it, our builder writes it empty).</summary>
    public int QuestDoneCount { get; init; }
    /// <summary>Entries of the list after the stat block, and whether the bytes up to the 30-byte fixed tail are exactly entries x 13.</summary>
    public int AfterStatEntries { get; init; }
    public bool AfterStatExact { get; init; }
    /// <summary>The last 30 bytes: u32, two bytes, 16 zeros, two bytes, 6 zeros in the retail captures; our builder writes a constant.</summary>
    public byte[] Tail { get; init; } = [];

    private ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(Head.AsSpan(o));
    private uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(Head.AsSpan(o));
    private ulong U64(int o) => BinaryPrimitives.ReadUInt64LittleEndian(Head.AsSpan(o));
    private float F32(int o) => BitConverter.ToSingle(Head, o);

    public ulong Uid => U64(0);
    public string Name => System.Text.Encoding.Unicode.GetString(Head, 12, 42).TrimEnd('\0');
    public byte[] Look => Head[54..69];
    public byte Level => Head[69];
    public ulong Exp => U64(70);
    public byte CombatClass => Head[78];
    public uint CurHp => U32(81);
    public ushort CurSp => (ushort)U32(85);
    public (float X, float Y, float Z) Pos => (F32(89), F32(93), F32(97));
    public uint MapId => U32(101);
    public ushort BindIndex => U16(105);
    public byte Facing => Head[107];
    public ulong Wallet => U64(112);
    public byte BankSlots => Head[121];
    public uint KharaPoints => U32(126);
    public byte CreationProfession => Head[130];
    public byte JobLevel => Head[131];
    public ulong JobExp => U64(132);
    public ushort EquipHide => U16(140);
    public uint TitleATimer => U32(143);
    public uint TitleA => U32(147);
    public ushort[] Bonus5 => [U16(151), U16(153), U16(155), U16(157), U16(159)];
    public uint UnionId => U32(173);
    public ulong UnionJoined => U64(177);
    public uint TitleB => U32(185);
    public byte ProJobLevel => Head[189];
    public byte ProJobId => Head[190];
    public ulong JobMastery => U64(191);

    /// <summary>Parses a captured 2310 body (length word first). Null when it does not walk cleanly to its stat block.</summary>
    public static RetailPc? Parse(byte[] b)
    {
        try
        {
            var o = 4;
            var head = b[o..(o + HeadLength)]; o += HeadLength;
            var skills = new List<uint>();
            var n = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o)); o += 2;
            for (var i = 0; i < n; i++, o += 4) skills.Add(BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o)));
            var items = new List<byte[]>();
            n = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o)); o += 2;
            for (var i = 0; i < n; i++, o += RecordLength) items.Add(b[o..(o + RecordLength)]);
            o += 2; // the vector our builder writes empty
            var quests = new List<uint>();
            n = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o)); o += 2;
            for (var i = 0; i < n; i++)
            {
                int a = b[o], second = b[o + 1];
                if (second != 0) return null; // our builder always writes 0; any other shape is not modelled
                quests.Add(BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o + 2)));
                o += 2 + 12 + a * 20;
            }
            n = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o)); o += 2;
            var questDone = n;
            o += 4 * n; // retail's list of 4-byte ids here (completed quests); our builder writes it empty, see the report
            var active = new List<(uint, uint, List<uint>)>();
            n = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o)); o += 2;
            for (var i = 0; i < n; i++)
            {
                var id = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
                var state = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o + 4));
                var rows = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o + 12)); o += 14;
                var counts = new List<uint>();
                for (var r = 0; r < rows; r++, o += 4) counts.Add(BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o)));
                active.Add((id, state, counts));
            }
            var done = new List<uint>();
            n = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o)); o += 2;
            for (var i = 0; i < n; i++, o += 4) done.Add(BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o)));
            // The stat block (size word 0x1C) must follow, or the walk lost its place.
            if (BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o)) != 0x1C) return null;
            o += 0x1C;
            var after = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o)); o += 2;
            const int FixedTail = 30;
            if (b.Length - o < FixedTail) return null;
            return new RetailPc
            {
                Head = head, Skills = skills, Items = items, ActiveQuests = quests, KharaActive = active, KharaDone = done,
                QuestDoneCount = questDone, AfterStatEntries = after, AfterStatExact = b.Length - o - FixedTail == after * 13, Tail = b[^FixedTail..],
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException) { return null; }
    }
}

/// <summary>One character of the retail 2307 list: its 102-byte header and its worn-equipment records.</summary>
internal sealed record RetailListChar(byte[] Header, List<byte[]> Worn)
{
    public ulong Uid => BinaryPrimitives.ReadUInt64LittleEndian(Header);
    public string Name => System.Text.Encoding.Unicode.GetString(Header, 8, 42).TrimEnd('\0');
    public static RetailListChar[]? ParseList(byte[] b, out uint accountId)
    {
        accountId = 0;
        try
        {
            accountId = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4));
            var count = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8));
            var o = 10;
            var chars = new List<RetailListChar>();
            for (var c = 0; c < count; c++)
            {
                var header = b[o..(o + 102)]; o += 102;
                var worn = new List<byte[]>();
                var n = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o)); o += 2;
                for (var i = 0; i < n; i++, o += RetailPc.RecordLength) worn.Add(b[o..(o + RetailPc.RecordLength)]);
                chars.Add(new RetailListChar(header, worn));
            }
            return o == b.Length ? [.. chars] : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException) { return null; }
    }
}

/// <summary>Everything the rig needs to stand a retail account up: the account id, its characters in list order and the one that logged in.</summary>
internal sealed record RetailSeed(uint AccountId, RetailListChar[] Characters, RetailPc Own)
{
    /// <summary>The 2307 and 2310 retail sent in this connection, or null when either is missing or does not parse.</summary>
    public static RetailSeed? From(IEnumerable<CapFrame> frames)
    {
        var list = frames.FirstOrDefault(f => !f.IsC2S && f.Op == 2307);
        var info = frames.FirstOrDefault(f => !f.IsC2S && f.Op == 2310);
        if (list is null || info is null || RetailPc.Parse(info.Body) is not { } own) return null;
        if (RetailListChar.ParseList(list.Body, out var account) is not { } chars) return null;
        return chars.Any(c => c.Uid == own.Uid) ? new RetailSeed(account, chars, own) : null;
    }
}

/// <summary>Writes a <see cref="RetailSeed"/> through the real repositories, so the Zone's own LoginPC reads it back.</summary>
internal static class RetailSeeder
{
    private const int CreateBlockLength = 23;

    /// <summary>Character ids already used by earlier rigs of this process (see <see cref="SkipUsedIdsAsync"/>).</summary>
    private static int _rigs;
    private const int IdsPerRig = 40;
    private const uint ScratchAccountId = 1;

    /// <summary>
    /// Several statics in the Zone are keyed by character id and loaded once per process (KharaService's published state, the attendance
    /// day gate ...). Every rig has a fresh database, so its first character would be id 1 again and inherit the previous capture's
    /// cached state. Soft-deleted scratch characters push each rig's ids past the ones the earlier rigs used.
    /// shortcut: assumes at most IdsPerRig characters per capture (the largest has 10), raise it if a capture lists more.
    /// </summary>
    private static async Task SkipUsedIdsAsync(ICharacterRepository characters)
    {
        var burn = Interlocked.Increment(ref _rigs) - 1;
        for (var i = 0; i < burn * IdsPerRig; i++)
        {
            var (scratch, error) = await characters.CreateCharacterAsync(ScratchAccountId, "scratch" + i, 1, 0, "{}");
            if (scratch is null) throw new InvalidOperationException("could not create a scratch character: " + error);
            await characters.DeleteCharacterAsync(scratch.Id, ScratchAccountId);
        }
    }

    /// <summary>Creates every character of the retail list on <paramref name="accountId"/>; returns them in list order plus the index of the logged-in one.</summary>
    public static async Task<(List<Character> Characters, int OwnIndex, List<string> Notes)> SeedAsync(
        IServiceProvider p, GameDataStore store, RetailSeed seed, uint accountId, bool burnIds = true)
    {
        var characters = p.GetRequiredService<ICharacterRepository>();
        var inventory = p.GetRequiredService<IInventoryRepository>();
        if (burnIds) await SkipUsedIdsAsync(characters);
        var made = new List<Character>();
        var notes = new List<string>();
        var ownIndex = -1;
        for (var i = 0; i < seed.Characters.Length; i++)
        {
            var lc = seed.Characters[i];
            var own = lc.Uid == seed.Own.Uid;
            if (own) ownIndex = i;
            var h = lc.Header;

            // The 23-byte creation block the list echoes: header [50..64] are create[0..14], [65] the combat class, [73] the creation
            // profession. create[15] (hair colour) and the padding are not on the wire in either message, so they stay 0.
            var raw = new byte[CreateBlockLength];
            h.AsSpan(50, 15).CopyTo(raw);
            raw[19] = h[65];
            raw[21] = h[73];
            var appearance = CharacterSerializer.ParseCreateAppearance(new PacketReader(raw));
            var (c, error) = await characters.CreateCharacterAsync(accountId, lc.Name, h[65], h[50], CharacterSerializer.ToJson(appearance), null, h[51]);
            if (c is null) throw new InvalidOperationException($"could not seed '{lc.Name}': {error}");

            c.Level = h[66];
            c.JobLevel = h[74];
            c.EquipHideFlag = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(75));
            c.MapId = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(67));
            (c.ProJobLevel, c.LifeJob) = (h[100], h[101]);
            // Header [77-88] carries the six allocated bonus-stat u16 (STR, AGI, INT, WIS, VIT, LUK).
            ushort Bonus(int i) => BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(77 + 2 * i));
            (c.BonusStr, c.BonusAgi, c.BonusInt, c.BonusWis, c.BonusVit, c.BonusLuk) = (Bonus(0), Bonus(1), Bonus(2), Bonus(3), Bonus(4), Bonus(5));

            if (own) ApplyHead(c, seed.Own);
            await characters.SaveCharacterAsync(c);
            made.Add(c);

            if (own) await SeedOwnAsync(p, store, c, seed.Own, inventory, notes);
            else
                foreach (var rec in lc.Worn)
                    await inventory.AddItemAsync(ItemOf(rec, c.Id, store));
        }
        return (made, ownIndex, notes);
    }

    /// <summary>The head fields that live on the character row.</summary>
    private static void ApplyHead(Character c, RetailPc pc)
    {
        c.Exp = pc.Exp;
        c.JobExp = pc.JobExp;
        c.Hp = (int)pc.CurHp;
        c.Mp = pc.CurSp;
        (c.X, c.Y, c.Z) = pc.Pos;
        c.MapId = pc.MapId;
        c.BindIndex = pc.BindIndex;
        c.FacingYaw = (float)(pc.Facing * 2.0 * Math.PI / 180.0);   // inverse of CharacterHeadRules.FacingByte
        c.Gold = (long)pc.Wallet;
        c.BankSlots = pc.BankSlots;
        (c.ProJobLevel, c.LifeJob, c.JobMasteryPoints) = (pc.ProJobLevel, pc.ProJobId, pc.JobMastery);
        var b = pc.Bonus5;
        (c.BonusStr, c.BonusAgi, c.BonusInt, c.BonusWis, c.BonusVit) = (b[0], b[1], b[2], b[3], b[4]);
    }

    private static async Task SeedOwnAsync(IServiceProvider p, GameDataStore store, Character c, RetailPc pc,
        IInventoryRepository inventory, List<string> notes)
    {
        foreach (var rec in pc.Items) await inventory.AddItemAsync(ItemOf(rec, c.Id, store));

        var skills = pc.Skills.Select(s => (s / 100, (byte)(s % 100))).ToList();
        await p.GetRequiredService<ISkillRepository>().UpsertSkillsAsync(c.Id, skills);

        var quests = p.GetRequiredService<IQuestRepository>();
        foreach (var q in pc.ActiveQuests) await quests.AcceptQuestAsync(c.Id, q);

        await p.GetRequiredService<IKharaStateRepository>().SaveAsync(new KharaStateDocument
        {
            Id = c.Id,
            Points = pc.KharaPoints,
            Active = [.. pc.KharaActive.OrderBy(a => a.Id).Select(a => new KharaActiveDocument { KharaId = a.Id, State = a.State, Counts = a.Counts })],
            Completed = [.. pc.KharaDone.Order()],
            UpdatedUtc = DateTime.UtcNow,
        });

        // The worn titles: the head carries Khara_Info.Reward_Title_Id, the store keeps the Khara id that grants it.
        uint KharaOf(uint title) => title == 0 ? 0
            : store.KharaInfo.Select(r => (uint)r.Quest_ID).Distinct().FirstOrDefault(id => KharaTitleRules.TitleOf(id, store.KharaInfo) == title);
        uint a = KharaOf(pc.TitleA), bTitle = KharaOf(pc.TitleB);
        if ((pc.TitleA != 0 && a == 0) || (pc.TitleB != 0 && bTitle == 0))
            notes.Add($"title {pc.TitleA}/{pc.TitleB}: no Khara_Info row grants it, left unworn");
        if (a != 0 || bTitle != 0)
            await p.GetRequiredService<IKharaTitleRepository>().SaveAsync(new KharaTitleDocument
            {
                Id = c.Id, KharaA = a, KharaB = bTitle, PlayClockUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            });

        if (pc.UnionId != 0)
            await p.GetRequiredService<IUnionMembershipRepository>().JoinAsync(c.Id, pc.UnionId, (long)pc.UnionJoined);
    }

    /// <summary>
    /// Inverts <c>ItemPackets.WriteItemInstance</c> + <c>ItemWorkbench.ApplyRuneholes</c>: a retail 126-byte record back into the row that writes it.
    /// </summary>
    internal static InventoryItem ItemOf(byte[] r, uint characterId, GameDataStore store)
    {
        var bag = r[0x0F];
        var slot = r[0x10];
        byte storage; short flatSlot;
        if (bag == 0) (storage, flatSlot) = (1, slot);                               // worn gear
        else if (bag == 5) (storage, flatSlot) = (2, slot);                          // bank
        else if (bag == 1 && slot >= 70) (storage, flatSlot) = (3, (short)(slot - 70)); // worn bag
        else if (bag == 1) (storage, flatSlot) = (0, (short)(slot - 50));            // main bag
        else (storage, flatSlot) = (0, (short)(20 + (bag - 2) * 35 + slot));         // extra bags

        // Holes are contiguous from hole 0; a filled hole's value is the Reinforce_ID of the row whose SourceItem_ID is the socketed item.
        var holes = new List<uint>();
        var punchers = new List<uint>();
        for (var i = 0; i < ItemWorkbench.HoleCount; i++)
        {
            var hole = r.AsSpan(ItemWorkbench.RuneholeOffset + i * 7, 7);
            if (!hole.ContainsAnyExcept((byte)0)) continue;
            while (holes.Count < i) { holes.Add(0); punchers.Add(0); }
            var reinforce = BinaryPrimitives.ReadUInt16LittleEndian(hole[1..]);
            holes.Add(reinforce == 0 ? 0 : store.ItemReinforceInfo.FirstOrDefault(x => x.Reinforce_ID == reinforce)?.SourceItem_ID ?? 0);
            punchers.Add(BinaryPrimitives.ReadUInt32LittleEndian(hole[3..]));
        }

        return new InventoryItem
        {
            Id = BinaryPrimitives.ReadUInt64LittleEndian(r),
            CharacterId = characterId,
            ItemId = BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(8)),
            Durability = (short)BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(0x0C)),
            Count = r[0x0E],
            StorageType = storage,
            Slot = flatSlot,
            EnchantReinforceId = BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(0x11)),
            EnchantSeconds = BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(0x13)),
            DyeColor = BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(0x17)),
            // Bound by the template (Bind_Type) is derived; only a flag the template does not explain was stored on the row.
            Bound = (r[0x1B] & ItemPackets.RecordBoundFlag) != 0
                && ItemPackets.RecordBindFlags(store.GetItem(BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(8)))?.Bind_Type ?? 0, storage) == 0,
            Refine = r[0x1D],
            CardsJson = holes.Count == 0 ? null : ItemSockets.SerializeSockets([.. holes]),
            SocketPunchersJson = holes.Count == 0 ? null : ItemSockets.SerializeSockets([.. punchers]),
            Element = r[0x44],
            AbilityValue = BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(0x45)),
            DyeColor2 = BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(0x65)),
            DyeColor3 = BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(0x69)),
            FxEnchantIndex = BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(0x71)),
        };
    }
}
