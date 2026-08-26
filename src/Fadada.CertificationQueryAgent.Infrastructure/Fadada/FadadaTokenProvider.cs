// Serializes token refreshes and keeps token material inside the provider adapter.
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fadada.CertificationQueryAgent.Application.Auditing;
using Fadada.CertificationQueryAgent.Application.DomainTools;
using Fadada.CertificationQueryAgent.Infrastructure.Security;

namespace Fadada.CertificationQueryAgent.Infrastructure.Fadada;

/// <summary>
/// 并发安全地缓存和刷新法大大访问令牌，且不把令牌写入日志或业务结果。
/// </summary>
internal sealed class FadadaTokenProvider(
    HttpClient httpClient,
    FadadaOptions options,
    IAuditStore auditStore,
    CredentialScrubber credentialScrubber,
    TimeProvider? timeProvider = null) : IFadadaTokenProvider, IDisposable
{
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private CachedToken? cachedToken;

    public async ValueTask<string> GetAsync(
        DomainQueryContext context,
        CancellationToken cancellationToken)
    {
        options.Validate();
        if (IsUsable(cachedToken))
        {
            return cachedToken!.Value;
        }

        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (IsUsable(cachedToken))
            {
                return cachedToken!.Value;
            }

            cachedToken = await RefreshAsync(context, cancellationToken);
            return cachedToken.Value;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    public void Dispose() => refreshLock.Dispose();

    private bool IsUsable(CachedToken? token) =>
        token is not null && token.ExpiresAtUtc - options.TokenRefreshSkew > clock.GetUtcNow();

    private async Task<CachedToken> RefreshAsync(
        DomainQueryContext context,
        CancellationToken cancellationToken)
    {
        var endpoint = FadadaEndpointCatalog.Get(FadadaEndpointKey.AccessToken);
        var audit = await ExternalAuditScope.StartAsync(auditStore, context, endpoint, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);

        try
        {
            var now = clock.GetLocalNow();
            var timestamp = now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            var signature = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(timestamp + options.AppSecret)));
            using var request = new HttpRequestMessage(
                endpoint.Method,
                new Uri(options.BaseUri, endpoint.RelativePath))
            {
                Content = JsonContent.Create(new AccessTokenRequest(options.AppId, timestamp, signature))
            };
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode || !TryReadToken(body, out var token, out var expiresIn))
            {
                await audit.CompleteAsync(AuditOperationStatus.Failed, "FDD_TOKEN_RESPONSE_INVALID", cancellationToken);
                throw new FadadaIntegrationException("FDD_TOKEN_RESPONSE_INVALID");
            }

            await audit.CompleteAsync(AuditOperationStatus.Succeeded, null, cancellationToken);
            return new CachedToken(token!, clock.GetUtcNow().AddSeconds(Math.Max(expiresIn, 60)));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await audit.CompleteAsync(AuditOperationStatus.Failed, "FDD_TOKEN_TIMEOUT", cancellationToken);
            throw new FadadaIntegrationException("FDD_TOKEN_TIMEOUT");
        }
        catch (FadadaIntegrationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            _ = credentialScrubber.Scrub(exception.Message, options.AppId, options.AppSecret);
            await audit.CompleteAsync(AuditOperationStatus.Failed, "FDD_TOKEN_TRANSPORT", cancellationToken);
            throw new FadadaIntegrationException("FDD_TOKEN_TRANSPORT");
        }
    }

    private static bool TryReadToken(string body, out string? token, out int expiresIn)
    {
        token = null;
        expiresIn = 0;
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        token = FadadaJson.GetString(data, "accessToken", "access_token");
        var rawExpiry = FadadaJson.GetString(data, "expiresIn", "expires_in");
        _ = int.TryParse(rawExpiry, NumberStyles.Integer, CultureInfo.InvariantCulture, out expiresIn);
        return !string.IsNullOrWhiteSpace(token);
    }

    /// <summary>
    /// 保存仅供令牌提供器内部使用的令牌值和到期时间，不跨越基础设施边界。
    /// </summary>
    private sealed record CachedToken(string Value, DateTimeOffset ExpiresAtUtc);
}

/// <summary>
/// 表示法大大集成层的安全失败码，隐藏外部原始错误和敏感上下文。
/// </summary>
internal sealed class FadadaIntegrationException(string errorCode) : Exception(errorCode)
{
    public string ErrorCode { get; } = errorCode;
}
