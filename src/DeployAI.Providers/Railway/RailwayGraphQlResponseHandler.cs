using System.Text;

namespace DeployAI.Providers.Railway;

/// <summary>
/// Rewrites every JSON-looking response body through <see cref="RailwayGraphQlResponseNormalizer"/>
/// before StrawberryShake deserializes it, working around shape inconsistencies in Railway's raw
/// GraphQL responses that the generated client can't tolerate directly.
/// </summary>
internal sealed class RailwayGraphQlResponseHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            var requestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode || response.Content is null)
        {
            return response;
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var looksLikeJson = body.AsSpan().TrimStart().StartsWith("{");
        var shouldNormalize = looksLikeJson ||
            (mediaType is not null &&
             mediaType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase));

        if (!shouldNormalize)
        {
            response.Content = new StringContent(body, Encoding.UTF8);
            return response;
        }

        var normalized = RailwayGraphQlResponseNormalizer.Normalize(body);
        response.Content = new StringContent(normalized, Encoding.UTF8, "application/json");
        return response;
    }
}
