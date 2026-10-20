namespace RO2.Server.Chat;

/// <summary>
/// Content check for a chat line before it is relayed. The three send paths (23528 channel, 23529 private
/// room, 23529 close-range) relay the client's UTF-16LE bytes verbatim, so a hostile client could push NUL,
/// control characters or private-use glyphs to every receiver.
/// </summary>
/// <remarks>
/// Server-side policy: there is no refusal frame for a bad line, so the caller drops it silently. The two
/// allowed private-use characters are the client's own end-of-line markers.
/// </remarks>
public static class ChatText
{
    /// <summary>The client's usual end-of-line marker.</summary>
    public const char EndMarkerF801 = '';

    /// <summary>The second end marker seen on private-room lines.</summary>
    public const char EndMarkerF842 = '';

    /// <summary>
    /// True when <paramref name="utf16le"/> (UTF-16LE, as on the wire) holds no NUL, C0 (U+0001-U+001F), DEL/C1
    /// (U+007F-U+009F) or private-use (U+E000-U+F8FF) character other than the two end markers. An odd byte count
    /// is not text and is rejected.
    /// </summary>
    public static bool IsCleanBody(ReadOnlySpan<byte> utf16le)
    {
        if ((utf16le.Length & 1) != 0) return false;
        for (int i = 0; i < utf16le.Length; i += 2)
        {
            char c = (char)(utf16le[i] | (utf16le[i + 1] << 8));
            if (c <= '\u001F' || (c >= '\u007F' && c <= '\u009F')) return false;
            if (c >= '' && c <= '' && c != EndMarkerF801 && c != EndMarkerF842) return false;
        }
        return true;
    }
}
