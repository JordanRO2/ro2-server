using System.Text;

namespace RO2.Recruitments.Domain;

/// <summary>Decoding of the fixed UTF-16 text buffers both boards read from their request bodies.</summary>
public static class WideText
{
    /// <summary>
    /// Reads a NUL-terminated wide string from a buffer of <paramref name="textChars"/> characters.
    /// </summary>
    public static string Read(ReadOnlySpan<byte> buffer, int textChars)
    {
        var chars = 0;
        // At most textChars - 1: the stored text must always fit a buffer that keeps its NUL.
        while (chars < textChars - 1 && chars * 2 + 1 < buffer.Length
               && (buffer[chars * 2] != 0 || buffer[chars * 2 + 1] != 0))
            chars++;
        return Encoding.Unicode.GetString(buffer[..(chars * 2)]);
    }
}
