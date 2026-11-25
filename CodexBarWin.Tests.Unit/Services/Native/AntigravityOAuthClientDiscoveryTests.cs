using System.Buffers.Binary;
using System.Text;
using AwesomeAssertions;
using CodexBarWin.Services.Native;

namespace CodexBarWin.Tests.Unit.Services.Native;

[TestClass]
public class AntigravityOAuthClientDiscoveryTests
{
    // Synthetic values only. The consumer client id is public (upstream ships it) and is used as a selector.
    private const string OtherId = "123456789012-syntheticotherclient0000000000000.apps.googleusercontent.com";
    private static readonly string OtherSecret = "GOCSPX-" + "SYNTHETICotherSECRET".PadRight(28, '0');
    private static readonly string ConsumerSecret = "GOCSPX-" + "SYNTHETICconsumerSECRET".PadRight(28, '0');

    private const int TextRaw = 0x400, TextVa = 0x1000, RdataRaw = 0x2400, RdataVa = 0x5000, SectionSize = 0x2000;

    /// <summary>Minimal x64 PE: .text with Go-style {ClientID, ClientSecret} initializers, .rdata with the strings.</summary>
    private static byte[] BuildPe(params (string Id, string Secret)[] records)
    {
        var image = new byte[RdataRaw + SectionSize];
        image[0] = (byte)'M'; image[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), 0x80);
        "PE\0\0"u8.CopyTo(image.AsSpan(0x80));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x84), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x86), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x94), 0); // no optional header in this fixture
        WriteSection(image, 0x98, ".text", TextVa, TextRaw, 0x60000020);
        WriteSection(image, 0x98 + 40, ".rdata", RdataVa, RdataRaw, 0x40000040);

        var text = TextRaw + 0x10;
        var data = RdataRaw + 0x10;
        foreach (var (id, secret) in records)
        {
            var idAt = data; data += Write(image, data, id);
            var secretAt = data; data += Write(image, data, secret);
            // mov qword [rax+8], len(id); lea rcx, [rip+id]; mov [rax], rcx;
            // mov qword [rax+0x18], len(secret); lea rcx, [rip+secret]; mov [rax+0x10], rcx
            var r = text;
            new byte[] { 0x48, 0xC7, 0x40, 0x08 }.CopyTo(image, r);
            BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(r + 4), id.Length);
            Lea(image, r + 8, idAt);
            new byte[] { 0x48, 0x89, 0x08, 0x48, 0xC7, 0x40, 0x18 }.CopyTo(image, r + 15);
            BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(r + 22), secret.Length);
            Lea(image, r + 26, secretAt);
            new byte[] { 0x48, 0x89, 0x48, 0x10 }.CopyTo(image, r + 33);
            text += 64;
        }
        return image;
    }

    private static void WriteSection(byte[] image, int at, string name, int va, int raw, uint characteristics)
    {
        Encoding.ASCII.GetBytes(name).CopyTo(image, at);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(at + 8), SectionSize);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(at + 12), va);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(at + 16), SectionSize);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(at + 20), raw);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 36), characteristics);
    }

    private static int Write(byte[] image, int at, string value)
    {
        Encoding.ASCII.GetBytes(value).CopyTo(image, at);
        return value.Length + 8;
    }

    private static void Lea(byte[] image, int at, int targetFileOffset)
    {
        image[at] = 0x48; image[at + 1] = 0x8D; image[at + 2] = 0x0D;
        var instructionRva = at - TextRaw + TextVa;
        var targetRva = targetFileOffset - RdataRaw + RdataVa;
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(at + 3), targetRva - (instructionRva + 7));
    }

    [TestMethod]
    public void FindInPe_WhenSeveralClients_PrefersConsumerClient()
    {
        var pe = BuildPe((OtherId, OtherSecret), (AntigravityOAuthClientDiscovery.ConsumerClientId, ConsumerSecret));

        var client = AntigravityOAuthClientDiscovery.FindInPe(new AntigravityOAuthClientDiscovery.ByteImage(pe));

        client!.ClientId.Should().Be(AntigravityOAuthClientDiscovery.ConsumerClientId);
        client.ClientSecret.Should().Be(ConsumerSecret);
        client.IsConsumerClient.Should().BeTrue();
    }

    [TestMethod]
    public void FindInPe_WhenOnlyOtherClient_ReturnsIt()
    {
        var pe = BuildPe((OtherId, OtherSecret));

        var client = AntigravityOAuthClientDiscovery.FindInPe(new AntigravityOAuthClientDiscovery.ByteImage(pe));

        client!.ClientId.Should().Be(OtherId);
        client.IsConsumerClient.Should().BeFalse();
    }

    [TestMethod]
    public void FindInPe_WhenSecretMalformed_IgnoresRecord()
    {
        var pe = BuildPe((OtherId, "GOCSPX-short"));

        AntigravityOAuthClientDiscovery.FindInPe(new AntigravityOAuthClientDiscovery.ByteImage(pe)).Should().BeNull();
    }

    [TestMethod]
    public void FindInPe_WhenNotPe_ReturnsNull()
    {
        var bytes = Encoding.ASCII.GetBytes(AntigravityOAuthClientDiscovery.ConsumerClientId + " " + ConsumerSecret);

        AntigravityOAuthClientDiscovery.FindInPe(new AntigravityOAuthClientDiscovery.ByteImage(bytes)).Should().BeNull();
    }

    [TestMethod]
    public void Discover_WhenPathsMissing_ReturnsNull() =>
        AntigravityOAuthClientDiscovery.Discover([Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "agy.exe")]).Should().BeNull();

    [TestMethod]
    public void ToString_WhenCalled_NeverContainsSecret() =>
        new AntigravityOAuthClient(OtherId, OtherSecret).ToString().Should().NotContain(OtherSecret).And.NotContain("GOCSPX");
}
