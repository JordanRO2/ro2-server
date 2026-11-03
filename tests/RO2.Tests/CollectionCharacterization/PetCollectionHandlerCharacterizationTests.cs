using RO2.Domain.Entities;
using RO2.Inventory.Domain;
using RO2.Network.Protocol;
using RO2.Server.Zone.Services;
using Xunit;
using static RO2.Tests.CollectionCharacterization.CollData;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.CollectionCharacterization;

/// <summary>
/// B10 S1: the pet-collection RMIs (1624 book, 1625 select, 1626 combine egg blank, 1627 use pet egg, 1628/1629 active pet) driven
/// through the REAL handlers over a loopback session, with the synthetic GameData of <see cref="CollData"/>. Pins the reply frames and
/// their ORDER, the result codes (30001 loyalty-capped / no active pet, 30004 no egg), the SILENT 1626 refusals, 1629 answering result 0
/// even when it refused, the ActivePetFlag re-derived from the loyalty on every load (never trusted), and the storage-failure policy.
/// </summary>
public class PetCollectionHandlerCharacterizationTests
{
    private static void AssertFrames(List<(ushort Op, byte[] Body)> actual, params (ushort Op, byte[] Body)[] expected)
        => Assert.Equal(expected.Select(Hexed).ToArray(), actual.Select(Hexed).ToArray());

    private static (ushort, byte[]) Skill2920(PetCollectionState s) => (ZoneS2COpcodes.NfyPCollectionSkill, CollectionPackets.BuildNfyPCollectionSkill(s));
    private static (ushort, byte[]) Count2919(int n) => (ZoneS2COpcodes.NfyPCollection, CollectionPackets.BuildNfyPCollection(n));
    private static (ushort, byte[]) Egg2925(uint pet, byte flag, ushort result) => (ZoneS2COpcodes.AnsPCollectionUsePetEgg, CollectionPackets.BuildAnsPCollectionUsePetEgg(pet, flag, result));
    private static (ushort, byte[]) Active2918(uint pet, byte level, ushort result) => (ZoneS2COpcodes.AnsPCollectionSetActivePet, CollectionPackets.BuildAnsPCollectionSetActivePet(pet, level, result));

    private static CollectionDocument Book(Action<CollectionDocument>? configure = null)
    {
        var d = new CollectionDocument { Id = CollRig.Self };
        configure?.Invoke(d);
        return d;
    }

    // ------------------------------------------------------------------------------------------------ 1627 use pet egg

    [Fact]
    public async Task The_first_egg_of_a_pet_registers_it_with_no_loyalty_entry_and_replies_2329_2920_2919_2925_flag_1()
    {
        await using var r = await CollRig.StartAsync();
        var egg = r.Give(PetHatched, 2, 0);

        var frames = await r.CallAsync(r.Handlers.Pet_1627, CollRig.Cell(CellOf(0), 0, Pet));

        AssertFrames(frames, r.Use2329(egg, 0, 1), Skill2920(new(Pet, PetEffect0, 0, 0)), Count2919(1), Egg2925(Pet, 1, 0));
        var book = r.Book.Stored(CollRig.Self)!;
        Assert.Equal(new uint[] { Pet }, book.RegisteredPetIds);
        Assert.Empty(book.PetLoyalties);                          // a booked-but-unfed pet has no entry: loyalty 0 is implicit
        Assert.Equal(1, r.CountOf(PetHatched));
    }

    [Fact]
    public async Task An_egg_for_a_registered_pet_feeds_it_plus_10_and_replies_2329_2920_2919_2925_flag_2()
    {
        await using var r = await CollRig.StartAsync();
        r.Book.Seed(Book(d => d.RegisteredPetIds.Add(Pet)));
        var egg = r.Give(PetHatched, 3, 0);

        var frames = await r.CallAsync(r.Handlers.Pet_1627, CollRig.Cell(CellOf(0), 0, Pet));

        AssertFrames(frames, r.Use2329(egg, 0, 2), Skill2920(new(Pet, PetEffect0, 0, 10)), Count2919(1), Egg2925(Pet, 2, 0));   // no 2918: the pet is not the active one
        Assert.Equal((ushort)10, r.Book.Stored(CollRig.Self)!.LoyaltyOf(Pet));
        Assert.Equal(2, r.CountOf(PetHatched));
    }

