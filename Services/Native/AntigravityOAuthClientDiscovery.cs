using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexBarWin.Services.Native;

/// <summary>An OAuth client pair. <see cref="ToString"/> never prints the secret.</summary>
public sealed record AntigravityOAuthClient(string ClientId, string ClientSecret)
{
    public bool IsConsumerClient => ClientId == AntigravityOAuthClientDiscovery.ConsumerClientId;
    public override string ToString() => $"AntigravityOAuthClient {{ Consumer = {IsConsumerClient}, Secret = [redacted] }}";
}

/// <summary>
/// Finds the OAuth client Antigravity signs in with, inside the installed Windows binaries (agy.exe or the IDE's
/// language_server.exe), without running them. Port of upstream CodexBar's amd64 Go record matcher
/// (AntigravityOAuthConfig.binaryClient), with RIP-relative addresses mapped through the PE section table instead
/// of a Mach-O __TEXT segment. Reads the file in chunks; never loads it whole.
/// </summary>
public static class AntigravityOAuthClientDiscovery
{
    /// <summary>
    /// Public client id that agy and the language server use for consumer (personal Google account) sign-in.
    /// Not a secret; used only to pick the matching record when a binary carries several clients.
    /// </summary>
    public const string ConsumerClientId = "1071006060591-tmhssin2h21lcre235vtolojh4g403ep.apps.googleusercontent.com";

    private const ushort MachineAmd64 = 0x8664;
    private const uint ScnMemExecute = 0x20000000;
    private const int RecordLength = 37;
    private const int ChunkSize = 1 << 20;
    private static ReadOnlySpan<byte> Marker => [0x48, 0xC7, 0x40, 0x08];              // mov qword [rax+8], imm32 (id length)
    private static ReadOnlySpan<byte> Middle => [0x48, 0x89, 0x08, 0x48, 0xC7, 0x40, 0x18]; // mov [rax], rcx; mov qword [rax+0x18], imm32

