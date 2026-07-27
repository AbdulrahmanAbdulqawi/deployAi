using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeployAI.Api.Services;

/// <summary>Data an OAuth state value carries through the redirect round-trip (which user initiated it, where to send them back).</summary>
public sealed record OAuthStatePayload(Guid? UserId = null, string? ReturnUrl = null);

/// <summary>Anti-CSRF state values for OAuth flows (GitHub login, Railway/Vercel connect) - one-time-use, expiring tokens tying a callback back to who started the flow.</summary>
public interface IOAuthStateStore
{
    /// <summary>Creates a state value with no payload (for flows that don't need to carry data through).</summary>
    string CreateState();
    /// <summary>Creates a state value carrying the given payload.</summary>
    string CreateState(OAuthStatePayload payload);
    /// <summary>Validates and consumes (single-use) a state value, discarding its payload.</summary>
    bool ValidateAndConsume(string state);
    /// <summary>Validates and consumes a state value, returning its payload if valid.</summary>
    bool TryValidateAndConsume(string state, out OAuthStatePayload? payload);
}

/// <summary>In-process state store (10-minute expiry) - fine for a single-instance deployment; would need a shared backing store if DeployAI ever scales to multiple API instances.</summary>
public sealed class InMemoryOAuthStateStore : IOAuthStateStore
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset ExpiresAt, OAuthStatePayload? Payload)> _states = new();
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public string CreateState() => CreateState(new OAuthStatePayload());

    public string CreateState(OAuthStatePayload payload)
    {
        Cleanup();
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _states[state] = (DateTimeOffset.UtcNow.Add(Lifetime), payload);
        return state;
    }

    public bool ValidateAndConsume(string state)
    {
        return TryValidateAndConsume(state, out _);
    }

    public bool TryValidateAndConsume(string state, out OAuthStatePayload? payload)
    {
        Cleanup();
        payload = null;
        if (!_states.TryRemove(state, out var entry))
        {
            return false;
        }

        if (entry.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return false;
        }

        payload = entry.Payload ?? new OAuthStatePayload();
        return true;
    }

    private void Cleanup()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _states)
        {
            if (entry.Value.ExpiresAt < now)
            {
                _states.TryRemove(entry.Key, out _);
            }
        }
    }
}