    [Fact]
    public async Task Feeding_the_ACTIVE_pet_adds_a_2918_with_the_new_loyalty_level_between_the_2920_and_the_2919()
    {
        await using var r = await CollRig.StartAsync();
        r.Book.Seed(Book(d => { d.RegisteredPetIds.Add(Pet); d.ActivePetId = Pet; d.SetLoyalty(Pet, 20); }));
        var egg = r.Give(PetHatched, 1, 0);

        var frames = await r.CallAsync(r.Handlers.Pet_1627, CollRig.Cell(CellOf(0), 0, Pet));

        // 20 + 10 is clamped to the cap 25 and crosses LoyaltyLevel_1: effect 222, level 1; the 2918 carries that NEW level.
        AssertFrames(frames, r.Use2329(egg, 0, 0), Skill2920(new(Pet, PetEffect1, 1, 25)), Active2918(Pet, 1, 0), Count2919(1), Egg2925(Pet, 2, 0));
        Assert.Equal((ushort)25, r.Book.Stored(CollRig.Self)!.LoyaltyOf(Pet));
        Assert.Null(r.Cellrow(0));
    }

    [Theory]
    [InlineData(25)]
    [InlineData(30)]       // above the cap (a table edit lowered it): still capped
    public async Task A_pet_at_or_above_its_loyalty_cap_answers_30001_alone_with_flag_0_and_takes_no_egg(int loyalty)
    {
        await using var r = await CollRig.StartAsync();
        r.Book.Seed(Book(d => { d.RegisteredPetIds.Add(Pet); d.SetLoyalty(Pet, (ushort)loyalty); }));
        r.Give(PetHatched, 3, 0);
        var saves = r.Book.Saves;

        var frames = await r.CallAsync(r.Handlers.Pet_1627, CollRig.Cell(CellOf(0), 0, Pet));

        AssertFrames(frames, Egg2925(Pet, 0, 30001));
        Assert.Equal(3, r.CountOf(PetHatched));
        Assert.Equal((ushort)loyalty, r.Book.Stored(CollRig.Self)!.LoyaltyOf(Pet));
        Assert.Equal(saves, r.Book.Saves);
    }

    [Theory]
    [InlineData(0, 50)]    // an empty cell
    [InlineData(2, 50)]    // a cell holding another item (the protein)
    [InlineData(0, 70)]    // not a bag cell
    [InlineData(0, 49)]
    public async Task A_cell_without_the_hatched_egg_answers_30004_alone_even_for_an_unregistered_or_a_capped_pet(int variant, int cellByte)
    {
        await using var r = await CollRig.StartAsync();
        r.Book.Seed(Book(d => { d.RegisteredPetIds.Add(Pet); d.SetLoyalty(Pet, 25); }));   // capped: the egg check still comes first
        if (variant == 2) r.Give(Protein, 2, 0);

        var frames = await r.CallAsync(r.Handlers.Pet_1627, CollRig.Cell((byte)cellByte, 0, Pet));

        AssertFrames(frames, Egg2925(Pet, 0, 30004));
        AssertFrames(await r.CallAsync(r.Handlers.Pet_1627, CollRig.Cell((byte)cellByte, 0, PetBig)), Egg2925(PetBig, 0, 30004));   // unregistered pet: same
        Assert.DoesNotContain(PetBig, r.Book.Stored(CollRig.Self)!.RegisteredPetIds);
    }

