using Org.BouncyCastle.Crypto.Parameters;
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AppKeeper;

internal static class AgendaSyncProtocol
{
    internal const int MaximumCiphertext = 8 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    internal const string PairContext = "MarkDo pairing v1";
    internal const string SyncContext = "MarkDo sync v1";
    internal static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static byte[] DecodeKey(string value)
    {
        var bytes = Convert.FromBase64String(value.Trim().TrimEnd('=').Replace('-', '+').Replace('_', '/') + "=");
        if (bytes.Length != 32) throw new CryptographicException("配对密钥无效。");
        return bytes;
    }
    internal static byte[] PairKey(string session, string secret, string code) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{PairContext}|{session}|{secret}|{code}"));

    internal static string Encrypt(object value, byte[] key, string context = SyncContext)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (plaintext.Length + 28 > MaximumCiphertext) throw new InvalidDataException("同步数据超过原 MarkDo 协议的大小上限。");
        var output = new byte[plaintext.Length + 28];
        RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(output.AsSpan(0, 12), plaintext, output.AsSpan(12, plaintext.Length), output.AsSpan(12 + plaintext.Length), Encoding.UTF8.GetBytes(context));
        return Convert.ToBase64String(output);
    }

    internal static JsonElement Decrypt(string value, byte[] key, string context = SyncContext)
    {
        if (value.Length > 12 * 1024 * 1024) throw new InvalidDataException("同步数据过大。");
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length is < 29 or > MaximumCiphertext) throw new CryptographicException("同步数据无效。");
        var plaintext = new byte[bytes.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(12, plaintext.Length), bytes.AsSpan(bytes.Length - 16), plaintext, Encoding.UTF8.GetBytes(context));
        using var document = JsonDocument.Parse(plaintext, new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("同步格式无效。");
        return document.RootElement.Clone();
    }

    internal static (string PublicKey, string Secret) ManualSecret(string publicKey)
    {
        var remote = Convert.FromBase64String(publicKey);
        if (remote.Length != 32) throw new CryptographicException("配对公钥无效。");
        var local = new X25519PrivateKeyParameters(RandomNumberGenerator.GetBytes(32), 0);
        var shared = new byte[32];
        local.GenerateSecret(new X25519PublicKeyParameters(remote, 0), shared, 0);
        return (Convert.ToBase64String(local.GeneratePublicKey().GetEncoded()),
            Convert.ToBase64String(shared).Replace('+', '-').Replace('/', '_')); // Dart base64UrlEncode includes its trailing '=' here.
    }
}

// Pairing keys are tied to the current Windows user, like MarkDo's secure storage.
internal static class AgendaKeyProtection
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { internal int Length; internal IntPtr Bytes; }
    internal static string Protect(string value) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), true));
    internal static string Unprotect(string value) => Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), false));
    private static byte[] Transform(byte[] value, bool protect)
    {
        var input = new Blob { Length = value.Length, Bytes = Marshal.AllocHGlobal(value.Length) };
        var output = new Blob();
        try
        {
            Marshal.Copy(value, 0, input.Bytes, value.Length);
            var ok = protect ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output) :
                CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("无法读取手机配对密钥，请在原 Windows 用户下运行或重新配对。", new Win32Exception(Marshal.GetLastWin32Error()));
            var result = new byte[output.Length];
            Marshal.Copy(output.Bytes, result, 0, result.Length);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input.Bytes);
            if (output.Bytes != IntPtr.Zero) LocalFree(output.Bytes);
        }
    }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
