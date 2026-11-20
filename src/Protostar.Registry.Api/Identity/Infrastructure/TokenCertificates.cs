using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Protostar.Registry.Api.Identity;

/// <summary>The certificates OpenIddict signs and encrypts tokens with, loaded from PFX files named in configuration.</summary>
/// <remarks>
/// <para>
/// Tokens outlive the process that issued them: a CLI keeps its access and refresh tokens across registry
/// restarts and image upgrades. So the keys must come from somewhere that outlives the container, here two
/// PFX files mounted read-only, with their passwords in configuration. The signing key signs access and
/// identity tokens; the encryption key protects authorization codes and refresh tokens. Losing either
/// signs every user out.
/// </para>
/// <para>
/// Configuration: <c>OpenIddict:SigningCertificate:Path</c> and <c>:Password</c>,
/// <c>OpenIddict:EncryptionCertificate:Path</c> and <c>:Password</c>. When neither path is set, Development
/// uses OpenIddict's per-machine development certificates and every other environment refuses to start;
/// there is no silent fallback to development keys. Configured files win in every environment.
/// </para>
/// </remarks>
public sealed record TokenCertificates(X509Certificate2 Signing, X509Certificate2 Encryption)
{
    public const string SigningSection = "OpenIddict:SigningCertificate";
    public const string EncryptionSection = "OpenIddict:EncryptionCertificate";

    /// <summary>Loads both certificates, or returns <see langword="null"/> when Development should use the development certificates.</summary>
    /// <exception cref="InvalidOperationException">
    /// Outside Development with no certificates configured, when only one is configured, or when a file is missing,
    /// unreadable, the wrong password, or has no private key. The message names the setting to fix.
    /// </exception>
    public static TokenCertificates? Load(IConfiguration configuration, IHostEnvironment environment)
    {
        var signing = configuration.GetSection(SigningSection);
        var encryption = configuration.GetSection(EncryptionSection);

        if (string.IsNullOrWhiteSpace(signing["Path"]) && string.IsNullOrWhiteSpace(encryption["Path"]))
        {
            if (environment.IsDevelopment())
                return null;

            throw new InvalidOperationException(
                $"Token signing and encryption certificates are required in the {environment.EnvironmentName} environment, " +
                "and none are configured. Set OpenIddict__SigningCertificate__Path and OpenIddict__EncryptionCertificate__Path " +
                "to two PFX files (with OpenIddict__SigningCertificate__Password and OpenIddict__EncryptionCertificate__Password). " +
                "scripts/generate-token-certificates.sh creates them; see docs/deploy/production.md. " +
                "The development certificates are used only in Development.");
        }

        return new TokenCertificates(LoadPfx(signing, "signing"), LoadPfx(encryption, "encryption"));
    }

    private static X509Certificate2 LoadPfx(IConfigurationSection section, string role)
    {
        var setting = section.Path.Replace(":", "__");
        var path = section["Path"];
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException(
                $"The token {role} certificate is not configured: set {setting}__Path. Both certificates are required together.");

        if (!File.Exists(path))
            throw new InvalidOperationException($"The token {role} certificate file '{path}' ({setting}__Path) does not exist.");

        X509Certificate2 certificate;
        try
        {
            // EphemeralKeySet keeps the private key in memory instead of writing it to a key store on disk,
            // which a read-only, non-root container may not have.
            certificate = X509CertificateLoader.LoadPkcs12FromFile(path, section["Password"], X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (Exception e) when (e is CryptographicException or UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                $"The token {role} certificate file '{path}' ({setting}__Path) could not be loaded: {e.Message} " +
                $"Check that it is a PFX file readable by the registry's user and that {setting}__Password is its password.", e);
        }

        if (!certificate.HasPrivateKey)
            throw new InvalidOperationException(
                $"The token {role} certificate file '{path}' ({setting}__Path) has no private key. Export the PFX with its key.");

        return certificate;
    }
}
