using DeployAI.Api.Services;
using DeployAI.Core.Exceptions;

namespace DeployAI.Tests.Services;

public class AnthropicJsonFileParserTests
{
    [Fact]
    public void ParseFilesResponse_ParsesMarkdownFencedJson()
    {
        const string response = """
            Here is the fix:

            ```json
            {
              "files": [
                { "path": "src/app/app.component.ts", "content": "export class App {}" }
              ]
            }
            ```
            """;

        var files = AnthropicJsonFileParser.ParseFilesResponse(response, _ => true);

        Assert.Single(files);
        Assert.Equal("src/app/app.component.ts", files[0].Path);
        Assert.Equal("export class App {}", files[0].Content);
    }

    [Fact]
    public void ParseFilesResponse_ParsesJsonWithBracesInFileContent()
    {
        const string response = """
            {"files":[{"path":"src/util.ts","content":"export function run() {\n  return { ok: true };\n}"}]}
            ```
            """;

        var files = AnthropicJsonFileParser.ParseFilesResponse(response, _ => true);

        Assert.Single(files);
        Assert.Contains("return { ok: true }", files[0].Content);
    }

    [Fact]
    public void ParseFilesResponse_IgnoresExampleJsonBeforeActualPayload()
    {
        const string response = """
            Return format: {"files":[]}

            ```json
            {"files":[{"path":"vercel.json","content":"{}"}]}
            ```
            """;

        var files = AnthropicJsonFileParser.ParseFilesResponse(response, _ => true);

        Assert.Single(files);
        Assert.Equal("vercel.json", files[0].Path);
    }

    [Fact]
    public void ParseFilesResponse_ThrowsDeployAIExceptionForInvalidJson()
    {
        const string response = "This is not JSON at all.";

        var ex = Assert.Throws<DeployAIException>(() =>
            AnthropicJsonFileParser.ParseFilesResponse(response, _ => true));

        Assert.Equal("fix_generation_failed", ex.ErrorCode);
    }

    [Fact]
    public void ParseFilesResponse_FiltersDisallowedPaths()
    {
        const string response = """
            {"files":[
              {"path":"../secrets.env","content":"nope"},
              {"path":"src/app/app.component.ts","content":"ok"}
            ]}
            """;

        var files = AnthropicJsonFileParser.ParseFilesResponse(response, path =>
            path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase));

        Assert.Single(files);
        Assert.Equal("src/app/app.component.ts", files[0].Path);
    }

    [Fact]
    public void NormalizeFilesSubmission_ReturnsRawJsonForFilesPayload()
    {
        const string json = """{"files":[{"path":"vercel.json","content":"{}"}]}""";

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var normalized = AnthropicJsonFileParser.NormalizeFilesSubmission(document.RootElement);

        Assert.Equal(json, normalized);
    }

    [Fact]
    public void ParseFilesResponse_UsesCustomErrorMessage()
    {
        const string response = "not json";

        var ex = Assert.Throws<DeployAIException>(() =>
            AnthropicJsonFileParser.ParseFilesResponse(
                response,
                _ => true,
                "setup_generation_failed",
                "Try generating the setup again."));

        Assert.Equal("setup_generation_failed", ex.ErrorCode);
        Assert.Contains("setup again", ex.Message);
    }
}
