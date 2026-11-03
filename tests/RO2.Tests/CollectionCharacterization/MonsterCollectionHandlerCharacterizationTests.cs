using RO2.Collections.Domain;
using RO2.Inventory.Domain;
using RO2.Network.Protocol;
using RO2.Server.Zone.Services;
using Xunit;
using static RO2.Tests.CollectionCharacterization.CollData;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.CollectionCharacterization;

/// <summary>
/// B10 S1: the monster-collection RMIs (1620 book, 1621 select, 1622 combine DNA, 1623 feed DNA) driven through the REAL handlers
/// over a loopback session, with the synthetic GameData of <see cref="CollData"/>. Pins the reply frames and their ORDER, the result
/// codes (30004 no-DNA, 30005 max level, 30008 registered, 30009 exp gained), the SILENT 1622 refusals (no reply, nothing taken), the
/// stored state after each call and the storage-failure policy. Every value was observed from the current code.
/// </summary>
[Collection(CollectionCharacterizationCollection.Name)]
public class MonsterCollectionHandlerCharacterizationTests
{
    private static void AssertFrames(List<(ushort Op, byte[] Body)> actual, params (ushort Op, byte[] Body)[] expected)
        => Assert.Equal(expected.Select(Hexed).ToArray(), actual.Select(Hexed).ToArray());

    private static (ushort, byte[]) Skill2914(MonsterCollectionState s) => (ZoneS2COpcodes.NfyMCollectionSkill, CollectionPackets.BuildNfyMCollectionSkill(s));
    private static (ushort, byte[]) Count2913(int n, CollectRateHeader h) => (ZoneS2COpcodes.NfyMCollection, CollectionPackets.BuildNfyMCollection(n, h));
    private static (ushort, byte[]) Ack2923(uint id, ushort r) => (ZoneS2COpcodes.AnsMCollectionUseProtein, CollectionPackets.BuildCollectionIdResultAck(id, r));

    private static void Register(CollRig r, uint npc, byte? level = null, uint exp = 0)
    {
        r.Book.Seed(new CollectionDocument { Id = CollRig.Self, RegisteredMonsterIds = { npc } });
        if (level is { } l)
            r.Progress.Seed(new MonsterCollectionProgressDocument { Id = CollRig.Self, Entries = { new MonsterCollectionEntry { NpcId = npc, Level = l, Exp = exp } } });
    }

    // ------------------------------------------------------------------------------------------------ 1623 feed DNA

    [Fact]
    public async Task The_first_DNA_registers_the_collection_at_level_1_exp_0_and_replies_2329_2914_2913_2923_in_that_order()
    {
        await using var r = await CollRig.StartAsync();
        var dna = r.Give(Dna, 3, 0);

        var frames = await r.CallAsync(r.Handlers.Mon_1623, CollRig.Cell(CellOf(0), 0, Mon));

        AssertFrames(frames, r.Use2329(dna, 0, 2), Skill2914(new(Mon, Skill1, 1, 0)), Count2913(1, new(1, 3, 1000)), Ack2923(Mon, 30008));
        Assert.Equal(new uint[] { Mon }, r.Book.Stored(CollRig.Self)!.RegisteredMonsterIds);
        var e = Assert.Single(r.Progress.Stored(CollRig.Self)!.Entries);
        Assert.Equal((Mon, (byte)1, 0u), (e.NpcId, e.Level, e.Exp));
        Assert.Equal(2, r.CountOf(Dna));
    }

    [Theory]
    // seeded (level, exp) -> (result, level, exp) after one DNA; CollectMonster.Exp of Mon: L1 2, L2 3, L3 1
    [InlineData(1, 0, 30009, 1, 1)]
    [InlineData(1, 1, 30009, 2, 0)]     // exp 2 >= row.Exp 2: levels up and the exp resets
    [InlineData(1, 9, 30009, 2, 0)]     // a banked exp already above the row still levels up exactly once
    [InlineData(2, 1, 30009, 2, 2)]
    [InlineData(2, 2, 30009, 3, 0)]     // row.Exp is read for the CURRENT level (L2 = 3)
    [InlineData(3, 0, 30009, 4, 0)]     // L3 needs 1
    public async Task A_DNA_for_a_registered_collection_adds_1_exp_and_levels_up_when_it_reaches_the_current_levels_Exp(
        byte level, uint exp, ushort result, byte newLevel, uint newExp)
    {
        await using var r = await CollRig.StartAsync();
        Register(r, Mon, level, exp);
        var dna = r.Give(Dna, 5, 0);

        var frames = await r.CallAsync(r.Handlers.Mon_1623, CollRig.Cell(CellOf(0), 0, Mon));

        var skill = new[] { 0u, Skill1, Skill2, Skill3, Skill4 }[newLevel];
        AssertFrames(frames, r.Use2329(dna, 0, 4), Skill2914(new(Mon, skill, newLevel, newExp)), Count2913(1, new(1, 3, 1000)), Ack2923(Mon, result));
        var e = Assert.Single(r.Progress.Stored(CollRig.Self)!.Entries);
        Assert.Equal((newLevel, newExp), (e.Level, e.Exp));
        Assert.Equal(new uint[] { Mon }, r.Book.Stored(CollRig.Self)!.RegisteredMonsterIds);   // already registered: no second registration
        Assert.Equal(4, r.CountOf(Dna));
    }

