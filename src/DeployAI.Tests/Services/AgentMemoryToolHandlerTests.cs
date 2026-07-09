using System.Text.Json;
using DeployAI.Api.Services;
using DeployAI.Data;
using DeployAI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DeployAI.Tests.Services;

public class AgentMemoryToolHandlerTests
{
    [Fact]
    public async Task CreateThenView_RoundTripsContent()
    {
        await using var db = CreateDb();
        var projectId = Guid.NewGuid();
        var handler = new AgentMemoryToolHandler(db, projectId);

        var createResult = await handler.ExecuteAsync(
            BuildInput(new { command = "create", path = "/memories/notes.md", file_text = "hello" }),
            CancellationToken.None);

        Assert.Contains("notes.md", createResult, StringComparison.Ordinal);

        var viewResult = await handler.ExecuteAsync(
            BuildInput(new { command = "view", path = "/memories/notes.md" }),
            CancellationToken.None);

        Assert.Equal("hello", viewResult);
    }

    [Fact]
    public async Task View_WithoutPath_ListsAllFilesForProject()
    {
        await using var db = CreateDb();
        var projectId = Guid.NewGuid();
        var handler = new AgentMemoryToolHandler(db, projectId);

        await handler.ExecuteAsync(
            BuildInput(new { command = "create", path = "/memories/a.md", file_text = "a" }),
            CancellationToken.None);
        await handler.ExecuteAsync(
            BuildInput(new { command = "create", path = "/memories/b.md", file_text = "b" }),
            CancellationToken.None);

        var listing = await handler.ExecuteAsync(
            BuildInput(new { command = "view" }),
            CancellationToken.None);

        Assert.Contains("/memories/a.md", listing, StringComparison.Ordinal);
        Assert.Contains("/memories/b.md", listing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StrReplace_UpdatesUniqueMatch()
    {
        await using var db = CreateDb();
        var projectId = Guid.NewGuid();
        var handler = new AgentMemoryToolHandler(db, projectId);

        await handler.ExecuteAsync(
            BuildInput(new { command = "create", path = "/memories/notes.md", file_text = "the sky is blue" }),
            CancellationToken.None);

        await handler.ExecuteAsync(
            BuildInput(new { command = "str_replace", path = "/memories/notes.md", old_str = "blue", new_str = "green" }),
            CancellationToken.None);

        var viewResult = await handler.ExecuteAsync(
            BuildInput(new { command = "view", path = "/memories/notes.md" }),
            CancellationToken.None);

        Assert.Equal("the sky is green", viewResult);
    }

    [Fact]
    public async Task StrReplace_FailsWhenOldStrNotUnique()
    {
        await using var db = CreateDb();
        var projectId = Guid.NewGuid();
        var handler = new AgentMemoryToolHandler(db, projectId);

        await handler.ExecuteAsync(
            BuildInput(new { command = "create", path = "/memories/notes.md", file_text = "dup dup" }),
            CancellationToken.None);

        var result = await handler.ExecuteAsync(
            BuildInput(new { command = "str_replace", path = "/memories/notes.md", old_str = "dup", new_str = "x" }),
            CancellationToken.None);

        Assert.Contains("Error", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_RemovesFile()
    {
        await using var db = CreateDb();
        var projectId = Guid.NewGuid();
        var handler = new AgentMemoryToolHandler(db, projectId);

        await handler.ExecuteAsync(
            BuildInput(new { command = "create", path = "/memories/notes.md", file_text = "x" }),
            CancellationToken.None);
        await handler.ExecuteAsync(
            BuildInput(new { command = "delete", path = "/memories/notes.md" }),
            CancellationToken.None);

        var viewResult = await handler.ExecuteAsync(
            BuildInput(new { command = "view", path = "/memories/notes.md" }),
            CancellationToken.None);

        Assert.Contains("Error", viewResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_MovesFileToNewPath()
    {
        await using var db = CreateDb();
        var projectId = Guid.NewGuid();
        var handler = new AgentMemoryToolHandler(db, projectId);

        await handler.ExecuteAsync(
            BuildInput(new { command = "create", path = "/memories/old.md", file_text = "content" }),
            CancellationToken.None);
        await handler.ExecuteAsync(
            BuildInput(new { command = "rename", old_path = "/memories/old.md", new_path = "/memories/new.md" }),
            CancellationToken.None);

        var oldView = await handler.ExecuteAsync(
            BuildInput(new { command = "view", path = "/memories/old.md" }),
            CancellationToken.None);
        var newView = await handler.ExecuteAsync(
            BuildInput(new { command = "view", path = "/memories/new.md" }),
            CancellationToken.None);

        Assert.Contains("Error", oldView, StringComparison.Ordinal);
        Assert.Equal("content", newView);
    }

    [Fact]
    public async Task Create_RejectsPathTraversal()
    {
        await using var db = CreateDb();
        var handler = new AgentMemoryToolHandler(db, Guid.NewGuid());

        var result = await handler.ExecuteAsync(
            BuildInput(new { command = "create", path = "/memories/../secrets.env", file_text = "x" }),
            CancellationToken.None);

        Assert.Contains("Error", result, StringComparison.Ordinal);
        Assert.Empty(await db.AgentMemoryFiles.ToListAsync());
    }

    [Fact]
    public async Task Handler_IsScopedToItsOwnProject()
    {
        await using var db = CreateDb();
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        var handlerA = new AgentMemoryToolHandler(db, projectA);
        var handlerB = new AgentMemoryToolHandler(db, projectB);

        await handlerA.ExecuteAsync(
            BuildInput(new { command = "create", path = "/memories/notes.md", file_text = "project A secret" }),
            CancellationToken.None);

        var viewFromB = await handlerB.ExecuteAsync(
            BuildInput(new { command = "view", path = "/memories/notes.md" }),
            CancellationToken.None);

        Assert.Contains("Error", viewFromB, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppendHistoryAsync_CreatesThenAppendsToHistoryFile()
    {
        await using var db = CreateDb();
        var projectId = Guid.NewGuid();
        var handler = new AgentMemoryToolHandler(db, projectId);

        await handler.AppendHistoryAsync("first note", CancellationToken.None);
        await handler.AppendHistoryAsync("second note", CancellationToken.None);

        var file = await db.AgentMemoryFiles.SingleAsync(f => f.ProjectId == projectId && f.Path == "history.md");
        Assert.Contains("first note", file.Content, StringComparison.Ordinal);
        Assert.Contains("second note", file.Content, StringComparison.Ordinal);
    }

    private static JsonElement BuildInput(object value) =>
        JsonSerializer.SerializeToElement(value);

    private static DeployAIDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new DeployAIDbContext(options);
    }
}