    [Fact]
    public async Task A_pet_with_no_PetInfo_row_and_a_short_body_are_not_answered_at_all()
    {
        await using var r = await CollRig.StartAsync();
        r.Give(PetHatched, 2, 0);

        Assert.Empty(await r.CallAsync(r.Handlers.Pet_1627, CollRig.Cell(CellOf(0), 0, PetUnknown)));
        Assert.Empty(await r.CallAsync(r.Handlers.Pet_1627, new byte[6]));
        Assert.Equal(2, r.CountOf(PetHatched));
        Assert.Null(r.Book.Stored(CollRig.Self));
    }

    [Fact]
    public async Task A_storage_failure_is_swallowed_the_client_gets_the_registration_and_the_egg_is_consumed_but_nothing_persists()
    {
        await using var r = await CollRig.StartAsync();
        r.Book.ThrowOnSave = true;
        var egg = r.Give(PetHatched, 2, 0);

        var frames = await r.CallAsync(r.Handlers.Pet_1627, CollRig.Cell(CellOf(0), 0, Pet));

        AssertFrames(frames, r.Use2329(egg, 0, 1), Skill2920(new(Pet, PetEffect0, 0, 0)), Count2919(1), Egg2925(Pet, 1, 0));
        Assert.Null(r.Book.Stored(CollRig.Self));
        Assert.Equal(1, r.CountOf(PetHatched));
    }

    // ------------------------------------------------------------------------------------------------ 1629 / 1628 active pet

    [Fact]
    public async Task Setting_a_registered_pet_active_answers_its_loyalty_level_and_stores_the_active_id_and_the_derived_flag()
    {
        await using var r = await CollRig.StartAsync();
        r.Book.Seed(Book(d => { d.RegisteredPetIds.AddRange([Pet, PetBig]); d.SetLoyalty(Pet, 25); }));

        AssertFrames(await r.CallAsync(r.Handlers.Pet_1629, CollRig.Id(Pet)), Active2918(Pet, 1, 0));
        var book = r.Book.Stored(CollRig.Self)!;
        Assert.Equal((Pet, (byte)1), (book.ActivePetId, book.ActivePetFlag));

        AssertFrames(await r.CallAsync(r.Handlers.Pet_1629, CollRig.Id(PetBig)), Active2918(PetBig, 0, 0));   // never fed: level 0
        book = r.Book.Stored(CollRig.Self)!;
        Assert.Equal((PetBig, (byte)0), (book.ActivePetId, book.ActivePetFlag));
    }

    [Fact]
    public async Task QUIRK_setting_an_unregistered_pet_active_is_refused_but_still_answers_result_0_with_the_CURRENT_active_pet_and_stores_nothing()
    {
        await using var r = await CollRig.StartAsync();

        AssertFrames(await r.CallAsync(r.Handlers.Pet_1629, CollRig.Id(Pet)), Active2918(0, 0, 0));          // no active pet yet: {0, 0, result 0}
        Assert.Null(r.Book.Stored(CollRig.Self));

        r.Book.Seed(Book(d => { d.RegisteredPetIds.Add(Pet); d.ActivePetId = Pet; d.SetLoyalty(Pet, 25); }));
        var saves = r.Book.Saves;
        AssertFrames(await r.CallAsync(r.Handlers.Pet_1629, CollRig.Id(PetBig)), Active2918(Pet, 1, 0));     // refused: the answer is still the old active pet, result 0
        Assert.Equal(Pet, r.Book.Stored(CollRig.Self)!.ActivePetId);
        Assert.Equal(saves, r.Book.Saves);
    }