    [Fact]
    public async Task A_registered_collection_with_no_progress_entry_starts_at_level_1_exp_0_and_the_entry_is_created()
    {
        await using var r = await CollRig.StartAsync();
        Register(r, Mon);                                  // registered, nothing in the progress store
        var dna = r.Give(Dna, 2, 0);

        var frames = await r.CallAsync(r.Handlers.Mon_1623, CollRig.Cell(CellOf(0), 0, Mon));

        AssertFrames(frames, r.Use2329(dna, 0, 1), Skill2914(new(Mon, Skill1, 1, 1)), Count2913(1, new(1, 3, 1000)), Ack2923(Mon, 30009));
        var e = Assert.Single(r.Progress.Stored(CollRig.Self)!.Entries);
        Assert.Equal((Mon, (byte)1, 1u), (e.NpcId, e.Level, e.Exp));
    }

    [Fact]
    public async Task QUIRK_an_unregistered_collection_with_a_stale_progress_entry_is_reset_to_level_1_exp_0_by_the_registering_DNA()
    {
        await using var r = await CollRig.StartAsync();
        r.Progress.Seed(new MonsterCollectionProgressDocument { Id = CollRig.Self, Entries = { new MonsterCollectionEntry { NpcId = Mon, Level = 3, Exp = 1 } } });
        r.Give(Dna, 1, 0);

        var frames = await r.CallAsync(r.Handlers.Mon_1623, CollRig.Cell(CellOf(0), 0, Mon));

        Assert.Equal("30008", BitConverter.ToUInt16(frames[^1].Body, 4).ToString());
        var e = Assert.Single(r.Progress.Stored(CollRig.Self)!.Entries);
        Assert.Equal((Mon, (byte)1, 0u), (e.NpcId, e.Level, e.Exp));    // the stored (3,1) was overwritten
    }

    [Fact]
    public async Task The_last_DNA_of_a_stack_removes_the_row_and_the_2329_carries_remaining_0()
    {
        await using var r = await CollRig.StartAsync();
        var dna = r.Give(Dna, 1, 0);

        var frames = await r.CallAsync(r.Handlers.Mon_1623, CollRig.Cell(CellOf(0), 0, Mon));

        Assert.Equal(r.Use2329(dna, 0, 0).Item2, frames[0].Body);
        Assert.Null(r.Cellrow(0));
    }

    [Fact]
    public async Task A_collection_at_its_max_level_answers_30005_alone_and_takes_nothing()
    {
        await using var r = await CollRig.StartAsync();
        Register(r, Mon, 4, 0);
        r.Give(Dna, 3, 0);
        var savesBefore = r.Book.Saves;

        var frames = await r.CallAsync(r.Handlers.Mon_1623, CollRig.Cell(CellOf(0), 0, Mon));

        AssertFrames(frames, Ack2923(Mon, 30005));
        Assert.Equal(3, r.CountOf(Dna));
        var e = Assert.Single(r.Progress.Stored(CollRig.Self)!.Entries);
        Assert.Equal((4, 0u), ((int)e.Level, e.Exp));
        Assert.Equal(savesBefore, r.Book.Saves);
    }

