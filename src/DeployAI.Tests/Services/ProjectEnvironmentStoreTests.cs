using DeployAI.Api.Services;

namespace DeployAI.Tests.Services;

/// <summary>
/// The store was one flat map for a whole project, while the apps it writes to are separate. A split
/// deploy has a website and a server and both legitimately carry API_URL, so one record served two
/// values: saving on one overwrote the other's, deleting from one erased both. The provider stayed
/// right — it is written per target — but DeployAI's copy did not, and that copy is the only way
/// back for a secret the provider will not read out.
/// </summary>
public class ProjectEnvironmentStoreTests
{
    private const string Website = "11111111-1111-1111-1111-111111111111";
    private const string Server = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public void KeepsTheSameNameOnTwoAppsApart()
    {
        // API_URL is on both halves of every split deploy. This is the case the old shape could not
        // represent at all.
        var store = new ProjectEnvironmentStore();
        store.Set(Website, "API_URL", new StoredEnvVar("https://api.example.com", false));
        store.Set(Server, "API_URL", new StoredEnvVar("http://api.internal:8080", false));

        Assert.Equal("https://api.example.com", store.Find(Website, "API_URL")!.Value);
        Assert.Equal("http://api.internal:8080", store.Find(Server, "API_URL")!.Value);
    }

    [Fact]
    public void DeletingFromOneAppLeavesTheOther()
    {
        var store = new ProjectEnvironmentStore();
        store.Set(Website, "API_URL", new StoredEnvVar("website", false));
        store.Set(Server, "API_URL", new StoredEnvVar("server", false));

        store.Remove(Website, "API_URL");

        Assert.Null(store.Find(Website, "API_URL"));
        Assert.Equal("server", store.Find(Server, "API_URL")!.Value);
    }

    [Fact]
    public void SurvivesARoundTrip()
    {
        var store = new ProjectEnvironmentStore();
        store.Set(Server, "Jwt__SigningKey", new StoredEnvVar("s3cret", true));

        var reloaded = ProjectEnvironmentStore.Parse(store.Serialize());

        var value = reloaded.Find(Server, "Jwt__SigningKey")!;
        Assert.Equal("s3cret", value.Value);
        Assert.True(value.IsSecret);
    }

    [Fact]
    public void ReadsTheOldProjectWideBlob()
    {
        // Every existing project has this shape. Losing it would lose the only copy of every
        // generated secret, which is the opposite of what the store is for.
        var store = ProjectEnvironmentStore.Parse(
            """{"Jwt__SigningKey":{"Value":"legacy","IsSecret":true},"API_URL":{"Value":"x","IsSecret":false}}""");

        Assert.Equal("legacy", store.Find(Server, "Jwt__SigningKey")!.Value);
        Assert.Equal("legacy", store.Find(null, "Jwt__SigningKey")!.Value);
        Assert.Contains("API_URL", store.AllKeys());
    }

    [Fact]
    public void MovesALegacyValueOntoTheAppItIsSavedFor()
    {
        // Converges without a migration that would have to guess which app an old entry belonged
        // to: the first save against a real target claims it, and the ambiguous copy goes.
        var store = ProjectEnvironmentStore.Parse("""{"API_URL":{"Value":"old","IsSecret":false}}""");

        store.Set(Website, "API_URL", new StoredEnvVar("new", false));

        Assert.Equal("new", store.Find(Website, "API_URL")!.Value);
        // No longer answers for an app that was never asked about.
        Assert.Null(store.Find(Server, "API_URL"));
        Assert.Single(store.All());
    }

    [Fact]
    public void DeletingAlsoClearsTheAmbiguousCopy()
    {
        // Otherwise removing a legacy variable from the app showing it would leave the old
        // project-wide record behind, and it would reappear in the export as if nothing happened.
        var store = ProjectEnvironmentStore.Parse("""{"API_URL":{"Value":"old","IsSecret":false}}""");

        store.Remove(Website, "API_URL");

        Assert.Null(store.Find(Website, "API_URL"));
        Assert.Empty(store.All());
    }

    [Fact]
    public void TreatsAnUnreadableBlobAsEmptyRatherThanFailing()
    {
        Assert.Empty(ProjectEnvironmentStore.Parse("not json").All());
        Assert.Empty(ProjectEnvironmentStore.Parse(null).All());
    }

    [Fact]
    public void DoesNotWriteEmptyBucketsBack()
    {
        var store = new ProjectEnvironmentStore();
        store.Set(Website, "A", new StoredEnvVar("1", false));
        store.Remove(Website, "A");

        Assert.DoesNotContain(Website, store.Serialize());
    }
}
