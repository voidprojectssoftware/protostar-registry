using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Protostar.Registry.Acceptance.Support;

/// <summary>A signed-in user and the requests a client of the registry makes for them.</summary>
/// <remarks>
/// The helpers send what the CLI sends (the multipart skill push, transcript register then append, the memory push),
/// so a scenario reads as what the user did. Reads stay as plain requests in the steps, where the assertion is.
/// </remarks>
public sealed class RegistryUser
{
    /// <summary>The harness every backed-up session in these scenarios claims to come from.</summary>
    public const string Harness = "claude-code";

    private int _pushes;
    private Guid? _machineId;

    public RegistryUser(string login, HttpClient http)
    {
        Login = login;
        Http = http;
    }

    public string Login { get; }

    /// <summary>A client that sends this user's access token.</summary>
    public HttpClient Http { get; }

    /// <summary>Pushes a one-file skill named <paramref name="name"/> whose content differs from every earlier push.</summary>
    public Task<HttpResponseMessage> PushSkillAsync(string name, int? baseVersion = null)
    {
        var manifest = $"---\nname: {name}\ndescription: An acceptance test skill.\n---\nPush {++_pushes} from {Login}, {Guid.NewGuid()}.\n";
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(manifest));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        form.Add(file, "files", "SKILL.md");
        if (baseVersion is { } based)
            form.Add(new StringContent(based.ToString()), "baseVersion");
        return Http.PostAsync("/v1/skills", form);
    }

    /// <summary>Returns the machine this user's backups come from, registering it on first use.</summary>
    public async Task<Guid> EnsureMachineAsync()
    {
        if (_machineId is { } id)
            return id;

        id = Guid.NewGuid();
        var response = await Http.PutAsJsonAsync($"/v1/machines/{id}", new { name = $"{Login}-laptop", operatingSystem = "linux" });
        Assert.True(response.IsSuccessStatusCode, $"Registering {Login}'s machine answered {(int)response.StatusCode}.");
        _machineId = id;
        return id;
    }

    /// <summary>Backs up a new Claude Code session in which the user typed <paramref name="prompt"/>, and returns its key.</summary>
    public Task<string> BackUpSessionAsync(string prompt) =>
        BackUpSessionAsync(key => Encoding.UTF8.GetBytes(PromptLine(key, prompt).ToJsonString() + "\n"));

    /// <summary>Backs up a new Claude Code session whose JSONL is what <paramref name="content"/> returns for its key, and returns the key.</summary>
    public async Task<string> BackUpSessionAsync(Func<string, byte[]> content)
    {
        var machineId = await EnsureMachineAsync();
        var key = Guid.NewGuid().ToString();

        var registered = await Http.PutAsJsonAsync($"/v1/transcripts/{Harness}/{key}", new
        {
            kind = "session",
            machineId,
            sourcePath = $"/home/{Login}/.claude/projects/demo/{key}.jsonl",
        });
        Assert.True(registered.IsSuccessStatusCode, $"Registering the session answered {(int)registered.StatusCode}.");
        var position = (await registered.Content.ReadFromJsonAsync<JsonObject>())!;

        var appended = await AppendAsync(key, content(key), position["revision"]!.GetValue<int>(), position["length"]!.GetValue<long>());
        Assert.True(appended.IsSuccessStatusCode, $"Appending to the session answered {(int)appended.StatusCode}: {await appended.Content.ReadAsStringAsync()}");
        return key;
    }

    /// <summary>Appends one line in which the user typed <paramref name="prompt"/> to the session <paramref name="key"/>, at the given position.</summary>
    public Task<HttpResponseMessage> AppendPromptAsync(string key, string prompt, int revision, long offset) =>
        AppendAsync(key, Encoding.UTF8.GetBytes(PromptLine(key, prompt).ToJsonString() + "\n"), revision, offset);

    /// <summary>Appends <paramref name="content"/>, whole JSONL lines, to the session <paramref name="key"/>, at the given position.</summary>
    public async Task<HttpResponseMessage> AppendAsync(string key, byte[] content, int revision, long offset)
    {
        var machineId = await EnsureMachineAsync();
        var body = new ByteArrayContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await Http.PostAsync(
            $"/v1/transcripts/{Harness}/{key}/content?revision={revision}&offset={offset}&machineId={machineId}", body);
    }

    /// <summary>A Claude Code transcript line in which the user typed <paramref name="prompt"/> in the session <paramref name="key"/>.</summary>
    public JsonObject PromptLine(string key, string prompt) => new()
    {
        ["type"] = "user",
        ["sessionId"] = key,
        ["timestamp"] = "2026-11-19T10:00:00Z",
        ["cwd"] = $"/home/{Login}/src/demo",
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = prompt },
    };

    /// <summary>Backs up one memory file of the folder <paramref name="facts"/> describes, and returns the project it was filed under.</summary>
    public async Task<Guid> PushMemoryAsync(JsonObject facts, string path, string content)
    {
        var machineId = await EnsureMachineAsync();
        var response = await Http.PostAsJsonAsync("/v1/memory", new
        {
            machineId,
            project = facts,
            files = new[] { new { path, content = Encoding.UTF8.GetBytes(content) } },
        });
        Assert.True(response.IsSuccessStatusCode, $"Pushing memory answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["projectId"]!.GetValue<Guid>();
    }

    /// <summary>GETs <paramref name="path"/> and parses the JSON body, asserting success.</summary>
    public async Task<JsonNode> GetJsonAsync(string path)
    {
        var response = await Http.GetAsync(path);
        Assert.True(response.IsSuccessStatusCode, $"GET {path} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!;
    }

    /// <summary>The facts of a git checkout, as the CLI reports them, identified by <paramref name="rootCommit"/>.</summary>
    public static JsonObject GitFolder(string folder, string rootCommit) => new()
    {
        ["workingDirectory"] = folder,
        ["directory"] = folder,
        ["isGit"] = true,
        ["shallow"] = false,
        ["rootCommit"] = rootCommit,
        ["roots"] = new JsonArray(rootCommit),
        ["defaultBranch"] = "refs/remotes/origin/HEAD",
        ["remotes"] = new JsonArray(new JsonObject { ["name"] = "origin", ["url"] = "https://github.com/example/demo.git" }),
        ["topLevel"] = folder,
        ["mainWorktree"] = folder,
        ["subpath"] = "",
    };

    /// <summary>A random full commit hash, so each scenario's repository is its own.</summary>
    public static string NewCommit() => Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + "00000000";
}
