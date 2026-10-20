namespace RO2.Server.Zone.Services;

/// <summary>
/// Guild experience notifications: 2725 (donated share of a quest prize's EXP), 2726 (a member's level-up
/// credit) and 2728 (guild level-up). The 2725 and 2726 bodies are a u64 amount; 2728 is a u8 guild level.
/// </summary>
/// <remarks>
/// The level-up credit is <c>floor(Base_Exp_Next / 100)</c> of the level just completed. The donated amount
/// is <c>floor(prizeExp × percent / 100)</c>.
/// </remarks>
public static class GuildBribePackets
{
    public const int BodyLen = 8;

 /// <summary>
 /// The 2726 amount for one level-up: <paramref name="expToCompleteLevel"/> (the <c>Base_Exp_Next</c> of the
 /// level just completed) divided by 100, rounded down.
 /// </summary>
    public static ulong LevelUpBribeAmount(ulong expToCompleteLevel) => expToCompleteLevel / 100UL;

 /// <summary>Builds the 2726 body: <c>[u64 amount]</c>, little-endian.</summary>
    public static byte[] BuildNfyGuildBribePCLevelUp(ulong amount)
    {
        var body = new byte[BodyLen];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(body, amount);
        return body;
    }

 /// <summary>
 /// The 2725 amount: <c>floor(prizeExp × percent / 100)</c>, where <paramref name="prizeExp"/> is the quest
 /// prize's EXP and <paramref name="percent"/> the member's donation rate.
 /// </summary>
    public static ulong HuntingBribeAmount(ulong prizeExp, byte percent) => prizeExp * percent / 100UL;

 /// <summary>Builds the 2725 body: <c>[u64 amount]</c>, little-endian.</summary>
    public static byte[] BuildNfyGuildBribeHunting(ulong amount)
    {
        var body = new byte[BodyLen];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(body, amount);
        return body;
    }

 /// <summary>Builds the 2728 body: <c>[u8 new guild level]</c>.</summary>
    public static byte[] BuildNfyGuildLevelUp(byte newLevel) => new[] { newLevel };
}