    [Fact]
    public async Task Setting_pet_0_or_sending_a_short_body_clears_the_active_pet()
    {
        await using var r = await CollRig.StartAsync();
        r.Book.Seed(Book(d => { d.RegisteredPetIds.Add(Pet); d.ActivePetId = Pet; d.ActivePetFlag = 1; d.SetLoyalty(Pet, 25); }));

        AssertFrames(await r.CallAsync(r.Handlers.Pet_1629, CollRig.Id(0)), Active2918(0, 0, 0));
        var book = r.Book.Stored(CollRig.Self)!;
        Assert.Equal((0u, (byte)0), (book.ActivePetId, book.ActivePetFlag));

        r.Book.Seed(Book(d => { d.RegisteredPetIds.Add(Pet); d.ActivePetId = Pet; }));
        AssertFrames(await r.CallAsync(r.Handlers.Pet_1629, new byte[3]), Active2918(0, 0, 0));          // a body under 4 bytes reads as pet 0
        Assert.Equal(0u, r.Book.Stored(CollRig.Self)!.ActivePetId);
    }

    [Fact]
    public async Task Querying_the_active_pet_answers_30001_when_there_is_none_else_its_level_with_result_0()
    {
        await using var r = await CollRig.StartAsync();

        AssertFrames(await r.CallAsync(r.Handlers.Pet_1628, []), Active2918(0, 0, 30001));

        r.Book.Seed(Book(d => { d.RegisteredPetIds.Add(Pet); d.ActivePetId = Pet; d.SetLoyalty(Pet, 24); }));
        AssertFrames(await r.CallAsync(r.Handlers.Pet_1628, []), Active2918(Pet, 0, 0));
        r.Book.Seed(Book(d => { d.RegisteredPetIds.Add(Pet); d.ActivePetId = Pet; d.SetLoyalty(Pet, 25); }));
        AssertFrames(await r.CallAsync(r.Handlers.Pet_1628, []), Active2918(Pet, 1, 0));
    }

    [Fact]
    public async Task The_stored_ActivePetFlag_is_never_trusted_every_load_re_derives_it_from_the_pets_loyalty()
    {
        await using var r = await CollRig.StartAsync();
        r.Book.Seed(Book(d => { d.RegisteredPetIds.Add(Pet); d.ActivePetId = Pet; d.ActivePetFlag = 9; d.SetLoyalty(Pet, 25); }));   // bogus flag 9

        AssertFrames(await r.CallAsync(r.Handlers.Pet_1628, []), Active2918(Pet, 1, 0));            // derived 1, not 9
        Assert.Equal((byte)9, r.Book.Stored(CollRig.Self)!.ActivePetFlag);                           // the read re-derives in memory, it does not write

        r.Book.Seed(Book(d => { d.RegisteredPetIds.Add(Pet); d.ActivePetId = Pet; d.ActivePetFlag = 1; d.SetLoyalty(Pet, 3); }));    // a stale 1 over loyalty 3
        AssertFrames(await r.CallAsync(r.Handlers.Pet_1628, []), Active2918(Pet, 0, 0));
    }

    // ------------------------------------------------------------------------------------------------ 1625 select

    [Fact]
    public async Task Selecting_a_pet_stores_the_selection_does_not_register_and_answers_2917_at_the_stored_loyalty_with_the_bag_piece_counts()
    {
        await using var r = await CollRig.StartAsync();
        r.Give(PetPieceA, 5, 0);
        r.Give(PetPieceB, 2, 1);
        r.Book.Seed(Book(d => { d.RegisteredPetIds.Add(Pet); d.SetLoyalty(Pet, 25); }));

        var frames = await r.CallAsync(r.Handlers.Pet_1625, CollRig.Id(Pet));

        AssertFrames(frames, (ZoneS2COpcodes.AnsPCollectionSelectedSkill, CollectionPackets.BuildAnsPCollectionSelectedSkill(
            new PetCollectionState(Pet, PetEffect1, 1, 25), new PieceCounts(5, 2, 0, 0), 0)));
        Assert.Equal(new uint[] { Pet }, r.Book.Stored(CollRig.Self)!.SelectedPetSkills);

        await r.CallAsync(r.Handlers.Pet_1625, CollRig.Id(Pet));                                     // selecting again stores no second entry
        Assert.Equal(new uint[] { Pet }, r.Book.Stored(CollRig.Self)!.SelectedPetSkills);
    }

