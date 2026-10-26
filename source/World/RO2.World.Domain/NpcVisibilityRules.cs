namespace RO2.Worlds.Domain;

/// <summary>What the map-load announcement (OnNPC_In 2506) does with one spawn of a map.</summary>
public enum NpcAnnouncement
{
    /// <summary>The NPC is dead: it is not announced, and not counted.</summary>
    Skip,
    /// <summary>The client has no template for the asset: it is not announced, and counted as skipped.</summary>
    NoTemplate,
    /// <summary>The NPC is announced.</summary>
    Announce,
}

/// <summary>What a request for an actor's view data (ReqActorViewData 1306) is answered with.</summary>
public enum ViewDataReply
{
    /// <summary>The handle is not a known NPC (a PC target included): no reply.</summary>
    UnknownActor,
    /// <summary>The NPC's asset has no client template: no reply, and a warning, because the actor should never have been announced.</summary>
    NoClientTemplate,
    /// <summary>The NPC is replied to with its view data (2319).</summary>
    Reply,
}

/// <summary>
/// Pure rules for which NPC actors are shown to a client. The client only instantiates an actor whose asset has a client
/// template (an NPCInfo row); announcing one that fails it makes the client retry ReqActorViewData forever, so the same
/// predicate gates the announcement and the reply.
/// </summary>
public static class NpcVisibilityRules
{
 /// <summary>
 /// Classifies a present spawn for the map-load announcement. A dead NPC is skipped first (and is never counted as missing a
 /// template); a live one without a client template is skipped and counted.
 /// </summary>
    public static NpcAnnouncement ClassifyAnnouncement(bool isDead, bool hasClientTemplate)
        => isDead ? NpcAnnouncement.Skip : hasClientTemplate ? NpcAnnouncement.Announce : NpcAnnouncement.NoTemplate;

 /// <summary>
 /// Resolves a request for an actor's view data: an unknown handle is not answered, nor is a known NPC without a client
 /// template; every other known NPC is.
 /// </summary>
    public static ViewDataReply ReplyFor(bool isKnownNpc, bool hasClientTemplate)
        => !isKnownNpc ? ViewDataReply.UnknownActor : hasClientTemplate ? ViewDataReply.Reply : ViewDataReply.NoClientTemplate;
}
