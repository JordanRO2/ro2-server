using RO2.Domain.Entities;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// RMI 2587 AnsCardBookList (<see cref="CardPackets.BuildCardBookList2587"/>) and the card registry that feeds
/// it (<see cref="CardRegistryStore.Merge"/>). The two byte-exact checks use reference frames. Grade values
/// come from the Grade_Type field of data/CardInfo.json.
/// </summary>
public class CardBookList2587Tests
{
    [Fact]
    public void RegistryOnlyBook_ReproducesTheReferenceFrame()
    {
        
        
        
        var expected = Convert.FromHexString(
            "d601000011c3920b3a009a6c5401010000009a6c5401010000019b6c5401010000009b6c5401010000019d6c5401010000009d6c540101000001a16c540101000000a16c540101000001ec6e540101000000ec6e540101000001ed6e540101000000ed6e540101000001ee6e540101000000ee6e540101000001ef6e540101000000ef6e540101000001f06e540101000000f06e540101000001f16e540101000000f16e540101000001f26e540101000000f26e540101000001f36e540101000000f36e540101000001f46e540101000000f46e540101000001f56e540101000000f56e540101000001f66e540101000000f66e540101000001f76e540101000000f76e5401010000016b6d5401010000006b6d5401010000016d6d5401010000006d6d540101000001986d540101000000986d540101000001086f540101000000086f540101000001096f540101000000096f5401010000010a6f5401010000000a6f5401010000010b6f5401010000000b6f5401010000010c6f5401010000000c6f5401010000010d6f5401010000000d6f540101000001886f540101000000886f540101000001896f540101000000896f5401010000018a6f5401010000008a6f540101000001b06f540102000000b06f540102000001");
        var registry = Registry(
            (22310832u, 2), (22310794u, 1), (22310793u, 1), (22310792u, 1), (22310669u, 1), (22310668u, 1),
            (22310667u, 1), (22310666u, 1), (22310665u, 1), (22310664u, 1), (22310647u, 1), (22310646u, 1),
            (22310645u, 1), (22310644u, 1), (22310643u, 1), (22310642u, 1), (22310641u, 1), (22310640u, 1),
            (22310639u, 1), (22310638u, 1), (22310637u, 1), (22310636u, 1), (22310296u, 1), (22310253u, 1),
            (22310251u, 1), (22310049u, 1), (22310045u, 1), (22310043u, 1), (22310042u, 1));
        var grades = new Dictionary<uint, byte>
        {
            [22310042u] = 2, [22310043u] = 2, [22310045u] = 2, [22310049u] = 2, [22310251u] = 3, [22310253u] = 3,
            [22310296u] = 3, [22310636u] = 2, [22310637u] = 2, [22310638u] = 2, [22310639u] = 2, [22310640u] = 2,
            [22310641u] = 2, [22310642u] = 2, [22310643u] = 2, [22310644u] = 2, [22310645u] = 2, [22310646u] = 2,
            [22310647u] = 2, [22310664u] = 3, [22310665u] = 3, [22310666u] = 3, [22310667u] = 3, [22310668u] = 3,
            [22310669u] = 3, [22310792u] = 3, [22310793u] = 3, [22310794u] = 3, [22310832u] = 4,
        };

        var body = CardPackets.BuildCardBookList2587(0x0B92C311, registry, Array.Empty<CardBookEntry>(), c => grades[c]);

        Assert.Equal(expected, body);
    }

    [Fact]
    public void BookWithCopies_ReproducesTheArchiveFrame()
    {
        
        
        var expected = Convert.FromHexString(
            "a6000000114c44071400716c540101000000716c540101000001746c540101010000746c5401010000017a6c5401010000007a6c5401010000017e6c5401010100007e6c5401010000018e6c5401020000008e6c5401020000018f6c5401020100008f6c5401020000019b6c5401020100009b6c540102000001a06c540102010000a06c540102000001a56c540102010000a56c5401020000019b6e5401020300009b6e540102000001");
        var registry = Registry(
            (22310555u, 2), (22310053u, 2), (22310048u, 2), (22310043u, 2), (22310031u, 2), (22310030u, 2),
            (22310014u, 1), (22310010u, 1), (22310004u, 1), (22310001u, 1));
        var copies = new List<CardBookEntry>();
        foreach (var id in new[] { 22310004u, 22310014u, 22310031u, 22310043u, 22310048u, 22310053u, 22310555u, 22310555u, 22310555u })
            copies.Add(new CardBookEntry { CardId = id, Grade = 2, EnforceGrade = 0 });

        var body = CardPackets.BuildCardBookList2587(0x07444C11, registry, copies, _ => 2);

        Assert.Equal(expected, body);
    }