    [Fact]
    public async Task QUIRK_selecting_an_unregistered_pet_answers_its_loyalty_0_record_and_an_unknown_id_is_stored_with_a_bare_id_record()
    {
        await using var r = await CollRig.StartAsync();

        AssertFrames(await r.CallAsync(r.Handlers.Pet_1625, CollRig.Id(Pet)), (ZoneS2COpcodes.AnsPCollectionSelectedSkill,
            CollectionPackets.BuildAnsPCollectionSelectedSkill(new PetCollectionState(Pet, PetEffect0, 0, 0), default, 0)));
        Assert.Empty(r.Book.Stored(CollRig.Self)!.RegisteredPetIds);

        AssertFrames(await r.CallAsync(r.Handlers.Pet_1625, CollRig.Id(PetUnknown)), (ZoneS2COpcodes.AnsPCollectionSelectedSkill,
            CollectionPackets.BuildAnsPCollectionSelectedSkill(new PetCollectionState(PetUnknown, 0, 0, 0), default, 0)));
        Assert.Equal(new uint[] { Pet, PetUnknown }, r.Book.Stored(CollRig.Self)!.SelectedPetSkills);   // no table check on select

        AssertFrames(await r.CallAsync(r.Handlers.Pet_1625, new byte[1]), (ZoneS2COpcodes.AnsPCollectionSelectedSkill,
            CollectionPackets.BuildAnsPCollectionSelectedSkill(new PetCollectionState(0, 0, 0, 0), default, 0)));
        Assert.Equal(2, r.Book.Stored(CollRig.Self)!.SelectedPetSkills.Count);                          // id 0 is not stored
    }

    // ------------------------------------------------------------------------------------------------ 1624 open the book

    [Fact]
    public async Task Opening_the_pet_book_sends_2916_ascending_with_stored_loyalties_then_the_incubator_list_2926()
    {
        await using var r = await CollRig.StartAsync();
        r.Book.Seed(Book(d => { d.RegisteredPetIds.AddRange([PetBig, Pet]); d.SetLoyalty(Pet, 25); }));
        var start = DateTime.UtcNow;
        r.Incubators.Seed(CollRig.Self, new PetIncubatorSlot { Slot = 0, IncubatorId = Incubator, Charges = 2, StartUtc = start, EndUtc = start });

        var frames = await r.CallAsync(r.Handlers.Pet_1624, []);

        Assert.Equal(new ushort[] { ZoneS2COpcodes.AnsPCollection, ZoneS2COpcodes.AnsPCollectionIncubator }, frames.Select(f => f.Op).ToArray());
        Assert.Equal(Hex(CollectionPackets.BuildAnsPCollection([new PetCollectionState(Pet, PetEffect1, 1, 25), new PetCollectionState(PetBig, 11, 0, 0)])), Hex(frames[0].Body));
        Assert.Equal(new[] { (Incubator, 0u, (byte)0, (byte)2) }, IncubatorList.Parse(frames[1].Body));
    }

    [Fact]
    public async Task Opening_the_pet_book_with_the_incubator_store_down_sends_the_EMPTY_incubator_list_not_an_error()
    {
        await using var r = await CollRig.StartAsync();
        r.Incubators.ThrowOnGet = true;

        var frames = await r.CallAsync(r.Handlers.Pet_1624, []);

        Assert.Equal(new ushort[] { ZoneS2COpcodes.AnsPCollection, ZoneS2COpcodes.AnsPCollectionIncubator }, frames.Select(f => f.Op).ToArray());
        Assert.Equal(Hex(PetIncubatorPackets.BuildAnsPCollectionIncubator([], DateTime.UtcNow)), Hex(frames[1].Body));
        Assert.Equal("01000000", Hex(frames[1].Body));
    }

