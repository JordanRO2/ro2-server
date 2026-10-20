using System;

namespace RO2.Server.Zone.Services;

/// <summary>
/// 2567 NfyGuildInviteMemberAns: notifies the inviter of the invitee's answer.
/// Kept separate from <see cref="GuildPackets"/>.
/// </summary>
public static class GuildInviteAnswerPackets
{
    /// <summary>Total body length: a fixed 61-byte blob.</summary>
    public const int BodyLen = 61;

    /// <summary>Length of the guild GUID at the start of the body.</summary>
    private const int GuidLen = 16;

    /// <summary>Offset of the guild GUID.</summary>
    private const int GuidOff = 0;

    /// <summary>Offset of the accept byte (the accept byte of the invitee's 1430 answer, echoed).</summary>
    private const int AcceptOff = GuidOff + GuidLen;

    /// <summary>Offset of the invitee name, which follows the accept byte.</summary>
    private const int NameOff = AcceptOff + 1;

    /// <summary>Capacity of the name field in UTF-16 characters (44 bytes, so the body ends at 17 + 44 = 61).</summary>
    private const int NameChars = 22;

    /// <summary>
    /// Builds the body of the 2567 NfyGuildInviteMemberAns notification: a fixed 61-byte layout made of a
    /// 16-byte guild GUID, an accept byte and a 22-character UTF-16LE invitee name.
    /// </summary>
    /// <remarks>
    /// The name is the invitee's canonical spelling, so the caller passes the answering character's name.
    /// Bytes after the name's terminator are zero-filled, as in <see cref="GuildPackets.BuildAnsGuildInviteMember"/>.
    /// </remarks>
    /// <param name="guildGuid16">The inviter's guild GUID (16 bytes).</param>
    /// <param name="accept">The accept flag from the invitee's answer, echoed.</param>
    /// <param name="inviteeName">The answering character's name.</param>
    public static byte[] BuildNfyGuildInviteMemberAns(ReadOnlySpan<byte> guildGuid16, byte accept, string inviteeName)
    {
        var body = new byte[BodyLen];
        guildGuid16[..GuidLen].CopyTo(body.AsSpan(GuidOff, GuidLen));
        body[AcceptOff] = accept;
        var name = inviteeName ?? string.Empty;
        int n = Math.Min(name.Length, NameChars - 1);   // keeps room for the NUL terminator
        for (int i = 0; i < n; i++)
        {
            int p = NameOff + i * 2;
            body[p] = (byte)(name[i] & 0xFF);
            body[p + 1] = (byte)(name[i] >> 8);
        }
        return body;
    }
}