    /// <summary>Where WinGet and the Antigravity IDE installer put the binaries.</summary>
    public static IReadOnlyList<string> CandidatePaths()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var paths = new List<string>();
        var winget = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        try
        {
            if (Directory.Exists(winget))
                foreach (var dir in Directory.EnumerateDirectories(winget, "Google.AntigravityCLI_*"))
                    paths.Add(Path.Combine(dir, "agy.exe"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        paths.Add(Path.Combine(local, "Microsoft", "WinGet", "Links", "agy.exe"));
        paths.Add(Path.Combine(local, "Programs", "antigravity", "resources", "bin", "language_server.exe"));
        return paths;
    }

    /// <summary>First consumer client found in <paramref name="paths"/>; else the first client of any kind; else null.</summary>
    public static AntigravityOAuthClient? Discover(IEnumerable<string>? paths = null)
    {
        AntigravityOAuthClient? fallback = null;
        foreach (var path in paths ?? CandidatePaths())
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var client = FindInPe(new FileImage(handle));
                if (client?.IsConsumerClient == true) return client;
                fallback ??= client;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return fallback;
    }

    /// <summary>Random-access view over an image (a file, or a byte array in tests).</summary>
    public interface IImage
    {
        long Length { get; }
        int Read(long offset, Span<byte> destination);
    }

    public sealed class ByteImage(byte[] bytes) : IImage
    {
        public long Length => bytes.Length;
        public int Read(long offset, Span<byte> destination)
        {
            if (offset < 0 || offset >= bytes.Length) return 0;
            var count = (int)Math.Min(destination.Length, bytes.Length - offset);
            bytes.AsSpan((int)offset, count).CopyTo(destination);
            return count;
        }
    }

    private sealed class FileImage(SafeFileHandle handle) : IImage
    {
        public long Length { get; } = RandomAccess.GetLength(handle);
        public int Read(long offset, Span<byte> destination) => RandomAccess.Read(handle, destination, offset);
    }

    private readonly record struct Section(uint VirtualAddress, uint VirtualSize, uint RawPointer, uint RawSize, uint Characteristics);

    /// <summary>Scans an x64 PE image's executable sections for Go {ClientID, ClientSecret} initializers.</summary>
    public static AntigravityOAuthClient? FindInPe(IImage image)
    {
        Span<byte> header = stackalloc byte[4096];
        var headerLength = image.Read(0, header);
        header = header[..headerLength];
        if (headerLength < 0x40 || header[0] != (byte)'M' || header[1] != (byte)'Z') return null;
        var pe = BinaryPrimitives.ReadInt32LittleEndian(header[0x3C..]);
        if (pe <= 0 || pe + 24 > headerLength || !header.Slice(pe, 4).SequenceEqual("PE\0\0"u8)) return null;
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[(pe + 4)..]) != MachineAmd64) return null;
        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(header[(pe + 6)..]);
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(header[(pe + 20)..]);
        var table = pe + 24 + optionalSize;
        if (sectionCount is 0 or > 96 || table + sectionCount * 40 > headerLength) return null;
        var sections = new Section[sectionCount];
        for (var i = 0; i < sectionCount; i++)
        {
            var s = header.Slice(table + i * 40, 40);
            sections[i] = new(
                BinaryPrimitives.ReadUInt32LittleEndian(s[12..]), BinaryPrimitives.ReadUInt32LittleEndian(s[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(s[20..]), BinaryPrimitives.ReadUInt32LittleEndian(s[16..]),
                BinaryPrimitives.ReadUInt32LittleEndian(s[36..]));
        }

        AntigravityOAuthClient? first = null;
        var chunk = ArrayPool<byte>.Shared.Rent(ChunkSize + RecordLength);
        try
        {
            foreach (var section in sections)
            {
                if ((section.Characteristics & ScnMemExecute) == 0) continue;
                long start = section.RawPointer, end = Math.Min(image.Length, (long)section.RawPointer + section.RawSize);
                for (var position = start; position < end; position += ChunkSize)
                {
                    // Overlap by one record so a record straddling a chunk boundary is still seen whole.
                    var want = (int)Math.Min(ChunkSize + RecordLength, end - position);
                    var got = image.Read(position, chunk.AsSpan(0, want));
                    var span = chunk.AsSpan(0, got);
                    var limit = Math.Min(got - RecordLength, ChunkSize - 1);
                    for (var at = 0; at <= limit;)
                    {
                        var hit = span[at..].IndexOf(Marker);
                        if (hit < 0) break;
                        at += hit;
                        if (at > limit) break;
                        var client = TryRecord(image, sections, span.Slice(at, RecordLength), position + at);
                        if (client != null)
                        {
                            if (client.IsConsumerClient) return client;
                            first ??= client;
                        }
                        at += Marker.Length;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
        return first;
    }

    private static AntigravityOAuthClient? TryRecord(IImage image, Section[] sections, ReadOnlySpan<byte> record, long recordOffset)
    {
        if (!record.Slice(15, 7).SequenceEqual(Middle) || BinaryPrimitives.ReadUInt32LittleEndian(record[33..]) != 0x10488948) return null;
        var idLength = BinaryPrimitives.ReadInt32LittleEndian(record[4..]);
        var secretLength = BinaryPrimitives.ReadInt32LittleEndian(record[22..]);
        if (idLength is < 30 or > 256 || secretLength != 35) return null;
        var idOffset = LeaTarget(sections, record[8..], recordOffset + 8);
        var secretOffset = LeaTarget(sections, record[26..], recordOffset + 26);
        if (idOffset < 0 || secretOffset < 0) return null;

        Span<byte> id = stackalloc byte[idLength];
        Span<byte> secret = stackalloc byte[secretLength];
        if (image.Read(idOffset, id) != idLength || image.Read(secretOffset, secret) != secretLength) return null;
        if (!IsClientId(id) || !IsSecret(secret)) return null;
        return new(Encoding.ASCII.GetString(id), Encoding.ASCII.GetString(secret));
    }

    /// <summary>File offset targeted by "lea rcx, [rip+disp32]" at <paramref name="fileOffset"/>, or -1.</summary>
    private static long LeaTarget(Section[] sections, ReadOnlySpan<byte> instruction, long fileOffset)
    {
        if (instruction[0] != 0x48 || instruction[1] != 0x8D || instruction[2] != 0x0D) return -1;
        var rva = FileOffsetToRva(sections, fileOffset);
        if (rva < 0) return -1;
        var target = rva + 7 + BinaryPrimitives.ReadInt32LittleEndian(instruction[3..]);
        return RvaToFileOffset(sections, target);
    }

    private static long FileOffsetToRva(Section[] sections, long offset)
    {
        foreach (var s in sections)
            if (offset >= s.RawPointer && offset < (long)s.RawPointer + s.RawSize) return offset - s.RawPointer + s.VirtualAddress;
        return -1;
    }

    private static long RvaToFileOffset(Section[] sections, long rva)
    {
        foreach (var s in sections)
        {
            var size = Math.Min(s.VirtualSize == 0 ? s.RawSize : s.VirtualSize, s.RawSize);
            if (rva >= s.VirtualAddress && rva < (long)s.VirtualAddress + size) return rva - s.VirtualAddress + s.RawPointer;
        }
        return -1;
    }

    /// <summary>^[0-9]+-[A-Za-z0-9_-]+\.apps\.googleusercontent\.com$</summary>
    internal static bool IsClientId(ReadOnlySpan<byte> value)
    {
        var suffix = ".apps.googleusercontent.com"u8;
        if (!value.EndsWith(suffix)) return false;
        var head = value[..^suffix.Length];
        var dash = head.IndexOf((byte)'-');
        if (dash <= 0 || dash == head.Length - 1) return false;
        foreach (var b in head[..dash]) if (b is < (byte)'0' or > (byte)'9') return false;
        foreach (var b in head[(dash + 1)..]) if (!IsTokenByte(b)) return false;
        return true;
    }

    /// <summary>^GOCSPX-[A-Za-z0-9_-]{28}$</summary>
    internal static bool IsSecret(ReadOnlySpan<byte> value)
    {
        if (value.Length != 35 || !value.StartsWith("GOCSPX-"u8)) return false;
        foreach (var b in value[7..]) if (!IsTokenByte(b)) return false;
        return true;
    }

    private static bool IsTokenByte(byte b) =>
        b is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or (byte)'-' or (byte)'_';
}