    // ------------------------------------------------------------------------------------------------ 1626 combine egg blank

    private static async Task<CollRig> EggRigAsync(int catalyst = 3, int pieceA = 9, int pieceB = 3)
    {
        var r = await CollRig.StartAsync();
        r.Give(Catalyst, catalyst, 0);      // flat 0 = the cell the request names
        r.Give(PetPieceA, pieceA, 1);
        r.Give(PetPieceB, pieceB, 2);
        return r;
    }

    private static (uint Item, int Count, short Slot)[] Bag(CollRig r)
        => r.Fx.Inventory.Rows.Where(x => x.StorageType == 0).OrderBy(x => x.Slot).Select(x => (x.ItemId, x.Count, x.Slot)).ToArray();

    [Fact]
    public async Task An_egg_combine_takes_the_catalyst_and_pieces_places_the_egg_and_replies_2329_2333_2921_2924_without_registering_the_pet()
    {
        await using var r = await EggRigAsync();
        var catalyst = r.Cellrow(0)!;

        // times 2: catalyst 2 x Num_Catalyst(1) = 2; piece A 2 x 2 = 4; piece B 2 x 1 = 2
        var frames = await r.CallAsync(r.Handlers.Pet_1626, CollRig.Cell(CellOf(0), 2, Pet));

        Assert.Equal(new[] { (Catalyst, 1, (short)0), (PetPieceA, 5, (short)1), (PetPieceB, 1, (short)2), (PetEgg, 2, (short)3) }, Bag(r));
        AssertFrames(frames,
            r.Use2329(catalyst, 0, 1),
            (ZoneS2COpcodes.OnPickupItem, LootNotifyPackets.BuildPickupItem2333(ItemPackets.BuildItemRecord126(r.Cellrow(3)!), resultCode: 0, pickupKind: 0)),
            (ZoneS2COpcodes.NfyPCollectionSelectedSkill, CollectionPackets.BuildNfyPCollectionSelectedSkill(
                new PetCollectionState(Pet, PetEffect0, 0, 0), new PieceCounts(5, 1, 0, 0))),
            (ZoneS2COpcodes.AnsPCollectionCombinEggBlank, CollectionPackets.BuildCollectionIdResultAck(Pet, 0)));
        Assert.Null(r.Book.Stored(CollRig.Self));                  // the pet is NOT registered here: the egg still goes through the incubator and 1627
    }

    [Fact]
    public async Task An_egg_combine_tops_up_an_existing_egg_stack_and_the_2921_carries_the_registered_pets_loyalty()
    {
        await using var r = await CollRig.StartAsync();
        var catalyst = r.Give(Catalyst, 1, 0);
        r.Give(PetPieceA, 2, 1);
        r.Give(PetPieceB, 1, 2);
        r.Give(PetEgg, 7, 3);
        r.Book.Seed(Book(d => { d.RegisteredPetIds.Add(Pet); d.SetLoyalty(Pet, 25); }));

        var frames = await r.CallAsync(r.Handlers.Pet_1626, CollRig.Cell(CellOf(0), 1, Pet));

        Assert.Equal(new[] { (PetEgg, 8, (short)3) }, Bag(r));       // catalyst, A and B all spent to 0 and removed; the egg stack topped up
        AssertFrames(frames,
            r.Use2329(catalyst, 0, 0),
            (ZoneS2COpcodes.OnPickupItem, LootNotifyPackets.BuildPickupItem2333(ItemPackets.BuildItemRecord126(r.Cellrow(3)!), resultCode: 0, pickupKind: 0)),
            (ZoneS2COpcodes.NfyPCollectionSelectedSkill, CollectionPackets.BuildNfyPCollectionSelectedSkill(
                new PetCollectionState(Pet, PetEffect1, 1, 25), default)),
            (ZoneS2COpcodes.AnsPCollectionCombinEggBlank, CollectionPackets.BuildCollectionIdResultAck(Pet, 0)));
    }

