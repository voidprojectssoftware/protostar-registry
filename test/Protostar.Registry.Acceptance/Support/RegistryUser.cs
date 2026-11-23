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
    /// <param name="project">The facts of the folder whose project the skill belongs to; <c>null</c> for a global skill.</param>
    public async Task<HttpResponseMessage> PushSkillAsync(string name, int? baseVersion = null, JsonObject? project = null)
    {
        var manifest = $"---\nname: {name}\ndescription: An acceptance test skill.\n---\nPush {++_pushes} from {Login}, {Guid.NewGuid()}.\n";
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(manifest));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        form.Add(file, "files", "SKILL.md");
        if (baseVersion is { } based)
            form.Add(new StringContent(based.ToString()), "baseVersion");
        if (project is not null)
            form.Add(new StringContent(new JsonObject { ["machineId"] = await EnsureMachineAsync(), ["facts"] = project.DeepClone() }.ToJsonString()), "project");
        return await Http.PostAsync("/v1/skills", form);
    }

    /// <summary>The file the last <see cref="PushAgentAsync"/> sent, so a scenario can push the same bytes again.</summary>
    public string? LastAgentFile { get; private set; }

    /// <summary>Pushes a custom agent named <paramref name="name"/> whose content differs from every earlier push, as the CLI sends it.</summary>
    /// <param name="project">The facts of the folder whose project the agent belongs to; <c>null</c> for a personal agent.</param>
    /// <param name="path">The file's path relative to the agents folder.</param>
    /// <param name="markdown">The whole file, instead of one generated for <paramref name="name"/>.</param>
    public async Task<HttpResponseMessage> PushAgentAsync(
        string name, int? baseVersion = null, JsonObject? project = null, string? path = null, string? markdown = null)
    {
        markdown ??= $"---\nname: {name}\ndescription: An acceptance test agent.\ntools: Read, Grep\n---\nPush {++_pushes} from {Login}, {Guid.NewGuid()}.\n";
        LastAgentFile = markdown;
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(markdown));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        form.Add(file, "file", path ?? $"{name}.md");
        if (baseVersion is { } based)
            form.Add(new StringContent(based.ToString()), "baseVersion");
        if (project is not null)
            form.Add(new StringContent(new JsonObject { ["machineId"] = await EnsureMachineAsync(), ["facts"] = project.DeepClone() }.ToJsonString()), "project");
        return await Http.PostAsync("/v1/agents", form);
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
    public Task<string> BackUpSessionAsync(string prompt, string? key = null) =>
        BackUpSessionAsync(k => Encoding.UTF8.GetBytes(PromptLine(k, prompt).ToJsonString() + "\n"), key);

    /// <summary>Backs up a new Claude Code session whose JSONL is what <paramref name="content"/> returns for its key, and returns the key.</summary>
    /// <param name="content">The session's JSONL, given its key.</param>
    /// <param name="key">The session's key; a new random UUID when omitted.</param>
    public async Task<string> BackUpSessionAsync(Func<string, byte[]> content, string? key = null)
    {
        key ??= Guid.NewGuid().ToString();
        await BackUpTranscriptAsync(key, content(key));
        return key;
    }

    /// <summary>Registers the transcript <paramref name="key"/> and appends <paramref name="content"/> as its first chunk.</summary>
    /// <param name="key">The transcript's key.</param>
    /// <param name="content">Its JSONL.</param>
    /// <param name="parentKey">For a subagent, the session it ran in; <c>null</c> for a session.</param>
    /// <param name="project">The facts of the folder it ran in, to file it under that folder's project; optional.</param>
    public async Task BackUpTranscriptAsync(string key, byte[] content, string? parentKey = null, JsonObject? project = null)
    {
        var machineId = await EnsureMachineAsync();
        var registered = await Http.PutAsJsonAsync($"/v1/transcripts/{Harness}/{key}", new
        {
            kind = parentKey is null ? "session" : "subagent",
            parentKey,
            machineId,
            sourcePath = $"/home/{Login}/.claude/projects/demo/{key}.jsonl",
            project,
        });
        Assert.True(registered.IsSuccessStatusCode, $"Registering the transcript answered {(int)registered.StatusCode}: {await registered.Content.ReadAsStringAsync()}");
        var position = (await registered.Content.ReadFromJsonAsync<JsonObject>())!;

        var appended = await AppendAsync(key, content, position["revision"]!.GetValue<int>(), position["length"]!.GetValue<long>());
        Assert.True(appended.IsSuccessStatusCode, $"Appending to the transcript answered {(int)appended.StatusCode}: {await appended.Content.ReadAsStringAsync()}");
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

    /// <summary>A Claude Code assistant line in which the model invoked <paramref name="skill"/> with the Skill tool at <paramref name="at"/>.</summary>
    /// <remarks>The <c>args</c> are there so a scenario can check they are never kept.</remarks>
    public static JsonObject SkillCallLine(string key, string skill, DateTimeOffset at, string args = "") => new()
    {
        ["type"] = "assistant",
        ["sessionId"] = key,
        ["timestamp"] = at.ToString("O"),
        ["message"] = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = "toolu_" + Guid.NewGuid().ToString("N"),
                ["name"] = "Skill",
                ["input"] = new JsonObject { ["skill"] = skill, ["args"] = args },
            }),
        },
    };

    /// <summary>A Claude Code assistant line in which the model delegated to the agent type <paramref name="agentType"/> at <paramref name="at"/>.</summary>
    /// <param name="tool"><c>Agent</c>, or <c>Task</c> as older Claude Code versions name it.</param>
    /// <remarks>The <c>prompt</c> is there so a scenario can check it is never kept.</remarks>
    public static JsonObject AgentCallLine(string key, string agentType, DateTimeOffset at, string prompt = "", string tool = "Agent") => new()
    {
        ["type"] = "assistant",
        ["sessionId"] = key,
        ["timestamp"] = at.ToString("O"),
        ["message"] = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = "toolu_" + Guid.NewGuid().ToString("N"),
                ["name"] = tool,
                ["input"] = new JsonObject { ["subagent_type"] = agentType, ["description"] = "Delegate", ["prompt"] = prompt },
            }),
        },
    };

    /// <summary>The JSONL of <paramref name="lines"/>, one per line.</summary>
    public static byte[] Jsonl(params JsonObject[] lines) =>
        Encoding.UTF8.GetBytes(string.Concat(lines.Select(line => line.ToJsonString() + "\n")));

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

    /// <summary>The facts of a folder outside git, as the CLI reports them: a project of its own, keyed by machine and path.</summary>
    public static JsonObject PlainFolder(string folder) => new()
    {
        ["workingDirectory"] = folder,
        ["directory"] = folder,
        ["isGit"] = false,
        ["shallow"] = false,
    };

    /// <summary>A random full commit hash, so each scenario's repository is its own.</summary>
    public static string NewCommit() => Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + "00000000";
}
