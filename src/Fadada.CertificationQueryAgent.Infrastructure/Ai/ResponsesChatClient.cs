// Bridges the approved Responses-compatible API to IChatClient without exposing credentials in application output.
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Fadada.CertificationQueryAgent.Infrastructure.Ai;

/// <summary>
/// 定义模型网关连接、模型名和重试参数，并在启动阶段校验安全边界。
/// </summary>
public sealed record ResponsesChatClientOptions(
    Uri BaseUri,
    string ApiKey,
    string Model,
    TimeSpan RequestTimeout,
    int MaximumRetries = 1)
{
    public void Validate()
    {
        if (!BaseUri.IsAbsoluteUri ||
            (BaseUri.Scheme != Uri.UriSchemeHttp && BaseUri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(ApiKey) || string.IsNullOrWhiteSpace(Model) ||
            Model.Length > 128 || RequestTimeout < TimeSpan.FromSeconds(1) || RequestTimeout > TimeSpan.FromMinutes(5) ||
            MaximumRetries is < 0 or > 2)
        {
            throw new InvalidOperationException("CONFIG_MODEL_INVALID");
        }
    }

    public override string ToString() =>
        $"ResponsesChatClientOptions {{ BaseUri = {BaseUri}, ApiKey = [REDACTED], Model = {Model}, RequestTimeout = {RequestTimeout} }}";
}

/// <summary>
/// 将 Microsoft.Extensions.AI 请求适配到 Responses 网关，并限定超时、重试和响应解析。
/// </summary>
public sealed class ResponsesChatClient : IChatClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;
    private readonly ResponsesChatClientOptions options;

    public ResponsesChatClient(HttpClient httpClient, ResponsesChatClientOptions options)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.options.Validate();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata)
            ? new ChatClientMetadata("responses-compatible", options.BaseUri, options.Model)
            : serviceType.IsInstanceOfType(this) ? this : null;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(messages, chatOptions, stream: false, cancellationToken).ConfigureAwait(false);
        var payload = await ReadSuccessPayloadAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseResponse(payload);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? chatOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The approved gateway's SSE behavior is not stable across model routes. The Agent surface still
        // exposes streaming updates, but obtains one complete stateless response before yielding content.
        using var response = await SendAsync(messages, chatOptions, stream: false, cancellationToken).ConfigureAwait(false);
        var payload = await ReadSuccessPayloadAsync(response, cancellationToken).ConfigureAwait(false);
        var parsed = ParseResponse(payload);
        foreach (var content in parsed.Messages.SelectMany(message => message.Contents))
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [content]);
        }

        if (parsed.Usage is not null)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(parsed.Usage)]);
        }
    }

    public void Dispose()
    {
    }

    private async Task<HttpResponseMessage> SendAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? chatOptions,
        bool stream,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        var requestBody = JsonSerializer.Serialize(CreateRequest(messages, chatOptions, stream), JsonOptions);
        for (var attempt = 0; attempt <= options.MaximumRetries; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.BaseUri, "v1/responses"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
            try
            {
                var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false);
                if (attempt < options.MaximumRetries && IsRetryable(response.StatusCode))
                {
                    response.Dispose();
                    continue;
                }

                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ResponsesChatClientException("MODEL_TIMEOUT");
            }
            catch (HttpRequestException)
            {
                throw new ResponsesChatClientException("MODEL_TRANSPORT_ERROR");
            }
        }

        throw new InvalidOperationException("Model retry loop terminated unexpectedly.");
    }

    private static bool IsRetryable(System.Net.HttpStatusCode statusCode) => statusCode is
        System.Net.HttpStatusCode.BadGateway or
        System.Net.HttpStatusCode.ServiceUnavailable or
        System.Net.HttpStatusCode.GatewayTimeout;

    private object CreateRequest(IEnumerable<ChatMessage> messages, ChatOptions? chatOptions, bool stream) => new
    {
        model = options.Model,
        instructions = chatOptions?.Instructions,
        input = messages.SelectMany(ToInputItems).ToArray(),
        tools = chatOptions?.Tools?.OfType<AIFunction>().Select(function => new
        {
            type = "function",
            name = function.Name,
            description = function.Description,
            parameters = function.JsonSchema,
            strict = true
        }).ToArray(),
        tool_choice = "auto",
        parallel_tool_calls = false,
        store = false,
        stream
    };

    private static IEnumerable<object> ToInputItems(ChatMessage message)
    {
        if (!string.IsNullOrEmpty(message.Text))
        {
            yield return new { role = message.Role.Value, content = message.Text };
        }

        foreach (var content in message.Contents)
        {
            if (content is FunctionCallContent call)
            {
                yield return new
                {
                    type = "function_call",
                    call_id = call.CallId,
                    name = call.Name,
                    arguments = JsonSerializer.Serialize(call.Arguments, JsonOptions)
                };
            }
            else if (content is FunctionResultContent result)
            {
                yield return new
                {
                    type = "function_call_output",
                    call_id = result.CallId,
                    output = result.Result?.ToString() ?? string.Empty
                };
            }
        }
    }

    private static async Task<JsonElement> ReadSuccessPayloadAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new ResponsesChatClientException($"MODEL_HTTP_{(int)response.StatusCode}");
        }

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    private static ChatResponse ParseResponse(JsonElement root)
    {
        var contents = new List<AIContent>();
        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                if (type == "function_call")
                {
                    var callId = item.GetProperty("call_id").GetString() ?? throw new ResponsesChatClientException("MODEL_FUNCTION_CALL_INVALID");
                    var name = item.GetProperty("name").GetString() ?? throw new ResponsesChatClientException("MODEL_FUNCTION_CALL_INVALID");
                    var argumentsJson = item.GetProperty("arguments").GetString() ?? "{}";
                    var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson, JsonOptions) ?? [];
                    contents.Add(new FunctionCallContent(callId, name, arguments!));
                }
                else if (type == "message" && item.TryGetProperty("content", out var contentItems))
                {
                    foreach (var content in contentItems.EnumerateArray())
                    {
                        if (content.TryGetProperty("type", out var contentType) && contentType.GetString() == "output_text" &&
                            content.TryGetProperty("text", out var text))
                        {
                            contents.Add(new TextContent(text.GetString() ?? string.Empty));
                        }
                    }
                }
            }
        }

        if (contents.Count == 0)
        {
            throw new ResponsesChatClientException("MODEL_OUTPUT_MISSING");
        }

        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, contents));
        if (root.TryGetProperty("usage", out var usage))
        {
            response.Usage = new UsageDetails
            {
                InputTokenCount = usage.TryGetProperty("input_tokens", out var input) ? input.GetInt64() : 0,
                OutputTokenCount = usage.TryGetProperty("output_tokens", out var outputTokens) ? outputTokens.GetInt64() : 0
            };
        }

        return response;
    }
}

/// <summary>
/// 表示模型网关适配器的可分类失败，避免向上层泄露原始响应或凭据。
/// </summary>
public sealed class ResponsesChatClientException(string errorCode) : Exception(errorCode)
{
    public string ErrorCode { get; } = errorCode;
}