    [Fact]
    public async Task A_one_level_collection_is_registered_by_the_first_DNA_then_is_at_max_level_with_no_progress_entry_needed()
    {
        await using var r = await CollRig.StartAsync();
        r.Give(Dna, 2, 0);
        // MonSingle's DNA is the shared Dna item (its recipe row carries it).
        var first = await r.CallAsync(r.Handlers.Mon_1623, CollRig.Cell(CellOf(0), 0, MonSingle));
        Assert.Equal(30008, BitConverter.ToUInt16(first[^1].Body, 4));

        var second = await r.CallAsync(r.Handlers.Mon_1623, CollRig.Cell(CellOf(0), 0, MonSingle));
        AssertFrames(second, Ack2923(MonSingle, 30005));      // level 1 >= MaxLevel 1: no exp is ever banked
        Assert.Equal(1, r.CountOf(Dna));
    }

    [Theory]
    [InlineData(1, 70)]     // bag 1 cells are 50..69
    [InlineData(1, 49)]
    [InlineData(5, 0)]      // there is no bag 5
    [InlineData(2, 35)]     // bags 2..4 hold 35 cells
    [InlineData(0, 50)]
    public async Task A_cell_that_is_not_a_bag_cell_answers_30004_alone_and_stores_nothing(byte bag, byte cell)
    {
        await using var r = await CollRig.StartAsync();
        r.Give(Dna, 3, 0);

        var frames = await r.CallAsync(r.Handlers.Mon_1623, CollRig.Cell(cell, 0, Mon, bag));

        AssertFrames(frames, Ack2923(Mon, 30004));
        Assert.Equal(3, r.CountOf(Dna));
        Assert.Null(r.Book.Stored(CollRig.Self));
        Assert.Null(r.Progress.Stored(CollRig.Self));
    }

    [Fact]
    public async Task An_empty_cell_or_a_cell_holding_another_item_answers_30004_alone()
    {
        await using var r = await CollRig.StartAsync();
        r.Give(Protein, 3, 1);                                  // the wrong item in the named cell
        AssertFrames(await r.CallAsync(r.Handlers.Mon_1623, CollRig.Cell(CellOf(0), 0, Mon)), Ack2923(Mon, 30004));   // empty cell
        AssertFrames(await r.CallAsync(r.Handlers.Mon_1623, CollRig.Cell(CellOf(1), 0, Mon)), Ack2923(Mon, 30004));   // wrong item
        Assert.Equal(3, r.CountOf(Protein));
    }

    [Fact]
    public async Task A_short_body_and_an_unknown_collection_are_not_answered_at_all()
    {
        await using var r = await CollRig.StartAsync();
        r.Give(Dna, 3, 0);

        Assert.Empty(await r.CallAsync(r.Handlers.Mon_1623, new byte[6]));
        Assert.Empty(await r.CallAsync(r.Handlers.Mon_1623, CollRig.Cell(CellOf(0), 0, 99999)));
        Assert.Equal(3, r.CountOf(Dna));
        Assert.Null(r.Book.Stored(CollRig.Self));
    }

    [Fact]
    public async Task A_storage_failure_is_swallowed_the_client_still_gets_the_success_and_the_DNA_is_consumed_but_nothing_persists()
    {
        await using var r = await CollRig.StartAsync();
        r.Book.ThrowOnSave = true;
        r.Progress.ThrowOnSave = true;
        var dna = r.Give(Dna, 2, 0);

        var frames = await r.CallAsync(r.Handlers.Mon_1623, CollRig.Cell(CellOf(0), 0, Mon));

        AssertFrames(frames, r.Use2329(dna, 0, 1), Skill2914(new(Mon, Skill1, 1, 0)), Count2913(1, new(1, 3, 1000)), Ack2923(Mon, 30008));
        Assert.Null(r.Book.Stored(CollRig.Self));
        Assert.Null(r.Progress.Stored(CollRig.Self));
        Assert.Equal(1, r.CountOf(Dna));
    }

    [Fact]
    public async Task A_progress_load_failure_reads_as_an_empty_document_level_1_exp_0()
    {
        await using var r = await CollRig.StartAsync();
        Register(r, Mon, 3, 0);                                  // stored (3,0) but the load throws
        r.Progress.ThrowOnGet = true;
        var dna = r.Give(Dna, 2, 0);

        var frames = await r.CallAsync(r.Handlers.Mon_1623, CollRig.Cell(CellOf(0), 0, Mon));

        AssertFrames(frames, r.Use2329(dna, 0, 1), Skill2914(new(Mon, Skill1, 1, 1)), Count2913(1, new(1, 3, 1000)), Ack2923(Mon, 30009));
    }

    // ------------------------------------------------------------------------------------------------ 1622 combine DNA (silent refusals)

