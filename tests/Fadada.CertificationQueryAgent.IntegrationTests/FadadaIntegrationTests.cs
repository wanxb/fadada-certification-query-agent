// Exercises provider composition through fake HTTP responses while prohibiting external traffic.
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Fadada.CertificationQueryAgent.Application.Auditing;
using Fadada.CertificationQueryAgent.Application.Common;
using Fadada.CertificationQueryAgent.Application.DomainTools;
using Fadada.CertificationQueryAgent.Domain.Evidence;
using Fadada.CertificationQueryAgent.Domain.Queries;
using Fadada.CertificationQueryAgent.Infrastructure.Fadada;
using Fadada.CertificationQueryAgent.Infrastructure.Security;

namespace Fadada.CertificationQueryAgent.IntegrationTests;

/// <summary>
/// 验证 FadadaIntegrationTests 所覆盖的行为、边界条件和安全回归约束。
/// </summary>
public sealed class FadadaIntegrationTests
{
    [Fact]
    public void PrimaryHandler_DisablesRedirectsAndCookies()
    {
        using var handler = Assert.IsType<HttpClientHandler>(FadadaHttpHandlerFactory.Create());

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
    }

    [Fact]
    public void Options_StringRepresentation_RedactsCredentials()
    {
        var options = new FadadaOptions(
            new Uri("https://fdd.test/"),
            "synthetic-app",
            "synthetic-secret",
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(2),
            1);

        Assert.DoesNotContain("synthetic-app", options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-secret", options.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PersonQueries_UseFixedGetsAndOneCachedToken()
    {
        var audit = new RecordingAuditStore();
        var handler = new RecordingHandler((request, _) => Respond(request.RequestUri!.AbsolutePath switch
        {
            "/base/login/oauth2/accessToken" => """{"code":0,"data":{"accessToken":"synthetic-token","expiresIn":3600}}""",
            "/user/api/account/getAccount" => """{"code":0,"data":{"accountId":"A-1","mobile":"13800000000","status":"active"}}""",
            "/user/api/verify/person/result" => """{"code":0,"data":{"accountId":"A-1","name":"测试甲","status":"verified"}}""",
            _ => throw new InvalidOperationException("Unexpected endpoint.")
        }));
        using var service = CreateService(handler, audit);

        var first = await service.QueryPersonAsync(Context(), new PersonQuery(MobileNumber.Create("13800000000"), PersonName.Create("测试甲")), CancellationToken.None);
        var second = await service.QueryPersonAsync(Context(), new PersonQuery(MobileNumber.Create("13800000000"), null), CancellationToken.None);

        Assert.Equal(EvidenceStatus.Succeeded, first.Status);
        Assert.Equal(ConclusionStatus.Confirmed, first.Conclusion.Status);
        Assert.Equal(EvidenceStatus.Succeeded, second.Status);
        Assert.Equal(1, handler.Requests.Count(request => request.Path == "/base/login/oauth2/accessToken"));
        var requestPaths = handler.Requests.Select(request => request.Path).ToArray();
        Assert.True(
            Array.IndexOf(requestPaths, "/user/api/account/getAccount") <
            Array.IndexOf(requestPaths, "/user/api/verify/person/result"));
        Assert.All(
            handler.Requests.Where(request => request.Path != "/base/login/oauth2/accessToken"),
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("bearer", request.AuthorizationScheme);
                Assert.Equal("synthetic-token", request.AuthorizationParameter);
            });
        Assert.Equal(handler.Requests.Count, audit.Prewrites.Count);
        Assert.DoesNotContain(audit.Prewrites, entry => entry.Operation.Contains("synthetic-token", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("true", BusinessStatus.Verified)]
    [InlineData("false", BusinessStatus.NotVerified)]
    [InlineData("0", BusinessStatus.NotVerified)]
    [InlineData("1", BusinessStatus.Verified)]
    [InlineData("3", BusinessStatus.Verified)]
    public async Task PersonVerification_MapsLegacyBooleanAndNumericCertificationValues(
        string providerValue,
        BusinessStatus expected)
    {
        object certificationStatus = bool.TryParse(providerValue, out var booleanStatus)
            ? booleanStatus
            : int.Parse(providerValue, System.Globalization.CultureInfo.InvariantCulture);
        var verificationResponse = JsonSerializer.Serialize(new
        {
            code = 0,
            data = new
            {
                accountId = "A-1",
                name = "测试甲",
                isCerdit = certificationStatus
            }
        });
        var handler = new RecordingHandler((request, _) => Respond(request.RequestUri!.AbsolutePath switch
        {
            "/base/login/oauth2/accessToken" => """{"code":0,"data":{"accessToken":"synthetic-token","expiresIn":3600}}""",
            "/user/api/account/getAccount" => """{"code":0,"data":{"accountId":"A-1","mobile":"13800000000","status":0}}""",
            "/user/api/verify/person/result" => verificationResponse,
            _ => throw new InvalidOperationException("Unexpected endpoint.")
        }));
        using var service = CreateService(handler, new RecordingAuditStore());

        var result = await service.QueryPersonAsync(
            Context(),
            new PersonQuery(MobileNumber.Create("13800000000"), null),
            CancellationToken.None);

        Assert.Equal(BusinessStatus.Active, result.Data!.AccountStatus);
        Assert.Equal(expected, result.Data.VerificationStatus);
    }

    [Fact]
    public async Task RelationshipQuery_StartsPersonAndCompanyBranchesInParallel()
    {
        var accountStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var companyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler(async (request, _) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/base/login/oauth2/accessToken":
                    return await Respond("""{"code":0,"data":{"accessToken":"synthetic-token","expiresIn":3600}}""");
                case "/user/api/account/getAccount":
                    accountStarted.SetResult();
                    await companyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    return await Respond("""{"code":0,"data":{"accountId":"A-1","mobile":"13800000000","status":"active"}}""");
                case "/user/api/company/getCompany":
                    companyStarted.SetResult();
                    await accountStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    return await Respond("""{"code":0,"data":{"companyId":"C-1","companyName":"星河测试有限公司","status":"active","adminInfo":[{"accountId":"A-1","userName":"企业查询管理员"}]}}""");
                case "/user/api/verify/person/result":
                    return await Respond("""{"code":0,"data":{"accountId":"A-1","name":"测试甲","status":"verified"}}""");
                case "/user/api/verify/company/result":
                    return await Respond("""{"code":0,"data":{"companyId":"C-1","isCerdit":3,"managerInfo":{"accountId":"A-1","name":"认证管理员","mobile":"13800000000"}}}""");
                default:
                    throw new InvalidOperationException("Unexpected endpoint.");
            }
        });
        using var service = CreateService(handler, new RecordingAuditStore());

        var result = await service.QueryRelationshipAsync(
            Context(),
            new RelationshipQuery(
                MobileNumber.Create("13800000000"),
                CompanyFullName.Create("星河测试有限公司"),
                PersonName.Create("测试甲")),
            CancellationToken.None);

        Assert.Equal(EvidenceStatus.Succeeded, result.Status);
        Assert.Equal(ConclusionStatus.Confirmed, result.Conclusion.Status);
        Assert.Equal("认证管理员", result.Data!.Company.Administrator!.Name);
        Assert.Equal("13800000000", result.Data.Company.Administrator.Mobile);
        Assert.Contains(result.Facts, fact =>
            fact.Name == "company.administrator.name" &&
            fact.Value == "认证管理员" &&
            fact.Reliability == FactReliability.VerifiedAttribute);
        Assert.Contains(result.Facts, fact =>
            fact.Name == "company.administrator.mobile" &&
            fact.Value == "13800000000" &&
            fact.Reliability == FactReliability.VerifiedAttribute);
    }

    [Fact]
    public async Task AuditPrewriteFailure_PreventsEveryHttpRequest()
    {
        var handler = new RecordingHandler((_, _) => Respond("{}"));
        using var service = CreateService(handler, new FailingAuditStore());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.QueryPersonAsync(
                Context(),
                new PersonQuery(MobileNumber.Create("13800000000"), null),
                CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MissingAccount_ShortCircuitsVerification()
    {
        var handler = new RecordingHandler((request, _) => Respond(
            request.RequestUri!.AbsolutePath == "/base/login/oauth2/accessToken"
                ? """{"code":0,"data":{"accessToken":"synthetic-token","expiresIn":3600}}"""
                : """{"code":0,"data":[]}"""));
        using var service = CreateService(handler, new RecordingAuditStore());

        var result = await service.QueryPersonAsync(
            Context(),
            new PersonQuery(MobileNumber.Create("19999999999"), null),
            CancellationToken.None);

        Assert.Equal(EvidenceStatus.NotFound, result.Status);
        Assert.DoesNotContain(handler.Requests, request => request.Path == "/user/api/verify/person/result");
    }

    [Fact]
    public async Task RetryableGet_RetriesOnceAndThenSucceeds()
    {
        var accountAttempts = 0;
        var handler = new RecordingHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/base/login/oauth2/accessToken")
            {
                return Respond("""{"code":0,"data":{"accessToken":"synthetic-token","expiresIn":3600}}""");
            }

            if (request.RequestUri.AbsolutePath == "/user/api/account/getAccount" && Interlocked.Increment(ref accountAttempts) == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            return Respond(request.RequestUri.AbsolutePath == "/user/api/account/getAccount"
                ? """{"code":0,"data":{"accountId":"A-1","mobile":"13800000000","status":"active"}}"""
                : """{"code":0,"data":{"accountId":"A-1","name":"测试甲","status":"verified"}}""");
        });
        using var service = CreateService(handler, new RecordingAuditStore());

        var result = await service.QueryPersonAsync(
            Context(),
            new PersonQuery(MobileNumber.Create("13800000000"), null),
            CancellationToken.None);

        Assert.Equal(EvidenceStatus.Succeeded, result.Status);
        Assert.Equal(2, accountAttempts);
    }

    [Fact]
    public async Task SealAuthorization_IsDerivedFromReliableAccountId()
    {
        var handler = new RecordingHandler((request, _) => Respond(request.RequestUri!.AbsolutePath switch
        {
            "/base/login/oauth2/accessToken" => """{"code":0,"data":{"accessToken":"synthetic-token","expiresIn":3600}}""",
            "/user/api/company/getCompany" => """{"code":0,"data":{"companyId":"C-1","companyName":"星河测试有限公司","status":"active"}}""",
            "/user/api/verify/company/result" => """{"code":0,"data":{"companyId":"C-1","isCerdit":3}}""",
            "/user/api/account/getAccount" => """{"code":0,"data":{"accountId":"A-1","mobile":"13800000000","status":"active"}}""",
            "/user/api/verify/person/result" => """{"code":0,"data":{"accountId":"A-1","name":"测试甲","status":"verified"}}""",
            "/base/api/seal/get" => """{"code":0,"data":[{"sealId":"S-1","sealName":"测试公章","sealType":"公章","status":"active"}]}""",
            "/base/api/seal/getSealInfo" => """{"code":0,"data":{"sealId":"S-1","sealName":"测试公章","sealType":"公章","status":"active","authorizeUserInfoList":[{"accountId":"A-1"}]}}""",
            _ => throw new InvalidOperationException("Unexpected endpoint.")
        }));
        using var service = CreateService(handler, new RecordingAuditStore());

        var result = await service.QuerySealsAsync(
            Context(),
            new SealsQuery(CompanyFullName.Create("星河测试有限公司"), MobileNumber.Create("13800000000")),
            CancellationToken.None);

        Assert.Equal(EvidenceStatus.Succeeded, result.Status);
        Assert.True(Assert.Single(result.Data!.Seals).HasAuthorization);
        Assert.DoesNotContain(handler.Requests, request => request.Method != HttpMethod.Get && request.Path != "/base/login/oauth2/accessToken");
    }

    private static FadadaDomainQueryService CreateService(HttpMessageHandler handler, IAuditStore auditStore) => new(
        new HttpClient(handler),
        new FadadaOptions(
            new Uri("https://fdd.test/"),
            "synthetic-app",
            "synthetic-secret",
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(2),
            maximumGetRetries: 1),
        auditStore,
        new CredentialScrubber());

    private static DomainQueryContext Context() => new(
        UserId.New(),
        ConversationId.New(),
        TurnId.New(),
        ToolCallId.New(),
        Guid.NewGuid());

    private static Task<HttpResponseMessage> Respond(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    });

    /// <summary>
    /// 为所属测试提供可观测且无外部副作用的 RecordingHandler 测试替身。
    /// </summary>
    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        public ConcurrentQueue<RequestSnapshot> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(new RequestSnapshot(
                request.Method,
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter));
            return await responseFactory(request, cancellationToken);
        }
    }

    /// <summary>
    /// 封装 RequestSnapshot 测试场景所需的固定输入和可验证状态，减少用例间重复装配。
    /// </summary>
    private sealed record RequestSnapshot(HttpMethod Method, string Path, string? AuthorizationScheme, string? AuthorizationParameter);

    /// <summary>
    /// 为所属测试提供可观测且无外部副作用的 RecordingAuditStore 测试替身。
    /// </summary>
    private sealed class RecordingAuditStore : IAuditStore
    {
        public ConcurrentQueue<AuditPrewrite> Prewrites { get; } = new();
        public ConcurrentQueue<AuditCompletion> Completions { get; } = new();

        public ValueTask PrewriteAsync(AuditPrewrite entry, CancellationToken cancellationToken)
        {
            Prewrites.Enqueue(entry);
            return ValueTask.CompletedTask;
        }

        public ValueTask CompleteAsync(AuditCompletion completion, CancellationToken cancellationToken)
        {
            Completions.Enqueue(completion);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// 为所属测试提供可观测且无外部副作用的 FailingAuditStore 测试替身。
    /// </summary>
    private sealed class FailingAuditStore : IAuditStore
    {
        public ValueTask PrewriteAsync(AuditPrewrite entry, CancellationToken cancellationToken) =>
            ValueTask.FromException(new InvalidOperationException("Synthetic audit failure."));

        public ValueTask CompleteAsync(AuditCompletion completion, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
