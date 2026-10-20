using EffectRow = RO2.Domain.GameData.Tables.EffectTable;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The per-tick HP and SP change and the tick period of a periodic effect, read from its Effect_Table rows.
/// </summary>
/// <remarks>
/// <para>
/// An effect ticks when it has a period row and at least one amount row:
/// </para>
/// <list type="bullet">
/// <item><description>Period: <c>Task 0</c> with <c>Task_Name 4</c>. <c>Basic_Add_Value</c> is the period in seconds.</description></item>
/// <item><description>HP amount: <c>Task 1</c> with <c>Task_Name 3</c>. SP amount: <c>Task 1</c> with <c>Task_Name 4</c>. <c>Task_Type</c> is the unit: 1 is an absolute amount, 2 is 1/10,000 of the target's max HP or SP. The value is <c>Basic_Add_Value + Level_Multiply_Value × effectValue</c>, times the stack count.</description></item>
/// </list>
/// <para>
/// Amounts are truncated toward zero. They are computed once, when the effect is applied, from the maxima the caller
/// passes, and are kept for the life of the effect.
/// </para>
/// <para>
/// Rows that need the caster's computed effect value are not filled: such a row has a zero base value and a level
/// multiplier of 1 or -1. Rows of other units and rows of a <c>Trait_Kind</c> other than 3 or 4 are also left out. An
/// effect with no usable amount row, or with no period row, does not tick.
/// </para>
/// </remarks>
/// <param name="HpPerTick">Signed HP change per tick: positive heals, negative damages.</param>
/// <param name="SpPerTick">Signed SP change per tick: positive gains, negative drains.</param>
/// <param name="IntervalMs">Tick period in milliseconds.</param>
public readonly record struct EffectTickPlan(int HpPerTick, int SpPerTick, int IntervalMs)
{
 /// <summary>Effect_Table <c>Task</c> of the period row.</summary>
    internal const byte PeriodTask = 0;

 /// <summary>Effect_Table <c>Task_Name</c> of the period row, as <see cref="PeriodTask"/>.</summary>
    internal const byte PeriodTaskName = 4;

 /// <summary>Effect_Table <c>Task</c> of an amount row.</summary>
    internal const byte AmountTask = 1;

 /// <summary>Effect_Table <c>Task_Name</c> of the HP amount row.</summary>
    internal const byte HpTaskName = 3;

 /// <summary>Effect_Table <c>Task_Name</c> of the SP amount row.</summary>
    internal const byte SpTaskName = 4;

 /// <summary>Amount row <c>Task_Type</c> meaning an absolute amount.</summary>
    internal const byte AbsoluteUnit = 1;

 /// <summary>Amount row <c>Task_Type</c> meaning a fraction of the target's max HP or SP.</summary>
    internal const byte PercentOfMaxUnit = 2;

 /// <summary>The unit of a <see cref="PercentOfMaxUnit"/> value: 10,000 is the whole max.</summary>
    private const double PercentOfMaxScale = 10000.0;

 /// <summary>
 /// The tick plan of effect <paramref name="effectId"/> applied with <paramref name="effectValue"/> and
 /// <paramref name="stackCount"/> to a target with the given <paramref name="maxHp"/> and <paramref name="maxSp"/>.
 /// Returns <c>null</c> when the effect does not tick from data.
 /// </summary>
    public static EffectTickPlan? For(IReadOnlyList<EffectRow> effectTable, uint effectId, ushort effectValue,
        byte stackCount, int maxHp, int maxSp)
    {
        double periodSeconds = 0;
        double hp = 0, sp = 0;
        bool anyAmount = false;

        foreach (var row in effectTable)
        {
            if (row.ID != effectId)
                continue;

            if (row.Task == PeriodTask && row.Task_Name == PeriodTaskName)
            {
                periodSeconds = row.Basic_Add_Value;
                continue;
            }

            if (row.Task != AmountTask || row.Task_Name is not (HpTaskName or SpTaskName))
                continue;
            if (row.Trait_Kind is not (3 or 4))
                continue;
            if (row.Task_Type is not (AbsoluteUnit or PercentOfMaxUnit))
                continue;
            if (IsCasterComputed(row))
                continue;

            double value = (row.Basic_Add_Value + row.Level_Multiply_Value * (double)effectValue)
                * Math.Max((byte)1, stackCount);
            if (row.Task_Type == PercentOfMaxUnit)
                value = value * (row.Task_Name == HpTaskName ? maxHp : maxSp) / PercentOfMaxScale;

            if (row.Task_Name == HpTaskName) hp += value;
            else sp += value;
            anyAmount = true;
        }

        if (!anyAmount || periodSeconds <= 0)
            return null;

        var plan = new EffectTickPlan(
            HpPerTick: TruncateToInt(hp),
            SpPerTick: TruncateToInt(sp),
            IntervalMs: (int)Math.Round(periodSeconds * 1000.0, MidpointRounding.AwayFromZero));
        return plan.HpPerTick == 0 && plan.SpPerTick == 0 || plan.IntervalMs <= 0 ? null : plan;
    }

 /// <summary>
 /// An amount row whose magnitude is the caster's computed effect value: a zero base value with a level multiplier of
 /// 1 (a heal) or -1 (damage).
 /// </summary>
    internal static bool IsCasterComputed(EffectRow row)
        => row.Basic_Add_Value == 0f && Math.Abs(row.Level_Multiply_Value) == 1f;

 /// <summary>Truncates toward zero, saturated to an <c>int</c>.</summary>
    private static int TruncateToInt(double value)
        => (int)Math.Clamp(Math.Truncate(value), int.MinValue, int.MaxValue);
}
