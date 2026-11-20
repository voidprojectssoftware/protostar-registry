using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Protostar.Registry.Acceptance.Support;

/// <summary>A self-signed token signing certificate and encryption certificate, written as password-protected PFX files.</summary>
/// <remarks>
/// The registry runs here as <c>Production</c>, which refuses to start without configured token certificates
/// (<c>TokenCertificates</c>), exactly as a real deployment does. These stand in for the files
/// <c>scripts/generate-token-certificates.sh</c> makes. They live in a temporary folder for the run and are deleted after.
/// </remarks>
public sealed class TokenCertificateFiles : IDisposable
{
    private readonly string _folder;

    private TokenCertificateFiles(string folder, string signingPath, string encryptionPath, string password)
    {
        _folder = folder;
        SigningPath = signingPath;
        EncryptionPath = encryptionPath;
        Password = password;
    }

    public string SigningPath { get; }

    public string EncryptionPath { get; }

    public string Password { get; }

    public static TokenCertificateFiles Create()
    {
        var folder = Directory.CreateTempSubdirectory("protostar-registry-acceptance-").FullName;
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var signing = Path.Combine(folder, "signing.pfx");
        var encryption = Path.Combine(folder, "encryption.pfx");
        File.WriteAllBytes(signing, SelfSigned("protostar acceptance signing", X509KeyUsageFlags.DigitalSignature, password));
        File.WriteAllBytes(encryption, SelfSigned("protostar acceptance encryption", X509KeyUsageFlags.KeyEncipherment, password));
        return new TokenCertificateFiles(folder, signing, encryption, password);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A temporary folder left behind is harmless; failing the run over it would not be.
        }
    }

    private static byte[] SelfSigned(string name, X509KeyUsageFlags usage, string password)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return certificate.Export(X509ContentType.Pfx, password);
    }
}
