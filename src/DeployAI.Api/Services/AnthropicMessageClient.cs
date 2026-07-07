using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeployAI.Core.Exceptions;
using DeployAI.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace DeployAI.Api.Services;

internal static class AnthropicJsonFileParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    internal static IReadOnlyList<(string Path, string Content)> ParseFilesResponse(
        string text,
        Func<string, bool> isAllowedPath,
        string parseErrorCode = "fix_generation_failed",
        string parseErrorMessage = "Claude returned a response that could not be parsed as JSON. Try generating again.")
    {
        var extracted = ExtractJson(text);

        GeneratedFilesPayload? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<GeneratedFilesPayload>(extracted, JsonOptions);
        }
        catch (JsonException)
        {
            throw new DeployAIException(parseErrorCode, parseErrorMessage);
        }

        if (parsed?.Files is null || parsed.Files.Count == 0)
        {
            return [];
        }

        return parsed.Files
            .Where(file => isAllowedPath(file.Path))
            .Select(file => (file.Path, file.Content))
            .ToArray();
    }

    internal static IReadOnlyList<(string Path, string Content)> ParseFilesFromToolInput(JsonElement input)
    {
        if (!input.TryGetProperty("files", out var filesElement) ||
            filesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var files = new List<(string Path, string Content)>();
        foreach (var fileElement in filesElement.EnumerateArray())
        {
            if (!fileElement.TryGetProperty("path", out var pathElement) ||
                !fileElement.TryGetProperty("content", out var contentElement))
            {
                continue;
            }

            var path = pathElement.GetString();
            var content = contentElement.GetString();
            if (string.IsNullOrWhiteSpace(path) || content is null)
            {
                continue;
            }

            files.Add((path, content));
        }

        return files;
    }

    internal static string NormalizeFilesSubmission(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("files", out _))
        {
            return input.GetRawText();
        }

        throw new DeployAIException(
            "setup_generation_failed",
            "Claude submitted files in an invalid format.");
    }

    internal static string ExtractJson(string text)
    {
        foreach (var block in ExtractMarkdownJsonBlocks(text).Reverse())
        {
            var candidate = TryExtractFilesPayload(block);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        var stripped = StripMarkdownCodeFences(text.Trim());
        var fromStripped = TryExtractFilesPayload(stripped);
        if (fromStripped is not null)
        {
            return fromStripped;
        }

        return stripped;
    }

    private static string? TryExtractFilesPayload(string text)
    {
        var searchEnd = text.Length;
        while (searchEnd > 0)
        {
            var filesKeyIndex = text.LastIndexOf("\"files\"", searchEnd - 1, StringComparison.Ordinal);
            if (filesKeyIndex < 0)
            {
                break;
            }

            var objectStart = text.LastIndexOf('{', filesKeyIndex);
            if (objectStart >= 0)
            {
                var balanced = ExtractBalancedJsonObject(text, objectStart);
                if (balanced is not null && LooksLikeFilesPayload(balanced))
                {
                    return balanced;
                }
            }

            searchEnd = filesKeyIndex;
        }

        var firstBrace = text.IndexOf('{');
        if (firstBrace >= 0)
        {
            var balanced = ExtractBalancedJsonObject(text, firstBrace);
            if (balanced is not null && LooksLikeFilesPayload(balanced))
            {
                return balanced;
            }
        }

        return null;
    }

    private static IEnumerable<string> ExtractMarkdownJsonBlocks(string text)
    {
        const string fence = "```json";
        var index = 0;
        while (index < text.Length)
        {
            var start = text.IndexOf(fence, index, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                yield break;
            }

            var contentStart = text.IndexOf('\n', start);
            if (contentStart < 0)
            {
                yield break;
            }

            contentStart++;
            var end = text.IndexOf("```", contentStart, StringComparison.Ordinal);
            if (end < 0)
            {
                yield break;
            }

            yield return text[contentStart..end].Trim();
            index = end + 3;
        }
    }

    private static bool LooksLikeFilesPayload(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("files", out var files) &&
                   files.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string StripMarkdownCodeFences(string text)
    {
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var lineEnd = text.IndexOf('\n');
            if (lineEnd >= 0)
            {
                text = text[(lineEnd + 1)..].TrimStart();
            }
        }

        text = text.TrimEnd();
        if (text.EndsWith("```", StringComparison.Ordinal))
        {
            var newlineBeforeFence = text.LastIndexOf("\n```", StringComparison.Ordinal);
            text = newlineBeforeFence >= 0 ? text[..newlineBeforeFence] : text[..^3];
        }

        return text.Trim();
    }

    private static string? ExtractBalancedJsonObject(string text, int start)
    {
        if (start < 0 || start >= text.Length || text[start] != '{')
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escape = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escape)
                {
                    escape = false;
                }
                else if (c == '\\')
                {
                    escape = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return text[start..(i + 1)];
                    }

                    break;
            }
        }

        return null;
    }

    private sealed class GeneratedFilesPayload
    {
        [JsonPropertyName("files")]
        public List<GeneratedFileEntry>? Files { get; set; }
    }

    private sealed class GeneratedFileEntry
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }
}

