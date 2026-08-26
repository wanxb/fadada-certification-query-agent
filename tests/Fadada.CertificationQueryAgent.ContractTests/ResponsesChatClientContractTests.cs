// Exercises the Responses wire contract against an in-memory HTTP handler with no network access.
using System.Net;
using System.Text;
using System.Text.Json;
using Fadada.CertificationQueryAgent.Infrastructure.Ai;
using Microsoft.Extensions.AI;

namespace Fadada.CertificationQueryAgent.ContractTests;

/// <summary>
/// 验证 ResponsesChatClientContractTests 所覆盖的行为、边界条件和安全回归约束。
/// </summary>
public sealed class ResponsesChatClientContractTests
{
    [Fact]
    public async Task NonStreamingRequest_UsesStrictStatelessFunctionTools()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""
            {"output":[{"type":"message","content":[{"type":"output_text","text":"done"}]}],"usage":{"input_tokens":12,"output_tokens":3}}
            """));
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);
        var function = AIFunctionFactory.Create(
            (string mobile) => mobile,
            new AIFunctionFactoryOptions { Name = "query_person", Description = "Query a person." });

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "query")],
            new ChatOptions { Instructions = "system", Tools = [function] });

        Assert.Equal("done", response.Text);
        Assert.Equal(12, response.Usage?.InputTokenCount);
        using var request = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.False(request.RootElement.GetProperty("store").GetBoolean());
        Assert.False(request.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(request.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
        var tool = Assert.Single(request.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("query_person", tool.GetProperty("name").GetString());
        Assert.True(tool.GetProperty("strict").GetBoolean());
    }

    [Fact]
    public async Task StreamingInterface_UsesBufferedResponseCompatibility()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""
            {"output":[{"type":"message","content":[{"type":"output_text","text":"safe answer"}]}],"usage":{"input_tokens":7,"output_tokens":2}}
            """));
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "query")]))
        {
            updates.Add(update);
        }

        Assert.Equal("safe answer", Assert.Single(updates, update => update.Text == "safe answer").Text);
        var usage = Assert.Single(updates.SelectMany(update => update.Contents).OfType<UsageContent>());
        Assert.Equal(7, usage.Details.InputTokenCount);
        using var request = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.False(request.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task HttpFailure_ExposesOnlyStableErrorCode()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("secret provider detail")
        });
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<ResponsesChatClientException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "query")]));

        Assert.Equal("MODEL_HTTP_401", exception.ErrorCode);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TransientGatewayFailure_IsRetriedOnce()
    {
        var attempt = 0;
        var handler = new RecordingHandler(_ => ++attempt == 1
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : JsonResponse("""
                {"output":[{"type":"message","content":[{"type":"output_text","text":"done"}]}]}
                """));
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "query")]);

        Assert.Equal("done", response.Text);
        Assert.Equal(2, handler.RequestBodies.Count);
    }

    [Fact]
    public void OptionsRendering_RedactsApiKey()
    {
        var options = Options();

        Assert.DoesNotContain("test-api-key", options.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", options.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://model.example:8080/")]
    [InlineData("https://model.example/")]
    public void Options_AcceptHttpAndHttpsBaseUrls(string baseUrl)
    {
        var options = new ResponsesChatClientOptions(
            new Uri(baseUrl),
            "test-api-key",
            "test-model",
            TimeSpan.FromSeconds(10));

        options.Validate();
    }

    [Theory]
    [InlineData("ftp://model.example/")]
    [InlineData("file:///model-gateway")]
    public void Options_RejectNonHttpProtocols(string baseUrl)
    {
        var options = new ResponsesChatClientOptions(
            new Uri(baseUrl),
            "test-api-key",
            "test-model",
            TimeSpan.FromSeconds(10));

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Equal("CONFIG_MODEL_INVALID", exception.Message);
    }

    private static ResponsesChatClient CreateClient(HttpClient httpClient) => new(httpClient, Options());

    private static ResponsesChatClientOptions Options() =>
        new(new Uri("https://model.example/"), "test-api-key", "test-model", TimeSpan.FromSeconds(10));

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    /// <summary>
    /// 为所属测试提供可观测且无外部副作用的 RecordingHandler 测试替身。
    /// </summary>
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }
}