    private static async Task<CollRig> CombineRigAsync(int protein = 5, int pieceA = 9, int pieceB = 3)
    {
        var r = await CollRig.StartAsync();
        r.Give(Protein, protein, 0);        // flat 0 = the cell the request names
        r.Give(PieceA, pieceA, 1);
        r.Give(PieceB, pieceB, 2);
        return r;
    }

    private static (uint Item, int Count, short Slot)[] Bag(CollRig r)
        => r.Fx.Inventory.Rows.Where(x => x.StorageType == 0).OrderBy(x => x.Slot).Select(x => (x.ItemId, x.Count, x.Slot)).ToArray();

    [Fact]
    public async Task A_combine_takes_the_protein_and_the_grouped_pieces_places_the_DNA_in_the_lowest_free_cell_and_replies_2329_2333_2915_2922()
    {
        await using var r = await CombineRigAsync();
        var protein = r.Cellrow(0)!;

        // times 2: protein 2 x Num_Combine1(2) = 4; piece A (Piece1 x2 + Piece4 x1 = 3 per combine) = 6; piece B = 2
        var frames = await r.CallAsync(r.Handlers.Mon_1622, CollRig.Cell(CellOf(0), 2, Mon));

        Assert.Equal(new[] { (Protein, 1, (short)0), (PieceA, 3, (short)1), (PieceB, 1, (short)2), (Dna, 2, (short)3) }, Bag(r));
        var placed = r.Cellrow(3)!;
        AssertFrames(frames,
            r.Use2329(protein, 0, 1),
            (ZoneS2COpcodes.OnPickupItem, LootNotifyPackets.BuildPickupItem2333(ItemPackets.BuildItemRecord126(placed), resultCode: 0, pickupKind: 0)),
            (ZoneS2COpcodes.NfyMCollectionSelectedSkill, CollectionPackets.BuildNfyMCollectionSelectedSkill(
                new MonsterCollectionState(Mon, Skill1, 0, 0), new PieceCounts(3, 1, 0, 3))),     // unregistered: level 0 with the level-1 skill; Piece4 repeats Piece1's item
            (ZoneS2COpcodes.AnsMCollectionCombinProtein, CollectionPackets.BuildCollectionIdResultAck(Mon, 0)));
        Assert.Null(r.Book.Stored(CollRig.Self));            // combining never registers
    }

    [Fact]
    public async Task A_combine_spends_pieces_from_the_lowest_slot_first_removes_emptied_stacks_and_tops_up_an_existing_DNA_stack()
    {
        await using var r = await CollRig.StartAsync();
        var protein = r.Give(Protein, 2, 0);
        r.Give(PieceA, 2, 1);               // the lowest-slot stack of A is spent first and goes empty
        r.Give(PieceA, 5, 2);
        r.Give(PieceB, 1, 3);
        r.Give(Dna, 10, 4);                 // an existing DNA stack with room: topped up, no new stack
        Register(r, Mon, 2, 1);

        var frames = await r.CallAsync(r.Handlers.Mon_1622, CollRig.Cell(CellOf(0), 1, Mon));

        Assert.Equal(new[] { (PieceA, 4, (short)2), (Dna, 11, (short)4) }, Bag(r));   // protein (2/2) and B (1/1) vanished; A: 3 from slot 1 (2) then slot 2 (1)
        AssertFrames(frames,
            r.Use2329(protein, 0, 0),
            (ZoneS2COpcodes.OnPickupItem, LootNotifyPackets.BuildPickupItem2333(ItemPackets.BuildItemRecord126(r.Cellrow(4)!), resultCode: 0, pickupKind: 0)),
            (ZoneS2COpcodes.NfyMCollectionSelectedSkill, CollectionPackets.BuildNfyMCollectionSelectedSkill(
                new MonsterCollectionState(Mon, Skill2, 2, 1), new PieceCounts(4, 0, 0, 4))),     // registered: the stored level and exp
            (ZoneS2COpcodes.AnsMCollectionCombinProtein, CollectionPackets.BuildCollectionIdResultAck(Mon, 0)));
    }

