using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeployAI.Api.Services;

public sealed record OAuthStatePayload(Guid? UserId = null, string? ReturnUrl = null);

public interface IOAuthStateStore
{
    string CreateState();
    string CreateState(OAuthStatePayload payload);
    bool ValidateAndConsume(string state);
    bool TryValidateAndConsume(string state, out OAuthStatePayload? payload);
}

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
