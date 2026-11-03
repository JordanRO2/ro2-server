using RO2.Socials.Domain;
using System.Buffers.Binary;
using System.Text;
using RO2.Domain.Entities;
using RO2.Characters.Domain;
using RO2.Server.Chat;
using RO2.Server.Chat.Handlers.Rmi;
using Xunit;

namespace RO2.Tests.Packets;

// B14 S1: characterization of the pure, host-free parts of the social graph (friend list / block list) that the Socials carve moves:
// the result codes and caps, the GID / character-id duality, the friend + block wire builders and parsers, WithGids and the presence
// notices. Every value is read off the current code. NOT covered here (needs a live ProudSession + a Mongo-backed scope, no seam yet):
// the ORDER of the ReqAddFriendHandler decide chain (throttle -> malformed -> list-full 33 -> unknown name 18 -> self-add drop ->
// per-pair drop -> Add 6/33 -> success -> 23512 gate) and of ReqAddBlockHandler (throttle -> unreadable -> 18 -> self drop -> 7 -> full drop -> success);
// they are inline in HandleAsync and become pure Decide functions only when the carve extracts them.
public class SocialGraphPureTests
{
    private const ulong KnownGid = 0x0DE0B6B300000000UL;

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
    private static byte[] Utf16(string s) => Encoding.Unicode.GetBytes(s);

    // ---------------------------------------------------------------------------------------------------- CharacterGid

    [Fact]
    public void CharacterGid_Of_puts_the_known_high_dword_over_the_32bit_id_and_CharacterIdOf_takes_it_back()
    {
        Assert.Equal(0x0DE0B6B3A7F46769UL, CharacterGid.Of(KnownGid, 0xA7F46769));
        Assert.Equal(0xA7F46769u, CharacterGid.CharacterIdOf(0x0DE0B6B3A7F46769UL));
        Assert.Equal(0xA7F46769u, CharacterGid.CharacterIdOf(CharacterGid.Of(KnownGid, 0xA7F46769)));
    }

    [Fact]
    public void CharacterGid_Of_drops_the_low_dword_of_the_known_gid_and_a_bare_legacy_id_reads_as_itself()
    {
        Assert.Equal(0x0DE0B6B300000005UL, CharacterGid.Of(0x0DE0B6B3FFFFFFFFUL, 5));
        Assert.Equal(5u, CharacterGid.CharacterIdOf(5UL));            // rows / 23007 values written before the GID change
        Assert.Equal(0x0000000100000000UL, CharacterGid.Of(0x00000001FFFFFFFFUL, 0));
    }

    // ---------------------------------------------------------------------------------------------------- codes and caps

    [Fact]
    public void Friend_and_block_result_codes_and_caps_are_pinned()
    {
        Assert.Equal((ushort)0, FriendStore.AddResultOk);
        Assert.Equal((ushort)6, FriendStore.AddResultAlreadyFriend);
        Assert.Equal((ushort)18, FriendStore.AddResultNotFound);
        Assert.Equal((ushort)33, FriendStore.AddResultListFull);
        Assert.Equal((ushort)0, BlockListPackets.ResultOk);
        Assert.Equal((ushort)7, BlockListPackets.AddBlockResultAlreadyRegistered);
        Assert.Equal((ushort)18, BlockListPackets.AddBlockResultNotFound);
        Assert.Equal(200, FriendStore.MaxFriends);
        Assert.Equal(200, BlockListRepository.MaxBlocked);
        Assert.Equal("chat_friend_lists", FriendStore.CollectionName);
        Assert.Equal("chat_block_lists", BlockListRepository.CollectionName);
    }

    [Fact]
    public void IsListFull_flips_exactly_at_the_cap()
    {
        Assert.False(FriendStore.IsListFull(0));
        Assert.False(FriendStore.IsListFull(FriendStore.MaxFriends - 1));
        Assert.True(FriendStore.IsListFull(FriendStore.MaxFriends));
        Assert.True(FriendStore.IsListFull(FriendStore.MaxFriends + 1));
    }

    // ---------------------------------------------------------------------------------------------------- friend wire builders