public sealed class AnthropicMessageClient
{
    private readonly HttpClient _httpClient;
    private readonly AnthropicOptions _options;

    public AnthropicMessageClient(HttpClient httpClient, IOptions<AnthropicOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public int FixAgentMaxTurns => _options.FixAgentMaxTurns;

    public int FixMaxToolCalls => _options.FixMaxToolCalls;

    public int FixMaxCommands => _options.FixMaxCommands;

    public int FixForceSubmitAfterToolCalls => _options.FixForceSubmitAfterToolCalls;

    public int SetupAgentMaxTurns => _options.SetupAgentMaxTurns;

    public int SetupMaxToolCalls => _options.SetupMaxToolCalls;

    public int SetupForceSubmitAfterToolCalls => _options.SetupForceSubmitAfterToolCalls;

    public async Task<string?> SendUserMessageAsync(string prompt, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        var responseBody = await PostMessagesAsync(
            new List<object> { new { role = "user", content = prompt } },
            tools: null,
            cancellationToken);
        var payload = JsonSerializer.Deserialize<AnthropicMessageResponse>(responseBody);
        return payload?.Content?.FirstOrDefault(block => block.Type == "text")?.Text;
    }

    public async Task<string> RunAgentWithToolsAsync(
        string userPrompt,
        IReadOnlyList<object> tools,
        Func<string, JsonElement, CancellationToken, Task<string>> toolHandler,
        int maxTurns,
        CancellationToken cancellationToken,
        Func<string, Task>? reportActivity = null,
        string? completionToolName = null,
        int? forceSubmitAfterToolCalls = null,
        Func<bool>? canProactiveSubmit = null)
    {
        if (!IsConfigured)
        {
            throw new DeployAIException(
                "claude_not_configured",
                "Anthropic API key is not configured. Add Anthropic:ApiKey to fix build errors with Claude.");
        }

        var messages = new List<object> { new { role = "user", content = userPrompt } };
        var forceCompletionTool = false;
        var explorationToolCalls = 0;
        var proactiveSubmitRequested = false;
        var recoveryAttempts = 0;

        try
        {
            for (var turn = 0; turn < maxTurns; turn++)
            {
                if (!string.IsNullOrEmpty(completionToolName) && !forceCompletionTool)
                {
                    var exploredEnough = forceSubmitAfterToolCalls is > 0 &&
                                         explorationToolCalls >= forceSubmitAfterToolCalls.Value;
                    var approachingTurnLimit = turn >= maxTurns - 2;
                    if ((exploredEnough || approachingTurnLimit) && !proactiveSubmitRequested &&
                        (canProactiveSubmit?.Invoke() ?? true))
                    {
                        proactiveSubmitRequested = true;
                        forceCompletionTool = true;

                        await ReportActivityAsync(
                            reportActivity,
                            "Exploration budget reached — requesting file submission via tool…");
                        messages.Add(new
                        {
                            role = "user",
                            content = BuildProactiveSubmitPrompt(completionToolName)
                        });
                    }
                }

                await ReportActivityAsync(
                    reportActivity,
                    $"Turn {turn + 1}/{maxTurns}: waiting for Claude…");
                var responseBody = await PostMessagesAsync(
                    messages,
                    tools,
                    cancellationToken,
                    forceCompletionTool ? completionToolName : null);
                forceCompletionTool = false;
                using var document = JsonDocument.Parse(responseBody);
                var root = document.RootElement;
                var stopReason = root.TryGetProperty("stop_reason", out var stopReasonElement)
                    ? stopReasonElement.GetString()
                    : null;

                if (!root.TryGetProperty("content", out var contentElement) ||
                    contentElement.ValueKind != JsonValueKind.Array ||
                    contentElement.GetArrayLength() == 0)
                {
                    throw new DeployAIException("fix_generation_failed", "Claude returned an empty response.");
                }

                messages.Add(new
                {
                    role = "assistant",
                    content = JsonSerializer.Deserialize<object>(contentElement.GetRawText())
                });

                if (!string.Equals(stopReason, "tool_use", StringComparison.Ordinal))
                {
                    var text = ExtractTextContent(contentElement);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        throw new DeployAIException("fix_generation_failed", "Claude did not return a fix for this build error.");
                    }

                    if (!string.IsNullOrEmpty(completionToolName))
                    {
                        if (TryExtractFilesPayloadJson(text, out var payloadJson))
                        {
                            return payloadJson;
                        }

                        if (turn + 1 < maxTurns && recoveryAttempts < 1)
                        {
                            recoveryAttempts++;

                            await ReportActivityAsync(
                                reportActivity,
                                "Claude replied with text — requesting file submission via tool…");
                            messages.Add(new
                            {
                                role = "user",
                                content = BuildCompletionToolRecoveryPrompt(completionToolName, stopReason)
                            });
                            forceCompletionTool = true;
                            continue;
                        }

                        throw new DeployAIException(
                            "setup_generation_failed",
                            "Claude did not submit deployment files via the submit_deployment_files tool. Try generating the setup again.");
                    }

                    await ReportActivityAsync(reportActivity, "Claude returned the final response.");
                    return text;
                }

                var toolResults = new List<object>();
                var toolCallCount = 0;
                JsonElement? completionInput = null;
                foreach (var block in contentElement.EnumerateArray())
                {
                    if (!block.TryGetProperty("type", out var typeElement) ||
                        !string.Equals(typeElement.GetString(), "tool_use", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var toolUseId = block.GetProperty("id").GetString()
                        ?? throw new DeployAIException("fix_generation_failed", "Claude returned a malformed tool request.");
                    var toolName = block.GetProperty("name").GetString()
                        ?? throw new DeployAIException("fix_generation_failed", "Claude returned a malformed tool request.");
                    var toolInput = block.GetProperty("input");

                    if (!string.IsNullOrEmpty(completionToolName) &&
                        string.Equals(toolName, completionToolName, StringComparison.Ordinal))
                    {
                        completionInput = toolInput;
                        break;
                    }

                    toolCallCount++;
                    await ReportActivityAsync(
                        reportActivity,
                        ClaudeDeploymentAgentTools.DescribeToolCall(toolName, toolInput));

                    var result = await toolHandler(toolName, toolInput, cancellationToken);
                    toolResults.Add(new
                    {
                        type = "tool_result",
                        tool_use_id = toolUseId,
                        content = result
                    });
                }

                if (completionInput is { } submittedFiles)
                {
                    await ReportActivityAsync(reportActivity, "Claude submitted generated files.");
                    return AnthropicJsonFileParser.NormalizeFilesSubmission(submittedFiles);
                }

                if (toolResults.Count == 0)
                {
                    throw new DeployAIException("fix_generation_failed", "Claude requested tools but none were executed.");
                }

                await ReportActivityAsync(
                    reportActivity,
                    toolCallCount == 1
                        ? "Sent tool result back to Claude."
                        : $"Sent {toolCallCount} tool results back to Claude.");

                explorationToolCalls += toolCallCount;

                messages.Add(new { role = "user", content = toolResults.ToArray() });
            }

            throw new DeployAIException(
                "fix_generation_failed",
                "Claude exceeded the maximum number of repository exploration steps.");
        }
        catch (OperationCanceledException)
        {
            throw CreateClaudeTimeoutException();
        }
    }

    private async Task<string> PostMessagesAsync(
        List<object> messages,
        IReadOnlyList<object>? tools,
        CancellationToken cancellationToken,
        string? forceToolName = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        request.Headers.Add("x-api-key", _options.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        var payload = new Dictionary<string, object?>
        {
            ["model"] = _options.Model,
            ["max_tokens"] = Math.Max(1024, _options.MaxOutputTokens),
            ["messages"] = messages
        };

        if (tools is { Count: > 0 })
        {
            payload["tools"] = tools;
        }

        if (!string.IsNullOrEmpty(forceToolName))
        {
            payload["tool_choice"] = new { type = "tool", name = forceToolName };
        }

        request.Content = JsonContent.Create(payload);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw CreateClaudeTimeoutException();
        }

        using (response)
        {
            string responseBody;
            try
            {
                responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw CreateClaudeTimeoutException();
            }

            if (!response.IsSuccessStatusCode)
            {
                var apiError = TryParseAnthropicError(responseBody);
                throw new DeployAIException(
                    "anthropic_api_error",
                    apiError ?? $"Anthropic API returned HTTP {(int)response.StatusCode}. Check Anthropic:Model in config.");
            }

            return responseBody;
        }
    }

    private static DeployAIException CreateClaudeTimeoutException() =>
        new(
            "claude_request_timeout",
            "Claude took too long to respond or the request was canceled. Try again — large repositories can take several minutes.");

    private static Task ReportActivityAsync(Func<string, Task>? reportActivity, string message) =>
        reportActivity is null ? Task.CompletedTask : reportActivity(message);

    private static string ExtractTextContent(JsonElement contentElement)
    {
        var builder = new StringBuilder();
        foreach (var block in contentElement.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var typeElement) &&
                string.Equals(typeElement.GetString(), "text", StringComparison.Ordinal) &&
                block.TryGetProperty("text", out var textElement))
            {
                builder.Append(textElement.GetString());
            }
        }

        return builder.ToString();
    }