    [Fact]
    public void EmptyBook_IsTheTenByteModalBody()
    {
        var body = CardPackets.BuildCardBookList2587(0x0B92C311, Array.Empty<CardRegistryEntry>(), Array.Empty<CardBookEntry>(), _ => 0);
        Assert.Equal(Convert.FromHexString("0600000011c3920b0000"), body);
    }

    [Fact]
    public void EnforcedCopies_FollowThePlusZeroPairInGradeOrder()
    {
        
        
        var copies = new List<CardBookEntry>
        {
            new() { CardId = 22310833u, Grade = 4, EnforceGrade = 3 },
            new() { CardId = 22310833u, Grade = 4, EnforceGrade = 1 },
        };

        var body = CardPackets.BuildCardBookList2587(1, Array.Empty<CardRegistryEntry>(), copies, _ => 4);

        Assert.Equal(Convert.FromHexString(
            "2600000001000000" + "0400" +
            "b16f540101000000" + "b16f540101000001" + "b16f540101010100" + "b16f540101010300"), body);
    }

    [Fact]
    public void KindOneCopies_AreListedApartFromKindZero()
    {
        
        
        var copies = new List<CardBookEntry>();
        for (int i = 0; i < 3; i++) copies.Add(new() { CardId = 22310821u, EnforceGrade = 0, Kind = 0 });
        for (int i = 0; i < 5; i++) copies.Add(new() { CardId = 22310821u, EnforceGrade = 0, Kind = 1 });
        copies.Add(new() { CardId = 22310839u, EnforceGrade = 1, Kind = 1 });
        copies.Add(new() { CardId = 22310839u, EnforceGrade = 0, Kind = 1 });
        var registry = Registry((22310821u, 1), (22310839u, 1));

        var body = CardPackets.BuildCardBookList2587(0x0787A311, registry, copies, _ => 4);

        Assert.Equal(Convert.FromHexString(
            "2e00000011a387070500" +
            "a56f540101030000" + "a56f540101050001" +
            "b76f540101000000" + "b76f540101010001" + "b76f540101010101"), body);
    }

    [Fact]
    public void Merge_BookCardsAreSeen_NewHeldCardsAreNew_NothingIsDropped()
    {
        var registry = new List<CardRegistryEntry>
        {
            new() { CardId = 22310001u, State = CardRegistryStore.StateSeen },   
            new() { CardId = 22310002u, State = CardRegistryStore.StateNew },    
            new() { CardId = 22310003u, State = CardRegistryStore.StateNew },    
        };

        bool changed = CardRegistryStore.Merge(registry,
            cardsInBook: new[] { 22310002u, 22310004u },
            cardsHeld: new[] { 22310003u, 22310005u, 22310004u });

        Assert.True(changed);
        Assert.Equal(new (uint, byte)[]
        {
            (22310001u, CardRegistryStore.StateSeen),
            (22310002u, CardRegistryStore.StateSeen),
            (22310003u, CardRegistryStore.StateNew),
            (22310004u, CardRegistryStore.StateSeen),
            (22310005u, CardRegistryStore.StateNew),
        }, registry.Select(e => (e.CardId, e.State)).ToArray());

        Assert.False(CardRegistryStore.Merge(registry, new[] { 22310002u }, new[] { 22310005u }));
    }

    private static List<CardRegistryEntry> Registry(params (uint CardId, byte State)[] entries)
        => entries.Select(e => new CardRegistryEntry { CardId = e.CardId, State = e.State }).ToList();
}
