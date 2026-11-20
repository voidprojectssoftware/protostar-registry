using Protostar.Registry.Api.Search;

namespace Protostar.Registry.Tests.Search.Domain;

/// <summary>Contract tests for <see cref="SecretRedactor"/>: each known pattern is replaced by its typed marker, and look-alikes are left alone.</summary>
/// <remarks>
/// Fake secrets are assembled from pieces at run time, so no string in this file has a real secret's shape for a
/// scanner to flag.
/// </remarks>
public sealed class SecretRedactorTests
{
    private static string Repeat(string unit, int length) => string.Concat(Enumerable.Repeat(unit, length / unit.Length + 1))[..length];

    private static readonly string Alnum36 = Repeat("Zx9Kq2Lm4N", 36);

    public static TheoryData<string, string, string> Secrets() => new()
    {
        // type, secret as it appears, the text around it ({0} is the secret)
        { "github-token", "gh" + "p_" + Alnum36, "my token {0} please" },
        { "github-token", "gh" + "s_" + Alnum36, "{0}" },
        { "github-token", "github" + "_pat_" + Repeat("11ABCDEFG0_", 82), "use {0} for the API" },
        { "aws-access-key", "AK" + "IA" + Repeat("QX7RZ2MNB4", 16), "key id {0}." },
        { "aws-access-key", "AS" + "IA" + Repeat("QX7RZ2MNB4", 16), "\"{0}\"" },
        { "anthropic-key", "sk-" + "ant-api03-" + Repeat("aB3_dE5-fG7h", 60), "ANTHROPIC_KEY is {0}" },
        { "openai-key", "sk-" + "proj-" + Repeat("aB3dE5fG7hJ9", 48), "key {0} here" },
        { "openai-key", "sk-" + Repeat("aB3dE5fG7hJ9", 48), "{0}" },
        { "google-api-key", "AI" + "za" + Repeat("SyB3dE5fG7hJ9kL1mN3", 35), "maps key {0}" },
        { "azure-client-secret", "abc" + "8Q~" + Repeat("aB3dE5fG7hJ9kL1mN3", 34), "client secret value {0} for the app" },
        { "slack-token", "xo" + "xb-" + "1234567890-" + Repeat("aB3dE5", 24), "{0}" },
        { "stripe-key", "sk" + "_live_" + Repeat("aB3dE5fG7h", 24), "{0}" },
        { "jwt", "ey" + "JhbGciOiJIUzI1NiJ9." + "ey" + "JzdWIiOiIxMjM0NTY3ODkwIn0." + "dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U", "Authorization: Bearer {0}" },
    };

    [Theory]
    [MemberData(nameof(Secrets))]
    public void A_known_secret_is_replaced_by_its_typed_marker(string type, string secret, string around)
    {
        var text = string.Format(around, secret);

        var redacted = SecretRedactor.Redact(text);

        Assert.DoesNotContain(secret, redacted);
        Assert.Equal(string.Format(around, SecretRedactor.Marker(type)), redacted);
    }

    [Fact]
    public void A_private_key_block_is_replaced_whole()
    {
        var block = "-----BEGIN " + "RSA PRIVATE KEY-----\nMIIEpAIBAAKCAQEA" + Repeat("q8Zx", 64) + "\n-----END RSA PRIVATE KEY-----";

        var redacted = SecretRedactor.Redact($"here it is:\n{block}\nthanks");

        Assert.Equal("here it is:\n[redacted:private-key]\nthanks", redacted);
    }

    [Fact]
    public void A_private_key_block_escaped_in_json_is_replaced()
    {
        var block = "-----BEGIN " + "PRIVATE KEY-----\\nMIIEvQIBADANBg" + Repeat("q8Zx", 64) + "\\n-----END PRIVATE KEY-----\\n";

        var redacted = SecretRedactor.Redact($"{{\"private_key\": \"{block}\"}}");

        Assert.DoesNotContain("MIIEvQIBADANBg", redacted);
        Assert.Contains("[redacted:private-key]", redacted);
    }

    [Fact]
    public void A_truncated_private_key_block_is_replaced_to_the_end_of_its_body()
    {
        var redacted = SecretRedactor.Redact("-----BEGIN " + "OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA" + Repeat("q8Zx", 40));

        Assert.Equal("[redacted:private-key]", redacted);
    }

    [Theory]
    [InlineData("password=hunter2-supersecret", "password=[redacted:secret]")]
    [InlineData("DB_PASSWORD: \"s3cr3t-value\"", "DB_PASSWORD: \"[redacted:secret]\"")]
    [InlineData("{\"client_secret\": \"abcd1234efgh\"}", "{\"client_secret\": \"[redacted:secret]\"}")]
    [InlineData("export API_KEY=abcdef123456", "export API_KEY=[redacted:secret]")]
    [InlineData("secret = 'tops3cret'", "secret = '[redacted:secret]'")]
    [InlineData("Api_Key=abcdef123456", "Api_Key=[redacted:secret]")]
    [InlineData("api_\u212Aey=abcdef123456", "api_\u212Aey=[redacted:secret]")] // a Kelvin sign is a k to a case-insensitive pattern
    public void A_value_assigned_to_a_secret_name_is_replaced_and_the_name_kept(string text, string expected) =>
        Assert.Equal(expected, SecretRedactor.Redact(text));

