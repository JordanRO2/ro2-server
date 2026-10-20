using System.Collections.Concurrent;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Minimal, in-memory SIEGE state: tracks the siege-arms (the emplaced weapons players operate) — their
/// yaw, current operator and running arm-skill cooldowns (1641 refuses a shot inside one with 2943
/// result 20032, and 1637's 2937 lists them to the player who takes the seat). Implemented as a STATIC
/// singleton on purpose: the class is not registered in the DI container (Program.cs is intentionally
/// left untouched) and no existing service constructor is modified, so there is zero DI-cycle risk.
/// </summary>
public static class SiegeService
{
 /// <summary>A tracked emplaced siege weapon.</summary>
    public sealed class SiegeArm
    {
 /// <summary>Actor handle of the arm.</summary>
        public uint Handle { get; init; }

        
        
        
        
        
 /// <summary>Last yaw from 1640 ReqSiegeArmsRotationStop.</summary>
        public float Yaw;

 /// <summary>Handle of the operating PC, or 0 when the arm is vacated.</summary>
        public uint OperatorHandle;

        
        
        internal readonly Dictionary<uint, ArmCooldown> Cooldowns = new();
        internal readonly object CooldownLock = new();
    }

 /// <summary>
 /// A running arm-skill cooldown of one CoolTime_Group: the skill whose shot started it, when it
 /// ends and its full length.
 /// </summary>
    public readonly record struct ArmCooldown(uint SkillId, DateTime EndsAtUtc, uint TotalMs);

 /// <summary>One 2937 cooldown entry: <c>{u32 armSkillId, u32 remainingMs, u32 totalMs}</c>.</summary>
    public readonly record struct ArmCooldownEntry(uint SkillId, uint RemainingMs, uint TotalMs);

 /// <summary>
 /// The cooldown group an arm skill counts against: Skill_Table_Base.CoolTime_Group of its
 /// Skill_Group (skill id / 100), or the Skill_Group itself when the table gives 0 or has no row.
 /// </summary>
    public static uint CooldownGroupOf(uint skillId, IReadOnlyDictionary<uint, RO2.Domain.GameData.SkillData> skills)
    {
        uint skillGroup = skillId / 100;
        return skills.TryGetValue(skillGroup, out var row) && row.CoolTime_Group != 0 ? row.CoolTime_Group : skillGroup;
    }

 /// <summary>
 /// Fires <paramref name="skillId"/> from <paramref name="armHandle"/> at <paramref name="nowUtc"/>
 /// if the arm's <paramref name="group"/> is not cooling down: starts that group's window of
 /// <paramref name="totalMs"/> and returns true.
 /// </summary>
    public static bool TryStartArmCooldown(uint armHandle, uint group, uint skillId, uint totalMs, DateTime nowUtc,
        out ArmCooldown running)
    {
        var arm = GetOrCreate(armHandle);
        lock (arm.CooldownLock)
        {
            if (arm.Cooldowns.TryGetValue(group, out running) && running.EndsAtUtc > nowUtc)
                return false;
            running = new ArmCooldown(skillId, nowUtc.AddMilliseconds(totalMs), totalMs);
            arm.Cooldowns[group] = running;
            return true;
        }
    }

 /// <summary>
 /// The arm's windows still running at <paramref name="nowUtc"/>, one entry per cooldown group
 /// (ascending group id), with the time left rounded UP to the millisecond so a window that has not
 /// ended never reads 0. Expired windows are dropped.
 /// </summary>
    public static IReadOnlyList<ArmCooldownEntry> ActiveCooldowns(uint armHandle, DateTime nowUtc)
    {
        if (!_arms.TryGetValue(armHandle, out var arm)) return Array.Empty<ArmCooldownEntry>();
        lock (arm.CooldownLock)
        {
            List<ArmCooldownEntry>? entries = null;
            List<uint>? expired = null;
            foreach (var (group, cd) in arm.Cooldowns.OrderBy(kv => kv.Key))
            {
                if (cd.EndsAtUtc <= nowUtc)
                {
                    (expired ??= new List<uint>()).Add(group);
                    continue;
                }
                double leftMs = Math.Ceiling((cd.EndsAtUtc - nowUtc).TotalMilliseconds);
                uint remaining = (uint)Math.Clamp(leftMs, 1d, cd.TotalMs == 0 ? uint.MaxValue : cd.TotalMs);
                (entries ??= new List<ArmCooldownEntry>()).Add(new ArmCooldownEntry(cd.SkillId, remaining, cd.TotalMs));
            }
            if (expired != null)
                foreach (var group in expired) arm.Cooldowns.Remove(group);
            return entries ?? (IReadOnlyList<ArmCooldownEntry>)Array.Empty<ArmCooldownEntry>();
        }
    }

    
    private static readonly ConcurrentDictionary<uint, SiegeArm> _arms = new();
    
    private static readonly ConcurrentDictionary<uint, uint> _operatedBySession = new();

 /// <summary>Get the arm with this handle, creating a tracked entry if it is new.</summary>
    public static SiegeArm GetOrCreate(uint handle)
        => _arms.GetOrAdd(handle, h => new SiegeArm { Handle = h });

 /// <summary>Try to fetch a tracked arm by handle.</summary>
    public static bool TryGet(uint handle, out SiegeArm arm) => _arms.TryGetValue(handle, out arm!);

 /// <summary>
 /// Marks <paramref name="sessionId"/> as operating <paramref name="armHandle"/> and returns the
 /// arm, creating it if needed.
 /// </summary>
    public static SiegeArm BeginOperate(uint sessionId, uint armHandle, uint operatorHandle)
    {
        var arm = GetOrCreate(armHandle);
        arm.OperatorHandle = operatorHandle;
        _operatedBySession[sessionId] = armHandle;
        return arm;
    }

 /// <summary>Returns the vacated arm, or null if none.</summary>
    public static SiegeArm? EndOperate(uint sessionId)
    {
        if (!_operatedBySession.TryRemove(sessionId, out var handle)) return null;
        if (!_arms.TryGetValue(handle, out var arm)) return null;
        arm.OperatorHandle = 0;
        return arm;
    }

 /// <summary>The arm this session is currently operating, or null.</summary>
    public static SiegeArm? GetOperatedArm(uint sessionId)
    {
        if (!_operatedBySession.TryGetValue(sessionId, out var handle)) return null;
        return _arms.TryGetValue(handle, out var arm) ? arm : null;
    }
}
