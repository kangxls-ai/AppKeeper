using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AppKeeper;

internal sealed record PigeonPacket(string Op, Guid Id = default, string? Text = null,
    string? Name = null, long Size = 0, string? Digest = null, int Port = 0);

internal static class PigeonWire
{
    internal const int MaximumFrame = 96 * 1024;

    internal static async Task Send(Stream stream, PigeonPacket packet, CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(packet);
        if (body.Length > MaximumFrame)
            throw new InvalidDataException("消息过长。");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, body.Length);
        await stream.WriteAsync(prefix, token);
        await stream.WriteAsync(body, token);
    }

    internal static async Task<PigeonPacket> Read(Stream stream, CancellationToken token)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MaximumFrame)
            throw new InvalidDataException("消息帧长度无效。");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, token);
        return JsonSerializer.Deserialize<PigeonPacket>(body) ?? throw new InvalidDataException("空消息。");
    }

    internal static X509Certificate2 Identity(string path)
    {
        if (File.Exists(path))
            return X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.UserKeySet);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=AppKeeper Pigeon", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        var identity = generated.Export(X509ContentType.Pkcs12);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            file.Write(identity);
            file.Flush(true);
        }
        return X509CertificateLoader.LoadPkcs12(identity, null, X509KeyStorageFlags.UserKeySet);
    }

    internal static string Fingerprint(X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    internal static string Code(string first, string second)
    {
        var pair = string.CompareOrdinal(first, second) < 0 ? first + second : second + first;
        var digest = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(pair));
        return (BinaryPrimitives.ReadUInt32LittleEndian(digest) % 1_000_000).ToString("D6");
    }

    internal static SslStream Encrypted(Stream stream, string? expected = null) =>
        new(stream, leaveInnerStreamOpen: false, (_, certificate, _, _) =>
            certificate != null && (expected == null || Fingerprint(certificate) == expected));
}