    [Theory]
    [InlineData("Host=db;Username=app;Password=Pa55word!;Database=registry", "Host=db;Username=app;Password=[redacted:connection-string-password];Database=registry")]
    [InlineData("Server=tcp:x.database.windows.net;User ID=sa;Pwd=Pa55word!", "Server=tcp:x.database.windows.net;User ID=sa;Pwd=[redacted:connection-string-password]")]
    public void A_connection_string_password_is_replaced(string text, string expected) =>
        Assert.Equal(expected, SecretRedactor.Redact(text));

    [Fact]
    public void A_password_in_a_url_is_replaced_and_the_user_and_host_kept() =>
        Assert.Equal(
            "postgres://app:[redacted:url-password]@db.internal:5432/registry",
            SecretRedactor.Redact("postgres://app:Pa55word!@db.internal:5432/registry"));

    [Fact]
    public void An_azure_storage_account_key_is_replaced()
    {
        var key = Repeat("aB3dE5fG7hJ9kL1mN3+/", 86) + "==";

        var redacted = SecretRedactor.Redact($"DefaultEndpointsProtocol=https;AccountName=acct;AccountKey={key};EndpointSuffix=core.windows.net");

        Assert.Equal("DefaultEndpointsProtocol=https;AccountName=acct;AccountKey=[redacted:azure-storage-key];EndpointSuffix=core.windows.net", redacted);
    }

    [Fact]
    public void An_azure_sas_signature_is_replaced_and_the_rest_of_the_url_kept() =>
        Assert.Equal(
            "https://acct.blob.core.windows.net/c/b?sv=2022-11-02&se=2026-12-01&sig=[redacted:azure-sas-signature]",
            SecretRedactor.Redact("https://acct.blob.core.windows.net/c/b?sv=2022-11-02&se=2026-12-01&sig=aB3dE5fG7hJ9kL1m%2BN3pQ5%3D"));

    [Fact]
    public void An_aws_secret_access_key_assigned_to_its_name_is_replaced() =>
        Assert.Equal(
            "aws_secret_access_key = [redacted:aws-secret-key]",
            SecretRedactor.Redact("aws_secret_access_key = " + Repeat("wJalrXUtnFEMI/K7MDENG+bPxRfiCY", 40)));

    [Fact]
    public void A_secret_assigned_to_a_name_keeps_its_specific_type()
    {
        var token = "gh" + "p_" + Alnum36;

        Assert.Equal("GITHUB_TOKEN=[redacted:github-token]", SecretRedactor.Redact($"GITHUB_TOKEN={token}"));
    }

    [Fact]
    public void Several_secrets_in_one_text_are_all_replaced()
    {
        var token = "gh" + "p_" + Alnum36;
        var key = "AK" + "IA" + Repeat("QX7RZ2MNB4", 16);

        var redacted = SecretRedactor.Redact($"token {token} and key {key} and {token} again");

        Assert.Equal("token [redacted:github-token] and key [redacted:aws-access-key] and [redacted:github-token] again", redacted);
    }

    [Fact]
    public void Redacting_twice_changes_nothing_more()
    {
        var once = SecretRedactor.Redact("password=hunter2-supersecret and gh" + "p_" + Alnum36);

        Assert.Equal(once, SecretRedactor.Redact(once));
    }

    // False positives: things that look random but are not secrets, and must come through untouched.
    [Theory]
    [InlineData("commit 9fceb02d0ae598e95dc970b74767f19372d61af8 fixed it")]
    [InlineData("sha256 e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("session 6f83525b-790f-4654-944d-ba3b0c96453e on machine 3F2504E0-4F89-11D3-9A0C-0305E82C3301")]
    [InlineData("<img src=\"data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==\">")]
    [InlineData("data:image/jpeg;base64,/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/")]
    [InlineData("set max_tokens: 4096 and input_token: 12")]
    [InlineData("the token: a short word")]
    [InlineData("pass the password: prompt to the user")]
    [InlineData("use the sk-learn-style-kebab-case-name-here helper")]
    [InlineData("https://github.com/voidprojectssoftware/protostar-registry/pull/66")]
    [InlineData("AKIA is the prefix AWS uses for access key ids")]
    [InlineData("eyJ alone is just the start of base64 JSON")]
    [InlineData("A plain sentence about parentUuid ordering and DNS records.")]
    public void Text_without_a_known_secret_is_unchanged(string text) =>
        Assert.Equal(text, SecretRedactor.Redact(text));

    [Fact]
    public void Null_and_empty_pass_through()
    {
        Assert.Null(SecretRedactor.RedactOrNull(null));
        Assert.Equal(string.Empty, SecretRedactor.Redact(string.Empty));
    }

    [Fact]
    public void Every_pattern_has_a_distinct_type() =>
        Assert.Equal(SecretRedactor.Types.Count, SecretRedactor.Types.Distinct().Count());
}
