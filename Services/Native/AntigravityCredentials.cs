using System.Runtime.InteropServices;
using System.Text.Json;

namespace CodexBarWin.Services.Native;

/// <summary>
/// The fields of Antigravity's sign-in this reader uses. <see cref="RefreshToken"/> and the client fields are only
/// populated when the caller asks for them (the opt-in refresh path). Token values never leave this process.
/// </summary>
public sealed record AntigravityCredentials
{
    public required string AccessToken { get; init; }
    public DateTimeOffset? Expiry { get; init; }
    public string? RefreshToken { get; init; }
    /// <summary>Account email as stored by agy ("id_token_email"); used only to compare accounts, never printed.</summary>
    public string? Email { get; init; }
    public string? ProjectId { get; init; }
    public string? AuthMethod { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }

    public override string ToString() => $"AntigravityCredentials {{ Expiry = {Expiry:o}, AuthMethod = {AuthMethod} }}";
}

/// <summary>Source of the raw Antigravity credential bytes.</summary>
public interface IAntigravityCredentialSource
{
    /// <summary>Returns the blob, or null when there is no sign-in. The caller disposes (zeroes) it.</summary>
    SensitiveBuffer? ReadBlob();
}

/// <summary>
/// Reads the generic credential agy (Antigravity CLI for Windows) writes to Windows Credential Manager.
/// Read-only by construction: only CredReadW and CredFree are imported, never CredWriteW or CredDeleteW.
/// </summary>
public sealed class WindowsCredentialManagerSource(string target = WindowsCredentialManagerSource.DefaultTarget) : IAntigravityCredentialSource
{
    public const string DefaultTarget = "gemini:antigravity";
    private const int CredTypeGeneric = 1;
    private const int ErrorNotFound = 1168;

    public string Target { get; } = target;

    public SensitiveBuffer? ReadBlob()
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!CredRead(Target, CredTypeGeneric, 0, out var handle))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound) return null;
            throw new IOException($"CredReadW failed with Win32 error {error}.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(handle);
            if (credential.CredentialBlobSize <= 0 || credential.CredentialBlob == IntPtr.Zero) return null;
            var buffer = new SensitiveBuffer(credential.CredentialBlobSize);
            Marshal.Copy(credential.CredentialBlob, buffer.Array, 0, credential.CredentialBlobSize);
            buffer.Length = credential.CredentialBlobSize;
            return buffer;
        }
        finally
        {
            CredFree(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);
}

/// <summary>Parses the Antigravity credential blob.</summary>
public static class AntigravityCredentialParser
{
    /// <summary>
    /// Accepts agy's Windows shape (a Go oauth2.Token under "token": access_token, refresh_token, RFC 3339 "expiry";
    /// plus "id_token_email", "auth_method") and upstream CodexBar's flat file shape (access_token/accessToken,
    /// expiry_date in ms, project_id, client_id/client_secret). Secrets other than the access token are read only
    /// when <paramref name="includeRefreshFields"/> is set. Returns null without an access token.
    /// </summary>
    public static AntigravityCredentials? Parse(ReadOnlySpan<byte> blob, bool includeRefreshFields = false)
    {
        var reader = new Utf8JsonReader(NativeCredentialFile.WithoutBom(blob), NativeCredentialFile.ReaderOptions);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            var fields = new Fields();
            ReadObject(ref reader, ref fields, includeRefreshFields, nested: false);
            var access = fields.Access?.Trim();
            if (string.IsNullOrEmpty(access)) return null;
            return new AntigravityCredentials
            {
                AccessToken = access,
                Expiry = fields.Expiry,
                RefreshToken = includeRefreshFields ? NativeParse.NonEmpty(fields.Refresh) : null,
                Email = NativeParse.NonEmpty(fields.Email),
                ProjectId = NativeParse.NonEmpty(fields.Project),
                AuthMethod = NativeParse.NonEmpty(fields.AuthMethod),
                ClientId = includeRefreshFields ? NativeParse.NonEmpty(fields.ClientId) : null,
                ClientSecret = includeRefreshFields ? NativeParse.NonEmpty(fields.ClientSecret) : null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private struct Fields
    {
        public string? Access, Refresh, Email, Project, AuthMethod, ClientId, ClientSecret;
        public DateTimeOffset? Expiry;
    }

    private static void ReadObject(ref Utf8JsonReader reader, ref Fields f, bool secrets, bool nested)
    {
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (!nested && reader.ValueTextEquals("token"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.StartObject) ReadObject(ref reader, ref f, secrets, nested: true);
                else reader.Skip();
            }
            else if (reader.ValueTextEquals("access_token"u8) || reader.ValueTextEquals("accessToken"u8)) { reader.Read(); f.Access = Str(ref reader) ?? f.Access; }
            else if (secrets && (reader.ValueTextEquals("refresh_token"u8) || reader.ValueTextEquals("refreshToken"u8))) { reader.Read(); f.Refresh = Str(ref reader) ?? f.Refresh; }
            else if (reader.ValueTextEquals("expiry"u8)) { reader.Read(); f.Expiry = NativeParse.ParseTimestamp(Str(ref reader)) ?? f.Expiry; }
            else if (reader.ValueTextEquals("expiry_date"u8) || reader.ValueTextEquals("expiresAt"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var ms) && ms > 0 && ms < 253402300799000)
                    f.Expiry = DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
            }
            else if (reader.ValueTextEquals("id_token_email"u8) || reader.ValueTextEquals("email"u8)) { reader.Read(); f.Email = Str(ref reader) ?? f.Email; }
            else if (reader.ValueTextEquals("project_id"u8) || reader.ValueTextEquals("projectId"u8)) { reader.Read(); f.Project = Str(ref reader) ?? f.Project; }
            else if (reader.ValueTextEquals("auth_method"u8)) { reader.Read(); f.AuthMethod = Str(ref reader); }
            else if (secrets && (reader.ValueTextEquals("client_id"u8) || reader.ValueTextEquals("clientId"u8))) { reader.Read(); f.ClientId = Str(ref reader); }
            else if (secrets && (reader.ValueTextEquals("client_secret"u8) || reader.ValueTextEquals("clientSecret"u8))) { reader.Read(); f.ClientSecret = Str(ref reader); }
            else { reader.Read(); reader.Skip(); }
        }
    }

    private static string? Str(ref Utf8JsonReader r) => r.TokenType == JsonTokenType.String ? r.GetString() : null;
}
