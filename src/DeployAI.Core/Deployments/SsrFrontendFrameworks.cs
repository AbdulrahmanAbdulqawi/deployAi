namespace DeployAI.Core.Deployments;

/// <summary>
/// Frontend frameworks that compile environment values into the built bundle instead of reading
/// them at runtime: Next.js inlines <c>NEXT_PUBLIC_*</c>, Vite <c>VITE_*</c>, Angular swaps its
/// environment files. Two consequences follow, and both are silent failures, which is why this
/// list is shared rather than restated per call site:
/// <list type="bullet">
/// <item>The values must be present when the build command runs, not just at runtime.</item>
/// <item>A cached build serves the previous values, so a config change needs a fresh build.</item>
/// </list>
/// </summary>
public static class SsrFrontendFrameworks
{
    private static readonly HashSet<string> Frameworks = new(StringComparer.OrdinalIgnoreCase)
    {
        "next", "nextjs", "nuxt", "sveltekit", "remix",
        "vite", "react", "vue", "svelte", "astro", "angular"
    };

    public static bool Inlines(string? framework) =>
        !string.IsNullOrWhiteSpace(framework) && Frameworks.Contains(framework.Trim());
}
