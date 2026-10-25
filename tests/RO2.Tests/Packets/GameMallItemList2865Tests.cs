using RO2.Economy.Infrastructure.Mongo;
using RO2.Persistence.Mongo.Repositories;
using RO2.Network.Packets;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte layout and ordering of 2865 AnsGameMallItemList (answer to 1593), and of the account list 2 that
/// 1597 ReqGameMallUpdate adds to (<see cref="GameMallListRepository"/>).
/// </summary>
public class GameMallItemList2865Tests
{
    
    
    
    private const uint FirstAdded = 70004013;  
    private const uint SecondAdded = 70004010; 
    private const string RetailAfterFirstHex = "02000001012d2d2c04";
    private const string RetailAfterSecondHex = "02000001022a2d2c042d2d2c04";

    private static byte[] Build(byte list, IReadOnlyList<uint> ids)
    {
        using var w = new PacketWriter();
        ReqGameMallItemListHandler.WriteItemList2865(w, list, ids);
        return w.GetWrittenSpan().ToArray();
    }

    [Fact]
    public void EmptyListsAreRetailsFiveByteForms()
    {
        Assert.Equal(Convert.FromHexString("0100000100"), Build(1, Array.Empty<uint>()));
        Assert.Equal(Convert.FromHexString("0200000100"), Build(2, Array.Empty<uint>()));
    }

    [Fact]
    public void RetailAddSequenceIsReproducedByteForByte()
    {
        var afterFirst = GameMallListRepository.Prepend(Array.Empty<uint>(), new[] { FirstAdded });
        Assert.Equal(Convert.FromHexString(RetailAfterFirstHex), Build(GameMallListStore.AccountList, afterFirst));

        
        var afterSecond = GameMallListRepository.Prepend(afterFirst, new[] { SecondAdded });
        Assert.Equal(Convert.FromHexString(RetailAfterSecondHex), Build(GameMallListStore.AccountList, afterSecond));
    }

    [Fact]
    public void KnownIdIsNotAddedTwice()
    {
        
        var list = GameMallListRepository.Prepend(new[] { SecondAdded, FirstAdded }, new[] { FirstAdded });

        Assert.Equal(new[] { SecondAdded, FirstAdded }, list);
    }

    [Fact]
    public void SeveralIdsAreAddedInTurn()
    {
        var list = GameMallListRepository.Prepend(new[] { 1u }, new[] { 2u, 3u, 2u });

        Assert.Equal(new[] { 3u, 2u, 1u }, list);
    }
}