    [Fact]
    public void AddFriend_failures_are_result_plus_len17_plus_17_zero_bytes()
    {
        var zeros = new string('0', 34);
        Assert.Equal("2100" + "11000000" + zeros, Hex(FriendStore.BuildAddFriendAnswer(FriendStore.AddResultListFull, null)));
        Assert.Equal("1200" + "11000000" + zeros, Hex(FriendStore.BuildAddFriendAnswer(FriendStore.AddResultNotFound, null)));
        Assert.Equal("0600" + "11000000" + zeros, Hex(FriendStore.BuildAddFriendAnswer(FriendStore.AddResultAlreadyFriend, null)));
    }

    [Fact]
    public void AddFriend_success_carries_the_record_and_innerLen_is_length_minus_6()
    {
        var entry = new CommunityChannels.FriendEntry(0x0DE0B6B3A7F46769UL, "Bob", true, 5, 4, "Channel 1", 0, 0);
        var body = FriendStore.BuildAddFriendAnswer(FriendStore.AddResultOk, entry);
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(body));
        Assert.Equal((uint)(body.Length - 6), BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(2)));
        // the record is the one-entry 23505 body from byte 8 on
        Assert.Equal(CommunityChannels.BuildFriendListBody(new[] { entry })[8..], body[6..]);
    }

    [Fact]
    public void DelFriend_answer_is_result_zero_then_the_echoed_uid()
    {
        Assert.Equal("0000" + "efcdab8967452301", Hex(FriendStore.BuildDelFriendAnswer(0x0123456789ABCDEFUL)));
    }

    [Fact]
    public void NfyAddFriend_is_a_bare_utf16_wstr_of_the_requester_name()
    {
        var body = FriendStore.BuildNfyAddFriend("Bob");
        Assert.Equal(0x02, body[0]);                                  // width tag: UTF-16LE
        Assert.Equal(Utf16("Bob"), body[^6..]);                       // name last
        Assert.Equal(3, body[^7]);                                    // compact length byte = char count (1-byte form)
        Assert.Equal(1 + 1 + 1 + 6, body.Length);                     // [tag][width][count][name]
    }

    [Fact]
    public void WithGids_rewrites_every_uid_to_ownerGid_high_dword_plus_the_low_dword_id_and_is_idempotent()
    {
        var bare = new CommunityChannels.FriendEntry(7, "A", false, 1, 1, "", 0, 0);
        var gid = bare with { Uid = 0x1111111100000009UL, Name = "B" };   // already a GID of another high dword
        var once = ReqFriendListHandler.WithGids(new[] { bare, gid }, KnownGid);
        Assert.Equal(new[] { 0x0DE0B6B300000007UL, 0x0DE0B6B300000009UL }, once.Select(e => e.Uid).ToArray());
        Assert.Equal(new[] { "A", "B" }, once.Select(e => e.Name).ToArray());
        Assert.Equal(once.Select(e => e.Uid), ReqFriendListHandler.WithGids(once, KnownGid).Select(e => e.Uid));
    }

    [Fact]
    public void FriendStore_Entry_blanks_the_location_when_offline_and_defaults_it_to_empty_when_online_without_one()
    {
        var c = new Character { Id = 11, Name = "Zed", Level = 9, Job = 3 };
        Assert.Equal("", FriendStore.Entry(c, online: false, location: "Channel 1").Location);
        Assert.Equal("Channel 1", FriendStore.Entry(c, online: true, location: "Channel 1").Location);
        Assert.Equal("", FriendStore.Entry(c, online: true, location: null).Location);
        var e = FriendStore.Entry(c, true, null);
        Assert.Equal((11UL, "Zed", (byte)9, (byte)3, (byte)0, (byte)0), (e.Uid, e.Name, e.Level, e.Job, e.TrailerA, e.TrailerB));
    }

    // ---------------------------------------------------------------------------------------------------- block wire builders / parser

    [Fact]
    public void BlockList_empty_is_the_8_byte_body_and_a_list_round_trips_through_a_hand_parse()
    {
        Assert.Equal("0000020000000000", Hex(BlockListPackets.BuildBlockListBody(Array.Empty<BlockedCharacter>())));

        var list = new[] { new BlockedCharacter(0x0DE0B6B300000007UL, "Ann"), new BlockedCharacter(0x0DE0B6B300000009UL, "Bo") };
        var body = BlockListPackets.BuildBlockListBody(list);
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(body));
        Assert.Equal((uint)(body.Length - 6), BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(2)));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(6)));
        var got = new List<BlockedCharacter>();
        var p = 8;
        for (var i = 0; i < 2; i++)
        {
            var gid = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(p)); p += 8;
            int n = body[p++];
            var name = Encoding.Unicode.GetString(body, p, n * 2); p += n * 2;
            Assert.Equal(0, body[p++]);                                // record terminator
            got.Add(new BlockedCharacter(gid, name));
        }
        Assert.Equal(body.Length, p);
        Assert.Equal(list, got);
    }

    [Fact]
    public void AddBlock_success_body_is_result0_innerLen_then_one_record_and_refusals_are_the_empty_record()
    {
        var ok = BlockListPackets.BuildAddBlockBody(0x0DE0B6B300000007UL, "Ann");
        // [u16 0][u32 innerLen = 8+1+6+1 = 16][u64 gid LE][03]["Ann" UTF-16LE][00]
        Assert.Equal("0000" + "10000000" + "07000000b3b6e00d" + "03" + Hex(Utf16("Ann")) + "00", Hex(ok));

        var zeros = new string('0', 20);
        Assert.Equal("0700" + "0a000000" + zeros, Hex(BlockListPackets.BuildAddBlockRefusal(BlockListPackets.AddBlockResultAlreadyRegistered)));
        Assert.Equal("1200" + "0a000000" + zeros, Hex(BlockListPackets.BuildAddBlockRefusal(BlockListPackets.AddBlockResultNotFound)));
    }

    [Fact]
    public void DelBlock_answer_is_result_zero_then_the_echoed_uid()
        => Assert.Equal("0000" + "efcdab8967452301", Hex(BlockListPackets.BuildDelBlockBody(0x0123456789ABCDEFUL)));

    private static byte[] AddBlockRequest(string name, uint? innerLen = null)
    {
        var n = Utf16(name);
        var b = new byte[4 + 1 + n.Length + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(b, innerLen ?? (uint)(1 + n.Length + 1));
        b[4] = (byte)name.Length;
        n.CopyTo(b, 5);
        return b;
    }

    [Fact]
    public void ReadAddBlockName_reads_the_first_name_and_rejects_short_empty_or_inconsistent_bodies()
    {
        Assert.Equal("iRO2ChatBot", BlockListPackets.ReadAddBlockName(AddBlockRequest("iRO2ChatBot")));
        Assert.Equal(0x18u, BinaryPrimitives.ReadUInt32LittleEndian(AddBlockRequest("iRO2ChatBot")));   // the documented 0x18

        Assert.Null(BlockListPackets.ReadAddBlockName(new byte[4]));                                       // shorter than the head
        Assert.Null(BlockListPackets.ReadAddBlockName(AddBlockRequest("")));                               // n1 == 0
        Assert.Null(BlockListPackets.ReadAddBlockName(AddBlockRequest("Abc", innerLen: 1 + 2 * 3 - 1)));   // innerLen < 1 + 2n
        Assert.Null(BlockListPackets.ReadAddBlockName(AddBlockRequest("Abc")[..(5 + 2 * 3 - 1)]));         // truncated name
    }

    // ---------------------------------------------------------------------------------------------------- presence notices

    [Fact]
    public void Login_notice_is_innerLen_uid_and_a_len8_location_and_is_clamped_at_255_chars_and_logout_is_the_bare_uid()
    {
        var login = FriendPresence.BuildLoginNotice(0x0DE0B6B300000007UL, "Channel 1");
        Assert.Equal((uint)(login.Length - 4), BinaryPrimitives.ReadUInt32LittleEndian(login));
        Assert.Equal(0x0DE0B6B300000007UL, BinaryPrimitives.ReadUInt64LittleEndian(login.AsSpan(4)));
        Assert.Equal(9, login[12]);
        Assert.Equal(Utf16("Channel 1"), login[13..]);

        var empty = FriendPresence.BuildLoginNotice(1, "");
        Assert.Equal(13, empty.Length);
        Assert.Equal(0, empty[12]);

        var clamped = FriendPresence.BuildLoginNotice(1, new string('x', 300));
        Assert.Equal(255, clamped[12]);
        Assert.Equal(13 + 510, clamped.Length);

        Assert.Equal("efcdab8967452301", Hex(FriendPresence.BuildLogoutNotice(0x0123456789ABCDEFUL)));
    }
}