    private static bool TryExtractFilesPayloadJson(string text, out string payloadJson)
    {
        payloadJson = string.Empty;
        try
        {
            var extracted = AnthropicJsonFileParser.ExtractJson(text);
            using var document = JsonDocument.Parse(extracted);
            if (!document.RootElement.TryGetProperty("files", out var files) ||
                files.ValueKind != JsonValueKind.Array ||
                files.GetArrayLength() == 0)
            {
                return false;
            }

            payloadJson = extracted;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string BuildProactiveSubmitPrompt(string toolName) =>
        $"You have explored enough of the repository. Call {toolName} now with every file you prepared. " +
        "Use the tool only — no prose, no markdown, no further GitHub exploration.";

    private static string BuildCompletionToolRecoveryPrompt(string toolName, string? stopReason) =>
        string.Equals(stopReason, "max_tokens", StringComparison.Ordinal)
            ? $"Your previous response was truncated. Call {toolName} immediately with every file you prepared. " +
              "Use the tool only — no prose, no markdown, no plain-text JSON."
            : $"Do not write prose or markdown. Call {toolName} now with every file you prepared. " +
              "Use the tool only — no plain-text JSON.";

    private static string? TryParseAnthropicError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message))
            {
                return message.GetString();
            }
        }
        catch
        {
            // ignore parse failures
        }

        return null;
    }

    private sealed class AnthropicMessageResponse
    {
        [JsonPropertyName("content")]
        public List<AnthropicContentBlock>? Content { get; set; }
    }

    private sealed class AnthropicContentBlock
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
