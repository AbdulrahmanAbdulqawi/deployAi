using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeployAI.Api.Services;

/// <summary>One remembered value, and whether it was written to the provider write-only.</summary>
public sealed record StoredEnvVar(string Value, bool IsSecret);

/// <summary>
/// DeployAI's own copy of the environment variables it has set, keyed by the app each one is on.
/// </summary>
/// <remarks>
/// This was a flat map of name to value for the whole project, which is not how the apps are
/// arranged: a split deploy has a website and a server, and both legitimately carry an
/// <c>API_URL</c>. One record served both, so saving on one overwrote the record of the other's
/// value and deleting from one erased the record for both. The live apps stayed correct — the
/// provider is written per target — but the export did not, and the export is the only way back
/// for a generated secret the provider will not read out.
/// <para>
/// Reading tolerates the old flat shape. Those entries land in a bucket that belongs to no app, are
/// still exported, and move to a real target the first time one of them is saved or deleted there —
/// so an existing project converges without a migration that has to guess.
/// </para>
/// </remarks>
public sealed class ProjectEnvironmentStore
{
    /// <summary>Entries from the old project-wide blob, whose app is genuinely unknown.</summary>
    public const string Unplaced = "";

    private const int CurrentVersion = 2;

    private readonly Dictionary<string, Dictionary<string, StoredEnvVar>> _byTarget = new(StringComparer.OrdinalIgnoreCase);

    public static ProjectEnvironmentStore Parse(string? json)
    {
        var store = new ProjectEnvironmentStore();
        if (string.IsNullOrWhiteSpace(json))
        {
            return store;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return store;
            }

            if (document.RootElement.TryGetProperty("version", out _) &&
                document.RootElement.TryGetProperty("targets", out var targets) &&
                targets.ValueKind == JsonValueKind.Object)
            {
                foreach (var target in targets.EnumerateObject())
                {
                    var values = target.Value.Deserialize<Dictionary<string, StoredEnvVar>>();
                    if (values is not null)
                    {
                        store.Bucket(target.Name).AddRangeOverwriting(values);
                    }
                }

                return store;
            }

            // The old shape: name -> value, for the project as a whole.
            var legacy = document.RootElement.Deserialize<Dictionary<string, StoredEnvVar>>();
            if (legacy is not null)
            {
                store.Bucket(Unplaced).AddRangeOverwriting(legacy);
            }
        }
        catch (JsonException)
        {
            // Unreadable blob — an empty store beats failing every request that touches settings.
        }

        return store;
    }

    public string Serialize() =>
        JsonSerializer.Serialize(new Envelope(
            CurrentVersion,
            _byTarget
                .Where(bucket => bucket.Value.Count > 0)
                .ToDictionary(bucket => bucket.Key, bucket => bucket.Value)));

    /// <summary>
    /// Records a value against the app it was written to, and clears any older project-wide copy of
    /// the same name so one save cannot leave two answers behind.
    /// </summary>
    public void Set(string? targetId, string key, StoredEnvVar value)
    {
        var target = Normalize(targetId);
        Bucket(target)[key] = value;

        if (target != Unplaced)
        {
            Bucket(Unplaced).Remove(key);
        }
    }

    /// <summary>Forgets a value on one app, and any unplaced copy it inherited.</summary>
    public void Remove(string? targetId, string key)
    {
        Bucket(Normalize(targetId)).Remove(key);
        Bucket(Unplaced).Remove(key);
    }

    /// <summary>
    /// The value remembered for this app, falling back to an unplaced one. The fallback is what
    /// keeps an existing project's export complete before anything has been re-saved.
    /// </summary>
    public StoredEnvVar? Find(string? targetId, string key)
    {
        if (Bucket(Normalize(targetId)).TryGetValue(key, out var onTarget))
        {
            return onTarget;
        }

        return Bucket(Unplaced).TryGetValue(key, out var unplaced) ? unplaced : null;
    }

    /// <summary>Every name held for any app — used to tell a generated key from a hand-typed one.</summary>
    public IReadOnlySet<string> AllKeys() =>
        _byTarget.Values
            .SelectMany(bucket => bucket.Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Everything held, so a caller can report what the live apps did not account for.</summary>
    public IEnumerable<(string TargetId, string Key, StoredEnvVar Value)> All() =>
        _byTarget.SelectMany(bucket => bucket.Value.Select(entry => (bucket.Key, entry.Key, entry.Value)));

    private Dictionary<string, StoredEnvVar> Bucket(string targetId)
    {
        if (!_byTarget.TryGetValue(targetId, out var bucket))
        {
            bucket = new Dictionary<string, StoredEnvVar>(StringComparer.OrdinalIgnoreCase);
            _byTarget[targetId] = bucket;
        }

        return bucket;
    }

    private static string Normalize(string? targetId) =>
        string.IsNullOrWhiteSpace(targetId) ? Unplaced : targetId.Trim();

    private sealed record Envelope(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("targets")] Dictionary<string, Dictionary<string, StoredEnvVar>> Targets);
}

internal static class StoredEnvVarDictionaryExtensions
{
    public static void AddRangeOverwriting(
        this Dictionary<string, StoredEnvVar> bucket,
        Dictionary<string, StoredEnvVar> values)
    {
        foreach (var (key, value) in values)
        {
            bucket[key] = value;
        }
    }
}