    [Fact]
    public async Task Every_1622_refusal_is_silent_and_takes_nothing()
    {
        await using var r = await CombineRigAsync(protein: 3);   // 3 protein: enough for 1 combine (2), not for 2 (4)
        var before = Bag(r);

        Assert.Empty(await r.CallAsync(r.Handlers.Mon_1622, new byte[6]));                                           // short body
        Assert.Empty(await r.CallAsync(r.Handlers.Mon_1622, CollRig.Cell(CellOf(0), 1, 99999)));                      // unknown collection
        Assert.Empty(await r.CallAsync(r.Handlers.Mon_1622, CollRig.Cell(CellOf(0), 0, Mon)));                        // times 0
        Assert.Empty(await r.CallAsync(r.Handlers.Mon_1622, CollRig.Cell(CellOf(0), 1, MonNoRecipe)));                // the recipe has no DNA result
        Assert.Empty(await r.CallAsync(r.Handlers.Mon_1622, CollRig.Cell(CellOf(5), 1, Mon)));                        // the named cell is empty
        Assert.Empty(await r.CallAsync(r.Handlers.Mon_1622, CollRig.Cell(CellOf(1), 1, Mon)));                        // the named cell holds the wrong item (piece A)
        Assert.Empty(await r.CallAsync(r.Handlers.Mon_1622, CollRig.Cell(CellOf(0), 2, Mon)));                        // protein 3 < 4
        Assert.Empty(await r.CallAsync(r.Handlers.Mon_1622, CollRig.Cell(70, 1, Mon)));                               // not a bag cell
        Assert.Empty(await r.CallAsync(r.Handlers.Mon_1622, CollRig.Cell(CellOf(0), 4, Mon)));                        // pieces: 4 x 3 = 12 A > 9 held
        Assert.Equal(before, Bag(r));
    }

    [Fact]
    public async Task A_combine_short_of_one_grouped_piece_is_silent()
    {
        await using var r = await CombineRigAsync(pieceA: 5);    // 2 combines need 6 A (Piece1 x2 + Piece4 x1, grouped), 5 held
        var before = Bag(r);

        Assert.Empty(await r.CallAsync(r.Handlers.Mon_1622, CollRig.Cell(CellOf(0), 2, Mon)));
        Assert.Equal(before, Bag(r));
        Assert.NotEmpty(await r.CallAsync(r.Handlers.Mon_1622, CollRig.Cell(CellOf(0), 1, Mon)));   // 1 combine needs 3 A: fine
    }

    [Fact]
    public async Task A_combine_with_no_free_bag_cell_for_the_DNA_is_silent_and_takes_nothing()
    {
        // MonBigDna's DNA (BigDna) has Stack_Max 1: 2 combines need 2 free cells. Fill the bag leaving exactly one.
        await using var r = await CollRig.StartAsync();
        r.Give(Protein, 4, 0);
        r.Give(PieceA, 9, 1);
        r.Give(PieceB, 9, 2);
        for (var flat = 3; flat < 19; flat++) r.Give(Filler, 1, flat);       // 0..18 occupied, only cell 19 free
        var before = Bag(r);

        Assert.Empty(await r.CallAsync(r.Handlers.Mon_1622, CollRig.Cell(CellOf(0), 2, MonBigDna)));      // needs 2 cells, 1 free
        Assert.Equal(before, Bag(r));

        var frames = await r.CallAsync(r.Handlers.Mon_1622, CollRig.Cell(CellOf(0), 1, MonBigDna));      // 1 cell: fits
        Assert.Equal(new ushort[] { ZoneS2COpcodes.OnUseItem, ZoneS2COpcodes.OnPickupItem, ZoneS2COpcodes.NfyMCollectionSelectedSkill, ZoneS2COpcodes.AnsMCollectionCombinProtein },
            frames.Select(f => f.Op).ToArray());
        Assert.Equal((BigDna, 1, (short)19), r.Fx.Inventory.Rows.Where(x => x.ItemId == BigDna).Select(x => (x.ItemId, x.Count, x.Slot)).Single());
    }

    // ------------------------------------------------------------------------------------------------ 1621 select

    [Fact]
    public async Task Selecting_an_unregistered_collection_stores_the_selection_does_not_register_and_answers_2912_at_level_0_with_the_bag_piece_counts()
    {
        await using var r = await CollRig.StartAsync();
        r.Give(PieceA, 5, 0);
        r.Give(PieceB, 2, 1);

        var frames = await r.CallAsync(r.Handlers.Mon_1621, CollRig.Id(Mon));

        AssertFrames(frames, (ZoneS2COpcodes.AnsMCollectionSelectedSkill, CollectionPackets.BuildAnsMCollectionSelectedSkill(
            new MonsterCollectionState(Mon, Skill1, 0, 0), new PieceCounts(5, 2, 0, 5), 0)));
        var book = r.Book.Stored(CollRig.Self)!;
        Assert.Equal(new uint[] { Mon }, book.SelectedMonsterSkills);
        Assert.Empty(book.RegisteredMonsterIds);                 // selecting never registers
    }

