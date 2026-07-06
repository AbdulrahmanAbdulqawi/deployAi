using DeployAI.Providers.Railway;

namespace DeployAI.Tests.Providers;

internal static class RailwayGraphQlTestSupport
{
    internal static string EnhanceResponse(string json) => RailwayGraphQlResponseNormalizer.Normalize(json);
}
