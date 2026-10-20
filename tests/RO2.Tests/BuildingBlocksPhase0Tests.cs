using RO2.BuildingBlocks.Domain;
using RO2.BuildingBlocks.Domain.ValueObjects;
using Xunit;

namespace RO2.Tests;

/// <summary>Smoke tests for the shared domain primitives: value-object invariants and typed id round trips.</summary>
public sealed class BuildingBlocksPhase0Tests
{
    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Level_Rejects_OutOfRange(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Level(value));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    public void Level_Accepts_InRange(int value)
    {
        var level = new Level(value);
        Assert.Equal(value, level.Value);
    }

    [Fact]
    public void Vital_Rejects_CurrentGreaterThanMax()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Vital(current: 11, max: 10));
    }

    [Fact]
    public void Vital_Accepts_CurrentWithinMax()
    {
        var vital = new Vital(current: 7, max: 10);
        Assert.Equal(7, vital.Current);
        Assert.Equal(10, vital.Max);
    }

    [Fact]
    public void Gold_Rejects_Negative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Gold(-1L));
    }

    [Fact]
    public void ValueObject_StructuralEquality_Positive()
    {
        var a = new Level(42);
        var b = new Level(42);

        Assert.True(a.Equals(b));
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        var c = new Level(20);
        Assert.False(a.Equals(c));
        Assert.True(a != c);
    }

    [Fact]
    public void CharacterId_RoundTrips_Through_RawValue()
    {
        const uint raw = 2733u;

        var id = CharacterId.From(raw);

        Assert.Equal(raw, id.Value);
        Assert.Equal(raw, (uint)id);
        Assert.Equal(id, (CharacterId)raw);
    }
}
