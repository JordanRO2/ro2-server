using RO2.Chats.Domain;
using System.Text;
using RO2.Server.Chat;
using Xunit;

namespace RO2.Tests.Packets;

// B15 S1: pins ChatText.IsCleanBody and ChannelChatFloodGate (pure; the gate's limiter is a process static, so every test uses its own character id).
public class ChatTextAndFloodGateTests
{
    private static byte[] U(string s) => Encoding.Unicode.GetBytes(s);

    [Theory]
    [InlineData("")]
    [InlineData("hello world")]
    [InlineData(" ")]                     // NBSP: just above the C1 range
    [InlineData("é中😀")]   // Latin-1 letter, CJK, a surrogate pair
    [InlineData("\uDFFF")]                     // lone surrogates sit below U+E000 and are accepted
    [InlineData("豈")]                     // just above the private-use range
    public void Clean_text_is_accepted(string s) => Assert.True(ChatText.IsCleanBody(U(s)));

    [Theory]
    [InlineData("a\0b")]       // NUL
    [InlineData("\u0001")]     // C0 low edge
    [InlineData("\n")]
    [InlineData("\t")]
    [InlineData("\u001F")]     // C0 high edge
    [InlineData("\u007F")]     // DEL
    [InlineData("\u0085")]     // C1
    [InlineData("\u009F")]     // C1 high edge
    [InlineData("")]     // private-use low edge
    [InlineData("")]     // private-use high edge
    [InlineData("")]     // private use next to an allowed marker
    [InlineData("")]
    [InlineData("")]
    [InlineData("")]
    public void Control_and_private_use_characters_are_rejected(string s) => Assert.False(ChatText.IsCleanBody(U(s)));

    [Fact]
    public void The_two_end_markers_are_the_only_private_use_characters_allowed()
    {
        Assert.Equal('', ChatText.EndMarkerF801);
        Assert.Equal('', ChatText.EndMarkerF842);
        Assert.True(ChatText.IsCleanBody(U("hi")));
        Assert.True(ChatText.IsCleanBody(U("hi")));
        Assert.True(ChatText.IsCleanBody(U("")));
        Assert.False(ChatText.IsCleanBody(U("hi\0")));   // one bad character anywhere rejects the whole line
    }

    [Fact]
    public void An_odd_byte_count_is_not_text()
    {
        Assert.False(ChatText.IsCleanBody(new byte[] { 0x41 }));
        Assert.False(ChatText.IsCleanBody(new byte[] { 0x41, 0x00, 0x42 }));
    }

    [Fact]
    public void The_policy_constants_are_four_lines_per_five_seconds_and_code_22()
    {
        Assert.Equal(4, ChannelChatFloodGate.MaxLinesPerWindow);
        Assert.Equal(5000L, ChannelChatFloodGate.WindowMs);
        Assert.Equal((ushort)22, ChannelChatFloodGate.ChatScrollingTooFast);
        Assert.Equal(new byte[] { 0x16, 0x00 }, ChannelChatFloodGate.RefusalBody());
    }

    [Fact]
    public void Four_lines_are_admitted_the_fifth_is_refused_and_the_window_slides()
    {
        uint who = 0xC0000000u + (uint)Random.Shared.Next(0, 1_000_000);
        const long t0 = 1_000_000;
        Assert.True(ChannelChatFloodGate.TryAdmit(who, t0));
        Assert.True(ChannelChatFloodGate.TryAdmit(who, t0 + 1000));
        Assert.True(ChannelChatFloodGate.TryAdmit(who, t0 + 2000));
        Assert.True(ChannelChatFloodGate.TryAdmit(who, t0 + 3000));
        Assert.False(ChannelChatFloodGate.TryAdmit(who, t0 + 4999));   // 5th inside 5 s of the first
        Assert.False(ChannelChatFloodGate.TryAdmit(who, t0 + 4999));   // a refused line is not counted, and stays refused
        Assert.True(ChannelChatFloodGate.TryAdmit(who, t0 + 5000));    // the first hit leaves the window at exactly W (>=)
        Assert.False(ChannelChatFloodGate.TryAdmit(who, t0 + 5001));   // window holds 1000/2000/3000/5000 again
    }

    [Fact]
    public void Senders_are_independent()
    {
        uint a = 0xD0000000u + (uint)Random.Shared.Next(0, 1_000_000), b = a + 1;
        for (int i = 0; i < 4; i++) Assert.True(ChannelChatFloodGate.TryAdmit(a, 10_000 + i));
        Assert.False(ChannelChatFloodGate.TryAdmit(a, 10_010));
        Assert.True(ChannelChatFloodGate.TryAdmit(b, 10_010));
    }
}
