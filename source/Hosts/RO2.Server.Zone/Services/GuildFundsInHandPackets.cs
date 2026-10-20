using System.Buffers.Binary;
using System.Globalization;
using RO2.Domain.GameData.Tables;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Guild vault zeny ("funds in hand", <see cref="Guild.Gold"/>): deposit and withdraw requests and answers,
/// the funds notification, and the limits that apply to each move.
/// </summary>
/// <remarks>
/// Deposit (1487) and withdraw (1488) requests are <c>[16-byte guild GUID][u64 amount]</c>, 24 bytes with no
/// envelope. Answers 2672 (deposit) and 2673 (withdraw) are <c>[u16 result][u64 wallet after the move]</c>;
/// the wallet is 0 on a refusal. The funds notification 2677 is <c>[u64 funds after the move]</c> and refreshes
/// an open vault window.
/// </remarks>
internal static class GuildFundsInHandPackets
{
 /// <summary>Body length of a 1487 / 1488 request: 16-byte GUID followed by a u64 amount.</summary>
    internal const int RequestLength = 24;

    private const int GuidLength = 16;

 /// <summary>Success.</summary>
    internal const ushort ResultOk = 0;

 /// <summary>GUILD_ERROR 6 ("System Error"). Returned when a write fails and the move is rolled back.</summary>
    internal const ushort ResultSystemError = 6;

 /// <summary>GUILD_ERROR 7 ("Cannot Find Guild"): no guild, a disbanded guild, or a GUID that is not the requester's guild.</summary>
    internal const ushort ResultGuildNotFound = 7;

 /// <summary>Code 33: the deposit would exceed the guild storage limit (see <see cref="StorageLimit"/>).</summary>
    internal const ushort ResultStorageLimit = 33;

 /// <summary>Code 34: the guild holds less than the requested amount.</summary>
    internal const ushort ResultGuildFundsShort = 34;

 /// <summary>Code 39: the move would take the wallet above the per-character maximum (<see cref="ConfigMaxWalletZeny"/>).</summary>
    internal const ushort ResultWalletLimit = 39;

 /// <summary>Code 46: the depositor holds less zeny than the requested amount.</summary>
    internal const ushort ResultWalletShort = 46;

 /// <summary>Config_Def type 91: the most zeny a character may hold.</summary>
    internal const ushort ConfigMaxWalletZeny = 91;

 /// <summary>The Guild_Skill_Level category whose <c>Skill_Value</c> sets the storage limit.</summary>
    internal const byte SkillCategoryStorageLimit = 4;

 /// <summary>
 /// Reads the amount of a 1487 / 1488 request and whether its GUID matches <paramref name="g"/>. Returns false
 /// for a body shorter than <see cref="RequestLength"/>.
 /// </summary>
    internal static bool TryParseRequest(ReadOnlySpan<byte> body, Guild? g, out ulong amount, out bool guidMatches)
    {
        amount = 0;
        guidMatches = false;
        if (body.Length < RequestLength) return false;
        amount = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(GuidLength, 8));
        if (g != null)
            lock (g.Gate) guidMatches = g.Guid16 != null && body[..GuidLength].SequenceEqual(g.Guid16);
        return true;
    }

 /// <summary>
 /// The guild's storage limit: the largest <c>Skill_Value</c> of a learned skill in the storage-limit category,
 /// or 0 when no such skill is learned.
 /// </summary>
    internal static long StorageLimit(IEnumerable<uint> learned, IReadOnlyDictionary<uint, GuildSkillLevel> table)
    {
        long limit = 0;
        foreach (var id in learned)
        {
            if (!table.TryGetValue(id, out var row)) continue;
            if (row.Skill_Category_1 == SkillCategoryStorageLimit) limit = Math.Max(limit, row.Skill_Value_1);
            if (row.Skill_Category_2 == SkillCategoryStorageLimit) limit = Math.Max(limit, row.Skill_Value_2);
            if (row.Skill_Category_3 == SkillCategoryStorageLimit) limit = Math.Max(limit, row.Skill_Value_3);
        }
        return limit;
    }

 /// <summary>
 /// The most zeny a character may hold (Config_Def type 91). No cap when the row is absent or unparseable.
 /// </summary>
    internal static long WalletCap(IReadOnlyList<ConfigDef> config)
    {
        var row = config.FirstOrDefault(c => c.type == ConfigMaxWalletZeny);
        return row != null && long.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cap) && cap > 0
            ? cap : long.MaxValue;
    }

 /// <summary>
 /// The wallet check of a deposit: <see cref="ResultWalletShort"/> when the depositor holds less than
 /// <paramref name="amount"/>, otherwise <see cref="ResultOk"/>.
 /// </summary>
    internal static ushort DepositWalletRefusal(long wallet, ulong amount)
        => wallet < 0 || amount > (ulong)wallet ? ResultWalletShort : ResultOk;

 /// <summary>
 /// The wallet check of a withdrawal: <see cref="ResultWalletLimit"/> when the resulting wallet would exceed
 /// <paramref name="walletCap"/>, otherwise <see cref="ResultOk"/>. Checked before the guild funds.
 /// </summary>
    internal static ushort WithdrawWalletRefusal(long wallet, ulong amount, long walletCap)
        => wallet > walletCap || amount > (ulong)(walletCap - Math.Max(0, wallet)) ? ResultWalletLimit : ResultOk;

 /// <summary>Builds the 2672 deposit answer: <c>[u16 result][u64 wallet after]</c>. The wallet is 0 on a refusal.</summary>
    internal static byte[] BuildAnsDeposit2672(ushort result, long walletAfter) => BuildAnswer(result, walletAfter);

 /// <summary>Builds the 2673 withdraw answer: <c>[u16 result][u64 wallet after]</c>, 0 on a refusal.</summary>
    internal static byte[] BuildAnsWithdraw2673(ushort result, long walletAfter) => BuildAnswer(result, walletAfter);

 /// <summary>Builds the 2677 funds notification: <c>[u64 funds after the move]</c>, never negative.</summary>
    internal static byte[] BuildNfyFundsInHand2677(long fundsAfter)
    {
        var body = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(body, (ulong)Math.Max(0, fundsAfter));
        return body;
    }

    private static byte[] BuildAnswer(ushort result, long walletAfter)
    {
        var body = new byte[10];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(2), result == ResultOk ? (ulong)Math.Max(0, walletAfter) : 0UL);
        return body;
    }
}
