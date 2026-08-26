// Calls only catalogued read-only endpoints and sanitizes failures before they cross the adapter boundary.
using System.Net.Http.Headers;
using Fadada.CertificationQueryAgent.Application.Auditing;
using Fadada.CertificationQueryAgent.Application.DomainTools;
using Fadada.CertificationQueryAgent.Infrastructure.Security;

namespace Fadada.CertificationQueryAgent.Infrastructure.Fadada;

/// <summary>
/// 仅调用目录中批准的法大大只读端点，并统一执行令牌、审计和响应边界处理。
/// </summary>
internal sealed class FadadaApiClient(
    HttpClient httpClient,
    FadadaOptions options,
    IFadadaTokenProvider tokenProvider,
    IAuditStore auditStore,
    CredentialScrubber credentialScrubber)
{
    public ValueTask<FadadaResult<string>> GetAccountAsync(
        DomainQueryContext context,
        string mobile,
        CancellationToken cancellationToken) =>
        SendGetAsync(FadadaEndpointKey.GetAccount, context, new Dictionary<string, string> { ["mobile"] = mobile }, cancellationToken);

    public ValueTask<FadadaResult<string>> GetPersonVerificationAsync(
        DomainQueryContext context,
        string accountId,
        CancellationToken cancellationToken) =>
        SendGetAsync(FadadaEndpointKey.GetPersonVerification, context, new Dictionary<string, string> { ["accountId"] = accountId }, cancellationToken);

    public ValueTask<FadadaResult<string>> GetCompanyAsync(
        DomainQueryContext context,
        string companyFullName,
        CancellationToken cancellationToken) =>
        SendGetAsync(FadadaEndpointKey.GetCompany, context, new Dictionary<string, string> { ["companyName"] = companyFullName }, cancellationToken);

    public ValueTask<FadadaResult<string>> GetCompanyVerificationAsync(
        DomainQueryContext context,
        string companyId,
        CancellationToken cancellationToken) =>
        SendGetAsync(FadadaEndpointKey.GetCompanyVerification, context, new Dictionary<string, string> { ["companyId"] = companyId }, cancellationToken);

    public ValueTask<FadadaResult<string>> GetSealsAsync(
        DomainQueryContext context,
        string companyId,
        CancellationToken cancellationToken) =>
        SendGetAsync(FadadaEndpointKey.GetSeals, context, new Dictionary<string, string> { ["companyId"] = companyId }, cancellationToken);

    public ValueTask<FadadaResult<string>> GetSealInfoAsync(
        DomainQueryContext context,
        string sealId,
        CancellationToken cancellationToken) =>
        SendGetAsync(FadadaEndpointKey.GetSealInfo, context, new Dictionary<string, string> { ["sealId"] = sealId }, cancellationToken);

    private async ValueTask<FadadaResult<string>> SendGetAsync(
        FadadaEndpointKey endpointKey,
        DomainQueryContext context,
        IReadOnlyDictionary<string, string> query,
        CancellationToken cancellationToken)
    {
        options.Validate();
        var endpoint = FadadaEndpointCatalog.Get(endpointKey);
        if (endpoint.Method != HttpMethod.Get || endpoint.CredentialExchange)
        {
            throw new InvalidOperationException("Only approved business GET endpoints can use this client.");
        }

        var audit = await ExternalAuditScope.StartAsync(auditStore, context, endpoint, cancellationToken);
        var retries = options.MaximumGetRetries;
        for (var attempt = 0; attempt <= retries; attempt++)
        {
            try
            {
                var token = await tokenProvider.GetAsync(context, cancellationToken);
                using var request = new HttpRequestMessage(endpoint.Method, BuildUri(endpoint, query));
                request.Headers.Authorization = new AuthenticationHeaderValue("bearer", token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.RequestTimeout);
                using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    await audit.CompleteAsync(AuditOperationStatus.Succeeded, null, cancellationToken);
                    return FadadaResult<string>.Success(body);
                }

                var retryable = (int)response.StatusCode >= 500;
                if (retryable && attempt < retries)
                {
                    continue;
                }

                var code = $"FDD_HTTP_{(int)response.StatusCode}";
                await audit.CompleteAsync(AuditOperationStatus.Failed, code, cancellationToken);
                return FadadaResult<string>.Failure(code, endpoint.Key.ToString(), retryable);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt < retries)
                {
                    continue;
                }

                await audit.CompleteAsync(AuditOperationStatus.Failed, "FDD_TIMEOUT", cancellationToken);
                return FadadaResult<string>.Failure("FDD_TIMEOUT", endpoint.Key.ToString(), retryable: true);
            }
            catch (FadadaIntegrationException exception)
            {
                await audit.CompleteAsync(AuditOperationStatus.Failed, exception.ErrorCode, cancellationToken);
                return FadadaResult<string>.Failure(exception.ErrorCode, endpoint.Key.ToString());
            }
            catch (HttpRequestException exception)
            {
                _ = credentialScrubber.Scrub(exception.Message, options.AppId, options.AppSecret);
                if (attempt < retries)
                {
                    continue;
                }

                await audit.CompleteAsync(AuditOperationStatus.Failed, "FDD_TRANSPORT_ERROR", cancellationToken);
                return FadadaResult<string>.Failure("FDD_TRANSPORT_ERROR", endpoint.Key.ToString(), retryable: true);
            }
        }

        throw new InvalidOperationException("Fadada retry loop terminated unexpectedly.");
    }

    private Uri BuildUri(FadadaEndpoint endpoint, IReadOnlyDictionary<string, string> query)
    {
        var encoded = string.Join('&', query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new Uri(options.BaseUri, $"{endpoint.RelativePath}?{encoded}");
    }
}
