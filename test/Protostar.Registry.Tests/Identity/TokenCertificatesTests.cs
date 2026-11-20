using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Protostar.Registry.Api.Identity;

namespace Protostar.Registry.Tests.Identity;

/// <summary>Contract tests for <see cref="TokenCertificates.Load"/>.</summary>
public sealed class TokenCertificatesTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("protostar-token-certs-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Outside_Development_with_nothing_configured_startup_is_refused_naming_the_settings(string environmentName)
    {
        var error = Assert.Throws<InvalidOperationException>(() => TokenCertificates.Load(Configuration(), Environment(environmentName)));

        Assert.Contains("OpenIddict__SigningCertificate__Path", error.Message);
        Assert.Contains("OpenIddict__EncryptionCertificate__Path", error.Message);
    }

    [Fact]
    public void In_Development_with_nothing_configured_the_development_certificates_are_used()
    {
        Assert.Null(TokenCertificates.Load(Configuration(), Environment(Environments.Development)));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void Configured_certificates_are_loaded_with_their_private_keys_in_any_environment(string environmentName)
    {
        var signing = WritePfx("signing.pfx", "s3cret", X509KeyUsageFlags.DigitalSignature);
        var encryption = WritePfx("encryption.pfx", "0ther", X509KeyUsageFlags.KeyEncipherment);

        var certificates = TokenCertificates.Load(
            Configuration(("OpenIddict:SigningCertificate:Path", signing), ("OpenIddict:SigningCertificate:Password", "s3cret"),
                ("OpenIddict:EncryptionCertificate:Path", encryption), ("OpenIddict:EncryptionCertificate:Password", "0ther")),
            Environment(environmentName));

        Assert.NotNull(certificates);
        Assert.True(certificates.Signing.HasPrivateKey);
        Assert.True(certificates.Encryption.HasPrivateKey);
        Assert.Equal("CN=signing.pfx", certificates.Signing.Subject);
        Assert.Equal("CN=encryption.pfx", certificates.Encryption.Subject);
    }

    [Fact]
    public void Only_one_certificate_configured_is_refused()
    {
        var signing = WritePfx("signing.pfx", "pw", X509KeyUsageFlags.DigitalSignature);

        var error = Assert.Throws<InvalidOperationException>(() => TokenCertificates.Load(
            Configuration(("OpenIddict:SigningCertificate:Path", signing), ("OpenIddict:SigningCertificate:Password", "pw")),
            Environment("Production")));

        Assert.Contains("OpenIddict__EncryptionCertificate__Path", error.Message);
    }

    [Fact]
    public void A_missing_file_is_refused_naming_the_file()
    {
        var missing = Path.Combine(_directory, "nope.pfx");

        var error = Assert.Throws<InvalidOperationException>(() => TokenCertificates.Load(
            Configuration(("OpenIddict:SigningCertificate:Path", missing), ("OpenIddict:EncryptionCertificate:Path", missing)),
            Environment("Production")));

        Assert.Contains(missing, error.Message);
    }

    [Fact]
    public void A_wrong_password_is_refused_naming_the_password_setting()
    {
        var signing = WritePfx("signing.pfx", "right", X509KeyUsageFlags.DigitalSignature);
        var encryption = WritePfx("encryption.pfx", "right", X509KeyUsageFlags.KeyEncipherment);

        var error = Assert.Throws<InvalidOperationException>(() => TokenCertificates.Load(
            Configuration(("OpenIddict:SigningCertificate:Path", signing), ("OpenIddict:SigningCertificate:Password", "wrong"),
                ("OpenIddict:EncryptionCertificate:Path", encryption), ("OpenIddict:EncryptionCertificate:Password", "right")),
            Environment("Production")));

        Assert.Contains("OpenIddict__SigningCertificate__Password", error.Message);
    }

    [Fact]
    public void A_file_without_a_private_key_is_refused()
    {
        using var key = RSA.Create(2048);
        using var withKey = new CertificateRequest("CN=public-only", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var publicOnly = X509CertificateLoader.LoadCertificate(withKey.RawData);
        var path = Path.Combine(_directory, "public-only.pfx");
        File.WriteAllBytes(path, publicOnly.Export(X509ContentType.Pkcs12, "pw"));
        var encryption = WritePfx("encryption.pfx", "pw", X509KeyUsageFlags.KeyEncipherment);

        var error = Assert.Throws<InvalidOperationException>(() => TokenCertificates.Load(
            Configuration(("OpenIddict:SigningCertificate:Path", path), ("OpenIddict:SigningCertificate:Password", "pw"),
                ("OpenIddict:EncryptionCertificate:Path", encryption), ("OpenIddict:EncryptionCertificate:Password", "pw")),
            Environment("Production")));

        Assert.Contains("no private key", error.Message);
    }

    private string WritePfx(string name, string password, X509KeyUsageFlags usage)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, password));
        return path;
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    private static HostingEnvironment Environment(string name) => new() { EnvironmentName = name };
}
