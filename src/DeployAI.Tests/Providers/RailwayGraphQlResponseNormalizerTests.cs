using System.Net;
using System.Text;
using DeployAI.Providers.Railway;

namespace DeployAI.Tests.Providers;

public class RailwayGraphQlResponseNormalizerTests
{
    [Fact]
    public void Normalize_StringifiesVariablesObject_EvenWhenErrorsArrayIsEmpty()
    {
        const string raw = """{"data":{"variables":{"API_URL":"https://example.com"}},"errors":[]}""";

        var normalized = RailwayGraphQlResponseNormalizer.Normalize(raw);

        Assert.Contains("\"variables\":\"{", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"variables\":{", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_StringifiesSerializedConfigObject()
    {
        const string raw = """{"data":{"template":{"__typename":"Template","id":"tpl_1","serializedConfig":{"services":[{"name":"Postgres"}]}}}}""";

        var normalized = RailwayGraphQlResponseNormalizer.Normalize(raw);

        Assert.Contains("\"serializedConfig\":\"{", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"serializedConfig\":{", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponseHandler_NormalizesJsonResponsesWithCharsetContentType()
    {
        const string raw = """{"data":{"variables":{"RAILWAY_PRIVATE_DOMAIN":"postgres.railway.internal"}}}""";
        using var handler = new RailwayGraphQlResponseHandler
        {
            InnerHandler = new StubHandler(raw)
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://backboard.railway.com/graphql/v2")
        };

        var response = await client.PostAsync(
            string.Empty,
            new StringContent("""{"operationName":"GetVariables"}""", Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"variables\":\"{", body, StringComparison.Ordinal);
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new StringContent(body, Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8"
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
