using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Protostar.Registry.Acceptance.Support;
using Reqnroll;
using Xunit;

namespace Protostar.Registry.Acceptance.Steps;

/// <summary>Steps for ReadingMemory.feature: a redacted memory read hides secrets, the default read is the push.</summary>
[Binding]
public sealed class ReadingMemorySteps
{
    // A well-formed fake, assembled here so the literal is not in the repository.
    private static readonly string GitHubToken = "gh" + "p_" + "abcdefghijklmnopqrstuvwxyz0123456789";
    private const string Plain = "Use the dev provider on ports other than 7443. café\n";

    private readonly RegistryScenario _scenario;
    private Guid _projectId;
    private string? _secretFile;
    private JsonArray? _files;

    public ReadingMemorySteps(RegistryScenario scenario) => _scenario = scenario;

    private string SecretFile => _secretFile ?? throw new InvalidOperationException("No memory was backed up in this scenario.");

    [Given("{word} has backed up a memory file with a GitHub token in it and one without")]
    public async Task GivenHasBackedUpMemoryWithAToken(string login)
    {
        var user = _scenario.User(login);
        var folder = RegistryUser.GitFolder($"/home/{login}/src/demo", RegistryUser.NewCommit());
        _secretFile = $"Deploy with token {GitHubToken} from the vault.\n";
        _projectId = await user.PushMemoryAsync(folder, "deploy.md", _secretFile);
        await user.PushMemoryAsync(folder, "ports.md", Plain);
    }

    [When("{word} reads her memory redacted")]
    public Task WhenReadsHerMemoryRedacted(string login) => DownloadAsync(login, "?redacted=true");

    [When("{word} downloads her memory")]
    public Task WhenDownloadsHerMemory(string login) => DownloadAsync(login, "");

    [Then("the file with the token reads with {string} in its place")]
    public void ThenTheFileReadsWithTheMarker(string marker) =>
        Assert.Equal(SecretFile.Replace(GitHubToken, marker), Content("deploy.md"));

    [Then("its sha256 and size describe what she reads")]
    public void ThenItsHashAndSizeDescribeWhatSheReads()
    {
        var file = File("deploy.md");
        var bytes = Convert.FromBase64String(file["content"]!.GetValue<string>());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), file["sha256"]!.GetValue<string>());
        Assert.Equal(bytes.Length, file["size"]!.GetValue<long>());
    }

    [Then("the file without a secret reads exactly as pushed")]
    public void ThenTheFileWithoutASecretReadsAsPushed() => Assert.Equal(Plain, Content("ports.md"));

    [Then("both files read exactly as pushed")]
    public void ThenBothFilesReadAsPushed()
    {
        Assert.Equal(SecretFile, Content("deploy.md"));
        Assert.Equal(Plain, Content("ports.md"));
    }

    private JsonNode File(string path) =>
        (_files ?? throw new InvalidOperationException("Nothing has been read in this scenario."))
            .Single(f => f!["path"]!.GetValue<string>() == path)!;

    private string Content(string path) => Encoding.UTF8.GetString(Convert.FromBase64String(File(path)["content"]!.GetValue<string>()));

    private async Task DownloadAsync(string login, string query) =>
        _files = (await _scenario.User(login).GetJsonAsync($"/v1/memory/{_projectId}/content{query}"))["files"]!.AsArray();
}
