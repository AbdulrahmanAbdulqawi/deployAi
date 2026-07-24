using DeployAI.Api.Controllers;

namespace DeployAI.Tests.GitHub;

/// <summary>
/// Locks the suggested-password policy in place. The first live compose deploy
/// crash-looped because the generated ADMIN_PASSWORD was alphanumeric-only and
/// ASP.NET Identity's default policy rejected it at seed time — the container
/// died on startup with "Passwords must have at least one non alphanumeric
/// character" and hit Docker's restart limit.
/// </summary>
public class GeneratedPasswordPolicyTests
{
    // Every character docker compose / .env parsing could mangle must stay out:
    // $ (interpolation), # (comments), quotes, backslash, whitespace.
    private const string AllowedSymbols = "!@^*-_+=?.";

    [Fact]
    public void GeneratePassword_ContainsEveryCharacterClass()
    {
        // Random output — run it enough times that a class-dropping regression
        // cannot slip through as a lucky draw.
        for (var i = 0; i < 50; i++)
        {
            var password = GitHubController.GeneratePassword(20);

            Assert.Equal(20, password.Length);
            Assert.Contains(password, char.IsUpper);
            Assert.Contains(password, char.IsLower);
            Assert.Contains(password, char.IsDigit);
            Assert.Contains(password, c => AllowedSymbols.Contains(c));
        }
    }

    [Fact]
    public void GeneratePassword_UsesOnlyEnvFileSafeCharacters()
    {
        for (var i = 0; i < 50; i++)
        {
            var password = GitHubController.GeneratePassword(20);

            Assert.All(password, c =>
                Assert.True(
                    char.IsAsciiLetterOrDigit(c) || AllowedSymbols.Contains(c),
                    $"Unexpected character '{c}' in generated password."));
        }
    }
}
