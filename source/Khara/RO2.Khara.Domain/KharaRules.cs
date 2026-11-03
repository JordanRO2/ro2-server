using RO2.GameData.Tables;

namespace RO2.Kharas.Domain;

/// <summary>Khara_Info Complete_Type table and the pure row rules built on it.</summary>
public static class KharaRules
{
    public const byte CompleteKillNpc = 2;
    public const byte CompleteHoldItem = 4;
    public const byte CompleteQuestDone = 13;
    public const byte CompleteKharaDone = 40;
 /// <summary>Khara_Info Condition_Type of a main row gated on a completed quest.</summary>
    public const byte ConditionQuestDone = 8;

 /// <summary>Complete_Type of a row that counts item uses.</summary>
    public const byte CompleteUseItem = 6;
 /// <summary>Complete_Type of a row that counts titled completions.</summary>
    public const byte CompleteTitles = 22;
 /// <summary>Complete_Type of a row that counts deaths.</summary>
    public const byte CompleteDeath = 26;
 /// <summary>Complete_Type of a row that counts revivals.</summary>
    public const byte CompleteRevived = 27;
 /// <summary>Complete_Type of a row that counts harvests.</summary>
    public const byte CompleteHarvest = 29;
 /// <summary>Complete_Type of a row that counts skill uses by skill group.</summary>
    public const byte CompleteUseSkill = 31;
 /// <summary>Complete_Type of a row that counts Guardian summons.</summary>
    public const byte CompleteGuardianSummon = 32;
 /// <summary>Complete_Type of a row that requires guild membership.</summary>
    public const byte CompleteInGuild = 43;
 /// <summary>Complete_Type of a row that requires a base level.</summary>
    public const byte CompleteBaseLevel = 14;
 /// <summary>Complete_Type of a row that requires a profession level.</summary>
    public const byte CompleteProfessionLevel = 15;
 /// <summary>Complete_Type of a row that counts item repairs.</summary>
    public const byte CompleteRepairCount = 30;

 /// <summary>True for the level Complete_Types, which are checked on level change.</summary>
    public static bool IsLevelRow(byte completeType) => completeType is CompleteBaseLevel or CompleteProfessionLevel;

 /// <summary>
 /// Subject that a condition row counts, as carried by the 2577 message: the item for Complete_Type 6, 0 for the types without a
 /// subject, and the row's Complete_Type_Value1 otherwise.
 /// </summary>
    public static uint RowSubject(KharaInfo c) => c.Complete_Type switch
    {
        CompleteUseItem => c.Complete_Type_Value2,
        CompleteTitles or CompleteDeath or CompleteRevived or CompleteHarvest or CompleteGuardianSummon or CompleteInGuild
            or CompleteBaseLevel or CompleteProfessionLevel or CompleteRepairCount => 0,
        _ => c.Complete_Type_Value1,
    };

 /// <summary>Count a condition row must reach to be met.</summary>
    public static uint RowTarget(KharaInfo c) => c.Complete_Type switch
    {
        CompleteUseItem => c.Complete_Type_Value3,
        CompleteKillNpc or CompleteHoldItem or CompleteUseSkill => c.Complete_Type_Value2,
        _ => c.Complete_Type_Value1,
    };

 /// <summary>Active_Type of an action run when a Khara is accepted.</summary>
    public const byte ActiveOnAccept = 1;

 /// <summary>Action_Type of the accept action that spends Khara points.</summary>
    public const byte ActionSpendPoints = 27;

 /// <summary>Fail_Type of a time-limited Khara, whose limit is given in Fail_Type_Value1.</summary>
    public const byte FailTypeTimeLimit = 38;

 /// <summary>True when the Khara is time-limited.</summary>
    public static bool IsTimed(KharaDefinition def)
        => def.Main.Fail_Type == FailTypeTimeLimit || def.Conditions.Any(c => c.Fail_Type == FailTypeTimeLimit);

 /// <summary>
 /// Accepted Kharas whose main row names a profession other than <paramref name="profession"/>, in ascending order.
 /// </summary>
    public static List<uint> OtherProfessionKharas(KharaStateDocument state, KharaCatalog catalog, byte profession)
    {
        var ids = new List<uint>();
        foreach (var a in state.Active)
            if (catalog.Get(a.KharaId)?.Main.PC_ProJob is byte p && p != 0 && p != profession)
                ids.Add(a.KharaId);
        ids.Sort();
        return ids;
    }
}