    [Fact]
    public async Task Every_1626_refusal_is_silent_and_takes_nothing()
    {
        await using var r = await EggRigAsync(catalyst: 1);          // 1 catalyst: enough for 1 combine, not for 2
        var before = Bag(r);

        Assert.Empty(await r.CallAsync(r.Handlers.Pet_1626, new byte[6]));                                    // short body
        Assert.Empty(await r.CallAsync(r.Handlers.Pet_1626, CollRig.Cell(CellOf(0), 1, PetUnknown)));           // no PetInfo row
        Assert.Empty(await r.CallAsync(r.Handlers.Pet_1626, CollRig.Cell(CellOf(0), 0, Pet)));                  // times 0
        Assert.Empty(await r.CallAsync(r.Handlers.Pet_1626, CollRig.Cell(CellOf(0), 1, PetNoRecipe)));          // Pet_Egg_ID 0 / no catalyst: no usable recipe
        Assert.Empty(await r.CallAsync(r.Handlers.Pet_1626, CollRig.Cell(CellOf(0), 1, PetBig)));               // no catalyst item
        Assert.Empty(await r.CallAsync(r.Handlers.Pet_1626, CollRig.Cell(CellOf(5), 1, Pet)));                  // the named cell is empty
        Assert.Empty(await r.CallAsync(r.Handlers.Pet_1626, CollRig.Cell(CellOf(1), 1, Pet)));                  // the named cell holds the wrong item
        Assert.Empty(await r.CallAsync(r.Handlers.Pet_1626, CollRig.Cell(CellOf(0), 2, Pet)));                  // catalyst 1 < 2
        Assert.Empty(await r.CallAsync(r.Handlers.Pet_1626, CollRig.Cell(70, 1, Pet)));                         // not a bag cell
        Assert.Equal(before, Bag(r));
    }

    [Fact]
    public async Task A_1626_short_of_a_piece_or_of_a_free_cell_is_silent()
    {
        await using var r = await EggRigAsync(pieceA: 3);            // 2 combines need 4 A, 3 held
        var before = Bag(r);
        Assert.Empty(await r.CallAsync(r.Handlers.Pet_1626, CollRig.Cell(CellOf(0), 2, Pet)));
        Assert.Equal(before, Bag(r));

        await using var full = await CollRig.StartAsync();           // PetEgg stacks to 99, so one free cell is all a combine needs
        full.Give(Catalyst, 3, 0);
        full.Give(PetPieceA, 9, 1);
        full.Give(PetPieceB, 9, 2);
        for (var flat = 3; flat < 20; flat++) full.Give(Filler, 1, flat);       // all 20 cells occupied
        var beforeFull = Bag(full);
        Assert.Empty(await full.CallAsync(full.Handlers.Pet_1626, CollRig.Cell(CellOf(0), 1, Pet)));
        Assert.Equal(beforeFull, Bag(full));
    }
}

/// <summary>Reads the fields of a 2926 body: <c>[compact length][58-byte records][u16 result]</c>; times are not compared.</summary>
internal static class IncubatorList
{
    public static (uint Incubator, uint Egg, byte Slot, byte Charges)[] Parse(byte[] body)
    {
        Assert.Equal(1, body[0]);                                    // the compact length prefix of a count under 128 is 1 byte of length + the count
        int n = body[1];
        var list = new (uint, uint, byte, byte)[n];
        for (var i = 0; i < n; i++)
        {
            var at = 2 + PetIncubatorPackets.RecordLength * i;
            list[i] = (BitConverter.ToUInt32(body, at), BitConverter.ToUInt32(body, at + 4), body[at + 8], body[at + 9]);
        }
        Assert.Equal(2 + PetIncubatorPackets.RecordLength * n + 2, body.Length);
        Assert.Equal(0, BitConverter.ToUInt16(body, body.Length - 2));       // the trailing result is always 0
        return list;
    }
}
