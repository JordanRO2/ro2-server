using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// 2893 AnsGmCommand. No reference frame exists, so these tests pin the layout the client reader expects
/// and are not verified against reference traffic: the dispatch reads a u32 length and that many bytes, and
/// the consumer reads a u16 code, then a u16 count and that many UTF-16 units.
/// </summary>
public class GmCommand2893Tests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact]
    public void PcLocation_PinsTheReaderLayout()
    {
        var body = GmCommandPackets.BuildAnsGmCommand2893(GmCommandPackets.PcLocation, "1100 1.5,2,3");

        
        Assert.Equal("1c000000" + "0a00" + "0c00" + Hex(Encoding.Unicode.GetBytes("1100 1.5,2,3")), Hex(body));
    }

    [Fact]
    public void EmptyArgument_IsCountZero()
    {
        
        Assert.Equal("04000000" + "0400" + "0000", Hex(GmCommandPackets.BuildAnsGmCommand2893(GmCommandPackets.GmGrade, "")));
    }

    [Fact]
    public void ReadsBackTheWayTheClientReaderDoes()
    {
        const string name = "J0K3R zufafah";
        var body = GmCommandPackets.BuildAnsGmCommand2893(GmCommandPackets.PcNotFoundByName, name);

        uint innerLen = BinaryPrimitives.ReadUInt32LittleEndian(body);
        Assert.Equal(body.Length - 4, (int)innerLen);             
        var inner = body.AsSpan(4);
        Assert.Equal(GmCommandPackets.PcNotFoundByName, BinaryPrimitives.ReadUInt16LittleEndian(inner));
        int n = BinaryPrimitives.ReadUInt16LittleEndian(inner[2..]);
        Assert.Equal(inner.Length - 4, 2 * n);                     
        Assert.Equal(name, Encoding.Unicode.GetString(inner.Slice(4, 2 * n)));
    }

    [Theory]
    [InlineData(0)]    
    [InlineData(31)]   
    [InlineData(32)]   
    public void NonMessageCodes_AreRefused(int code)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GmCommandPackets.BuildAnsGmCommand2893((ushort)code, "x"));
    }

 /// <summary>
 /// Each code the server sends names the client string it is meant to show: 2000000160 + code in
 /// data/strings/string_etc.json, with an @1 for the argument.
 /// </summary>
    [Theory]
    [InlineData(GmCommandPackets.PcNotFoundByName, "Not Founded PC. Name: @1")]
    [InlineData(GmCommandPackets.GmGrade, "GM grade : @1")]
    [InlineData(GmCommandPackets.ItemNotFound, "Not Founded Item. Index: @1")]
    [InlineData(GmCommandPackets.PcLocation, "PC Location: @1")]
    [InlineData(GmCommandPackets.IncorrectSpeed, "Incorrect Speed : @1")]
    [InlineData(GmCommandPackets.InvalidMapId, "Invalid Map ID : @1")]
    [InlineData(GmCommandPackets.InvalidCoordinates, "Invalid Coordinates : @1")]
    public async Task Codes_NameTheClientStrings(ushort code, string expected)
    {
        await using var file = File.OpenRead(Path.Combine(DataDirectory(), "strings", "string_etc.json"));
        using var doc = await JsonDocument.ParseAsync(file);
        var id = (GmCommandPackets.MessageStringBase + code).ToString(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(expected, doc.RootElement.GetProperty(id).GetString());
    }

    private static string DataDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "strings", "string_etc.json")))
                return candidate;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/strings/string_etc.json");
    }
}
