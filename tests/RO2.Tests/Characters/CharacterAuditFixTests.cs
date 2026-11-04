using System.Reflection;
using RO2.Characters.Application;
using RO2.Characters.Domain;
using RO2.GameData;
using Xunit;

namespace RO2.Tests.Characters;

/// <summary>Regression pins for the 2026-11-03 Character audit fixes ([10] job exp, [11] name regex, [32] allow-review).</summary>
public class CharacterAuditFixTests
{
    // ---- [11] ----

    [Theory]
    [InlineData("Abc\n")]
    [InlineData("Abc\r\n")]
    [InlineData("\nAbc")]
    public void Name_WithNewline_IsAFormatRefusal(string name)
        => Assert.Equal(NameRefusal.Format, NameRules.Check(name, []).refusal);

    [Fact]
    public void Name_Plain_IsAccepted() => Assert.Equal(NameRefusal.None, NameRules.Check("Abc_def1", []).refusal);

}