    [Fact]
    public async Task Selecting_a_registered_collection_answers_its_stored_level_and_exp_and_selecting_twice_stores_one_entry()
    {
        await using var r = await CollRig.StartAsync();
        Register(r, Mon, 3, 1);

        var expected = (ZoneS2COpcodes.AnsMCollectionSelectedSkill, CollectionPackets.BuildAnsMCollectionSelectedSkill(
            new MonsterCollectionState(Mon, Skill3, 3, 1), default, 0));
        AssertFrames(await r.CallAsync(r.Handlers.Mon_1621, CollRig.Id(Mon)), expected);
        AssertFrames(await r.CallAsync(r.Handlers.Mon_1621, CollRig.Id(Mon)), expected);
        Assert.Equal(new uint[] { Mon }, r.Book.Stored(CollRig.Self)!.SelectedMonsterSkills);
        Assert.Equal(1, r.Book.Saves);                           // Register seeds without a save; only the first select wrote
    }

    [Fact]
    public async Task QUIRK_selecting_id_0_or_a_short_body_stores_nothing_but_still_answers_and_an_unknown_id_IS_stored()
    {
        await using var r = await CollRig.StartAsync();

        AssertFrames(await r.CallAsync(r.Handlers.Mon_1621, new byte[2]), (ZoneS2COpcodes.AnsMCollectionSelectedSkill,
            CollectionPackets.BuildAnsMCollectionSelectedSkill(new MonsterCollectionState(0, 0, 0, 0), default, 0)));
        Assert.Null(r.Book.Stored(CollRig.Self));                // id 0: nothing stored

        AssertFrames(await r.CallAsync(r.Handlers.Mon_1621, CollRig.Id(99999)), (ZoneS2COpcodes.AnsMCollectionSelectedSkill,
            CollectionPackets.BuildAnsMCollectionSelectedSkill(new MonsterCollectionState(99999, 0, 0, 0), default, 0)));
        Assert.Equal(new uint[] { 99999 }, r.Book.Stored(CollRig.Self)!.SelectedMonsterSkills);   // no table check on select
    }

    // ------------------------------------------------------------------------------------------------ 1620 open the book

    [Fact]
    public async Task Opening_the_book_lists_the_registered_collections_ascending_distinct_with_their_stored_progress_and_the_rate_header_of_the_count()
    {
        await using var r = await CollRig.StartAsync();
        r.Book.Seed(new CollectionDocument { Id = CollRig.Self, RegisteredMonsterIds = { MonSingle, 0, Mon, MonSingle } });
        r.Progress.Seed(new MonsterCollectionProgressDocument { Id = CollRig.Self, Entries = { new MonsterCollectionEntry { NpcId = Mon, Level = 2, Exp = 1 } } });

        var frames = await r.CallAsync(r.Handlers.Mon_1620, []);

        AssertFrames(frames, (ZoneS2COpcodes.AnsMCollection, CollectionPackets.BuildAnsMCollection(
            [new MonsterCollectionState(Mon, Skill2, 2, 1), new MonsterCollectionState(MonSingle, 5201, 1, 0)], new CollectRateHeader(1, 3, 1000))));
    }

    [Fact]
    public async Task Opening_an_empty_book_answers_the_empty_list_with_the_tier_of_count_0()
    {
        await using var r = await CollRig.StartAsync();

        var frames = await r.CallAsync(r.Handlers.Mon_1620, []);

        AssertFrames(frames, (ZoneS2COpcodes.AnsMCollection, CollectionPackets.BuildAnsMCollection([], new CollectRateHeader(1, 3, 1000))));
    }

    [Fact]
    public async Task A_book_load_failure_opens_as_an_empty_book()
    {
        await using var r = await CollRig.StartAsync();
        r.Book.Seed(new CollectionDocument { Id = CollRig.Self, RegisteredMonsterIds = { Mon } });
        r.Book.ThrowOnGet = true;

        var frames = await r.CallAsync(r.Handlers.Mon_1620, []);

        AssertFrames(frames, (ZoneS2COpcodes.AnsMCollection, CollectionPackets.BuildAnsMCollection([], new CollectRateHeader(1, 3, 1000))));
    }
}
