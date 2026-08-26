// Exercises authentication, CSRF, ownership isolation, safe errors, and streaming API behavior.
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Fadada.CertificationQueryAgent.Application.AgentTurns;
using Fadada.CertificationQueryAgent.Application.Authentication;
using Fadada.CertificationQueryAgent.Application.Common;
using Fadada.CertificationQueryAgent.Application.Conversations;
using Fadada.CertificationQueryAgent.Application.Persistence;
using Fadada.CertificationQueryAgent.Web.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fadada.CertificationQueryAgent.WebTests;

/// <summary>
/// 验证 WebSecurityAndApiTests 所覆盖的行为、边界条件和安全回归约束。
/// </summary>
public sealed partial class WebSecurityAndApiTests
{
    [Fact]
    public async Task LoginPage_UsesExternalAssets_AndContainsAntiforgeryToken()
    {
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateSecureClient();

        var response = await client.GetAsync("/login");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("name=\"__RequestVerificationToken\"", html, StringComparison.Ordinal);
        Assert.Contains("src=\"/js/login.js\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/css/app.css\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Workbench_RequiresAuthentication_AndUsesInteractiveServerBootScript()
    {
        await using var factory = new TestApplicationFactory();
        using var anonymous = factory.CreateSecureClient();
        using var authenticated = factory.CreateSecureClient();

        var rejected = await anonymous.GetAsync("/");
        await LoginAsync(authenticated, "alice");
        var accepted = await authenticated.GetAsync("/");
        var html = await accepted.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Redirect, rejected.StatusCode);
        Assert.Equal("/login", rejected.Headers.Location?.AbsolutePath);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Contains("_framework/blazor.web.js", html, StringComparison.Ordinal);
        Assert.Contains("法大大认证信息查询", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BlazorBootAsset_IsAnonymousJavascript_NotAnAuthenticationRedirect()
    {
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateSecureClient();

        var response = await client.GetAsync("/_framework/blazor.web.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Content.Headers.ContentLength > 100_000);
    }

    [Fact]
    public async Task WorkbenchClient_AllowsOnlyTypedSse_AndNeverInjectsHtml()
    {
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateSecureClient();

        var script = await client.GetStringAsync("/js/workbench.js");

        Assert.Contains("turn.started", script, StringComparison.Ordinal);
        Assert.Contains("agent.text.delta", script, StringComparison.Ordinal);
        Assert.Contains("tool.completed", script, StringComparison.Ordinal);
        Assert.Contains("restoreConversation", script, StringComparison.Ordinal);
        Assert.Contains("?status=${scope}", script, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", script, StringComparison.Ordinal);
        Assert.DoesNotContain("insertAdjacentHTML", script, StringComparison.Ordinal);
        Assert.DoesNotContain("eval(", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnonymousApi_IsRejected_AndLiveHealthHasSecurityHeaders()
    {
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateSecureClient();

        var api = await client.GetAsync("/api/v1/conversations");
        var health = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("no-store, max-age=0", health.Headers.CacheControl?.ToString());
        Assert.True(health.Headers.Contains("Content-Security-Policy"));
        Assert.Equal("nosniff", Assert.Single(health.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task Login_RequiresAntiforgery_AndIssuesHardenedCookie()
    {
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateSecureClient();

        var missingToken = await client.PostAsJsonAsync("/auth/login", new { userName = "alice", password = "Valid!Password1" });
        var token = await GetAntiforgeryTokenAsync(client);
        using var login = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
        {
            Content = JsonContent.Create(new { userName = "alice", password = "Valid!Password1" })
        };
        login.Headers.Add("X-CSRF-TOKEN", token);
        var succeeded = await client.SendAsync(login);

        Assert.True(
            missingToken.StatusCode == HttpStatusCode.BadRequest,
            await missingToken.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, succeeded.StatusCode);
        var cookie = Assert.Single(
            succeeded.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("__Host-FadadaCertificationQueryAgent=", StringComparison.Ordinal));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConversationRead_UsesAuthenticatedOwnerId()
    {
        await using var factory = new TestApplicationFactory();
        using var alice = factory.CreateSecureClient();
        using var bob = factory.CreateSecureClient();
        await LoginAsync(alice, "alice");
        await LoginAsync(bob, "bob");

        var own = await alice.GetAsync($"/api/v1/conversations/{factory.ConversationId:D}");
        var foreign = await bob.GetAsync($"/api/v1/conversations/{factory.ConversationId:D}");

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task Archive_and_restore_are_owner_scoped_status_transitions()
    {
        await using var factory = new TestApplicationFactory();
        using var alice = factory.CreateSecureClient();
        using var bob = factory.CreateSecureClient();
        var aliceToken = await LoginAsync(alice, "alice");
        var bobToken = await LoginAsync(bob, "bob");

        Assert.Contains("Test conversation", await alice.GetStringAsync("/api/v1/conversations"), StringComparison.Ordinal);
        Assert.Equal("[]", await alice.GetStringAsync("/api/v1/conversations?status=archived"));

        using var foreignArchive = Mutation($"/api/v1/conversations/{factory.ConversationId:D}/archive", bobToken);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.SendAsync(foreignArchive)).StatusCode);

        using var archive = Mutation($"/api/v1/conversations/{factory.ConversationId:D}/archive", aliceToken);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.SendAsync(archive)).StatusCode);
        Assert.Equal("[]", await alice.GetStringAsync("/api/v1/conversations"));
        var archived = await alice.GetStringAsync("/api/v1/conversations?status=archived");
        Assert.Contains("Test conversation", archived, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"archived\"", archived, StringComparison.Ordinal);

        var archivedTurn = await SendTurnAsync(alice, factory.ConversationId, aliceToken, "query archived conversation");
        Assert.Equal(HttpStatusCode.NotFound, archivedTurn.StatusCode);

        using var restore = Mutation($"/api/v1/conversations/{factory.ConversationId:D}/restore", aliceToken);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.SendAsync(restore)).StatusCode);
        Assert.Contains("Test conversation", await alice.GetStringAsync("/api/v1/conversations"), StringComparison.Ordinal);
        Assert.Equal("[]", await alice.GetStringAsync("/api/v1/conversations?status=archived"));
    }

    [Fact]
    public async Task Archive_requires_antiforgery_and_invalid_list_status_is_rejected()
    {
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateSecureClient();
        await LoginAsync(client, "alice");

        var missingToken = await client.PostAsync(
            $"/api/v1/conversations/{factory.ConversationId:D}/archive",
            content: null);
        var invalidStatus = await client.GetAsync("/api/v1/conversations?status=deleted");

        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidStatus.StatusCode);
        Assert.Contains("CONVERSATION_STATUS_INVALID", await invalidStatus.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidatedSecurityStamp_RejectsExistingSession()
    {
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateSecureClient();
        await LoginAsync(client, "alice");
        factory.Authentication.SessionsValid = false;

        var response = await client.GetAsync("/api/v1/conversations");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UnhandledFailure_ReturnsOnlyStableSafeError()
    {
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateSecureClient();
        await LoginAsync(client, "alice");
        factory.Conversations.FailReads = true;

        var response = await client.GetAsync("/api/v1/conversations");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("SERVICE_REQUEST_FAILED", body, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive store details", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TurnStream_ContainsOnlyWhitelistedSafeEvents_AndPersistsCounts()
    {
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateSecureClient();
        var token = await LoginAsync(client, "alice");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/conversations/{factory.ConversationId:D}/turns")
        {
            Content = JsonContent.Create(new { message = "查询 13800000000" })
        };
        request.Headers.Add("X-CSRF-TOKEN", token);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("event: turn.started", body, StringComparison.Ordinal);
        Assert.Contains("event: tool.started", body, StringComparison.Ordinal);
        Assert.Contains("event: tool.completed", body, StringComparison.Ordinal);
        Assert.Contains("event: agent.text.delta", body, StringComparison.Ordinal);
        Assert.Contains("event: turn.completed", body, StringComparison.Ordinal);
        Assert.Contains("safe answer", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SYSTEM_PROMPT_SECRET", body, StringComparison.Ordinal);
        Assert.DoesNotContain("rawArguments", body, StringComparison.OrdinalIgnoreCase);
        var completion = Assert.Single(factory.TurnStore.Completions);
        Assert.Equal(2, completion.ModelCallCount);
        Assert.Equal(1, completion.ToolCallCount);
        Assert.Equal(AgentTurnStatus.Succeeded, completion.Status);
        Assert.Equal("safe answer", completion.AssistantMessage?.Content);
    }

    [Fact]
    public async Task TurnCompleted_IsNotStreamedUntilCompletionIsPersisted()
    {
        await using var factory = new TestApplicationFactory();
        factory.TurnStore.BlockCompletion = true;
        using var client = factory.CreateSecureClient();
        var token = await LoginAsync(client, "alice");
        using var request = TurnRequest(factory.ConversationId, token, "查询 13800000000");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var reader = new StreamReader(stream);

            Assert.Equal("turn.started", await ReadSseEventNameAsync(reader, timeout.Token));
            Assert.Equal("tool.started", await ReadSseEventNameAsync(reader, timeout.Token));
            Assert.Equal("tool.completed", await ReadSseEventNameAsync(reader, timeout.Token));
            Assert.Equal("agent.text.delta", await ReadSseEventNameAsync(reader, timeout.Token));
            await factory.TurnStore.CompletionEntered.WaitAsync(timeout.Token);

            var terminalRead = ReadSseEventNameAsync(reader, timeout.Token);
            await Task.Delay(100, timeout.Token);
            Assert.False(terminalRead.IsCompleted);

            factory.TurnStore.ReleaseCompletion();
            Assert.Equal("turn.completed", await terminalRead);
        }
        finally
        {
            factory.TurnStore.ReleaseCompletion();
        }
    }

    [Fact]
    public async Task CompletionPersistenceFailure_StreamsFailureInsteadOfCompleted()
    {
        await using var factory = new TestApplicationFactory();
        factory.TurnStore.FailCompletion = true;
        using var client = factory.CreateSecureClient();
        var token = await LoginAsync(client, "alice");
        using var request = TurnRequest(factory.ConversationId, token, "查询 13800000000");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("event: turn.failed", body, StringComparison.Ordinal);
        Assert.Contains("STORE_TURN_COMPLETION_FAILED", body, StringComparison.Ordinal);
        Assert.DoesNotContain("event: turn.completed", body, StringComparison.Ordinal);
        Assert.Empty(factory.TurnStore.Completions);
    }

    [Fact]
    public async Task Forged_provider_or_session_identifiers_are_rejected_before_turn_start()
    {
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateSecureClient();
        var token = await LoginAsync(client, "alice");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/conversations/{factory.ConversationId:D}/turns")
        {
            Content = JsonContent.Create(new
            {
                message = "查询 13800000000",
                providerSessionId = "forged-provider-session",
                sessionState = "forged-state"
            })
        };
        request.Headers.Add("X-CSRF-TOKEN", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.TurnStore.StartCalls);
        Assert.Equal(0, factory.AgentRuntime.Invocations);
    }

    [Fact]
    public async Task Concurrent_turns_allow_one_start_and_map_the_loser_to_conflict()
    {
        await using var factory = new TestApplicationFactory();
        factory.TurnStore.CoordinateConcurrentStarts = true;
        using var firstClient = factory.CreateSecureClient();
        using var secondClient = factory.CreateSecureClient();
        var firstToken = await LoginAsync(firstClient, "alice");
        var secondToken = await LoginAsync(secondClient, "alice");

        var first = SendTurnAsync(firstClient, factory.ConversationId, firstToken, "first query");
        var second = SendTurnAsync(secondClient, factory.ConversationId, secondToken, "second query");
        var responses = await Task.WhenAll(first, second);

        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.Conflict],
            responses.Select(response => response.StatusCode).Order().ToArray());
        Assert.Equal(2, factory.TurnStore.StartCalls);
        Assert.Equal(1, factory.AgentRuntime.Invocations);
        Assert.Single(factory.TurnStore.Completions);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Persisted_malicious_message_is_JSON_escaped_and_round_trips_as_text()
    {
        const string malicious = "<script>globalThis.compromised=true</script><img src=x onerror=alert(1)>";
        await using var factory = new TestApplicationFactory();
        factory.Conversations.PersistedMessages.Add(new ConversationMessage(
            MessageId.New(),
            new ConversationId(factory.ConversationId),
            null,
            MessageRole.Assistant,
            malicious,
            1,
            DateTimeOffset.UtcNow));
        using var client = factory.CreateSecureClient();
        await LoginAsync(client, "alice");

        var response = await client.GetAsync($"/api/v1/conversations/{factory.ConversationId:D}");
        var body = await response.Content.ReadAsStringAsync();
        using var json = System.Text.Json.JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("<script", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\\u003Cscript", body, StringComparison.Ordinal);
        Assert.Equal(malicious, json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public void UiDemo_profile_is_rejected_outside_development()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Persistence:Profile"] = "UiDemo" })
            .Build();
        var environment = new StubWebHostEnvironment { EnvironmentName = Environments.Production };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddDomainAgentRuntime(configuration, environment));

        Assert.Equal("CONFIG_UI_DEMO_DEVELOPMENT_ONLY", exception.Message);
    }

    [Fact]
    public async Task LoginRateLimit_IsPartitionedByRemoteIp()
    {
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateSecureClient();
        var token = await GetAntiforgeryTokenAsync(client);
        var statuses = new List<HttpStatusCode>();
        for (var index = 0; index < 6; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
            {
                Content = JsonContent.Create(new { userName = "invalid", password = "invalid" })
            };
            request.Headers.Add("X-CSRF-TOKEN", token);
            statuses.Add((await client.SendAsync(request)).StatusCode);
        }

        Assert.All(statuses.Take(5), status => Assert.Equal(HttpStatusCode.Unauthorized, status));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[5]);
    }

    private static async Task<string> LoginAsync(HttpClient client, string userName)
    {
        var token = await GetAntiforgeryTokenAsync(client);
        using var login = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
        {
            Content = JsonContent.Create(new { userName, password = "Valid!Password1" })
        };
        login.Headers.Add("X-CSRF-TOKEN", token);
        var response = await client.SendAsync(login);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await GetAntiforgeryTokenAsync(client);
    }

    private static Task<HttpResponseMessage> SendTurnAsync(
        HttpClient client,
        Guid conversationId,
        string antiforgeryToken,
        string message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/conversations/{conversationId:D}/turns")
        {
            Content = JsonContent.Create(new { message })
        };
        request.Headers.Add("X-CSRF-TOKEN", antiforgeryToken);
        return client.SendAsync(request);
    }

    private static HttpRequestMessage TurnRequest(Guid conversationId, string antiforgeryToken, string message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/conversations/{conversationId:D}/turns")
        {
            Content = JsonContent.Create(new { message })
        };
        request.Headers.Add("X-CSRF-TOKEN", antiforgeryToken);
        return request;
    }

    private static async Task<string?> ReadSseEventNameAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                return line[7..];
            }
        }

        return null;
    }

    private static HttpRequestMessage Mutation(string path, string antiforgeryToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-TOKEN", antiforgeryToken);
        return request;
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/login");
        var match = AntiforgeryTokenPattern().Match(html);
        Assert.True(match.Success);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex AntiforgeryTokenPattern();

    /// <summary>
    /// 为所属测试提供可观测且无外部副作用的 TestApplicationFactory 测试替身。
    /// </summary>
    private sealed class TestApplicationFactory : WebApplicationFactory<Program>
    {
        private static readonly UserId AliceId = new(Guid.Parse("10000000-0000-0000-0000-000000000001"));
        private static readonly UserId BobId = new(Guid.Parse("10000000-0000-0000-0000-000000000002"));
        public TestApplicationFactory()
        {
            ConversationId = Guid.Parse("20000000-0000-0000-0000-000000000001");
            Conversations = new FakeConversationStore(AliceId, new ConversationId(ConversationId));
            TurnStore = new FakeTurnStore();
            Authentication = new FakeAuthenticationService(AliceId, BobId);
            AgentRuntime = new FakeAgentRuntime();
        }

        public Guid ConversationId { get; }

        public FakeTurnStore TurnStore { get; }

        public FakeAuthenticationService Authentication { get; }

        public FakeConversationStore Conversations { get; }

        public FakeAgentRuntime AgentRuntime { get; }

        public HttpClient CreateSecureClient() => CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        protected override IHost CreateHost(IHostBuilder builder)
        {
            // Minimal Hosting 在 ConfigureWebHost 回调前校验应用配置，因此必须在入口点执行前提供合成值。
            using var environment = new TestEnvironmentScope(TestConfiguration());
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(TestConfiguration()));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuthenticationService>();
                services.RemoveAll<IConversationStore>();
                services.RemoveAll<IAgentTurnStore>();
                services.RemoveAll<IAgentRuntime>();
                services.AddSingleton<IAuthenticationService>(Authentication);
                services.AddSingleton<IConversationStore>(Conversations);
                services.AddSingleton<IAgentTurnStore>(TurnStore);
                services.AddSingleton<IAgentRuntime>(AgentRuntime);
            });
        }

        private static Dictionary<string, string?> TestConfiguration() => new()
        {
            ["Persistence:Profile"] = "ProductionReference",
            ["ConnectionStrings:FddDomainAgent"] = "Server=localhost;Database=FddDomainAgentTest;User ID=test;Password=test;Encrypt=True;TrustServerCertificate=False",
            ["Model:BaseUrl"] = "https://model.example/",
            ["Model:ApiKey"] = "test-model-key",
            ["Model:Name"] = "test-model",
            ["Fadada:BaseUrl"] = "https://fadada.example/",
            ["Fadada:AppId"] = "test-app",
            ["Fadada:AppSecret"] = "test-secret",
            ["Security:DataProtectionKeysPath"] = Path.Combine(AppContext.BaseDirectory, "web-test-keys"),
            ["OpenTelemetry:Enabled"] = "false"
        };

        /// <summary>
        /// 在测试宿主启动期间临时注入合成环境变量，并在宿主创建后恢复调用进程原值。
        /// </summary>
        private sealed class TestEnvironmentScope : IDisposable
        {
            private readonly Dictionary<string, string?> previous = new(StringComparer.Ordinal);

            public TestEnvironmentScope(IReadOnlyDictionary<string, string?> settings)
            {
                foreach (var setting in settings)
                {
                    var environmentName = setting.Key.Replace(":", "__", StringComparison.Ordinal);
                    previous[environmentName] = Environment.GetEnvironmentVariable(environmentName);
                    Environment.SetEnvironmentVariable(environmentName, setting.Value);
                }
            }

            public void Dispose()
            {
                foreach (var setting in previous)
                {
                    Environment.SetEnvironmentVariable(setting.Key, setting.Value);
                }
            }
        }
    }

    /// <summary>
    /// 为所属测试提供可观测且无外部副作用的 FakeAuthenticationService 测试替身。
    /// </summary>
    private sealed class FakeAuthenticationService(UserId aliceId, UserId bobId) : IAuthenticationService
    {
        public bool SessionsValid { get; set; } = true;

        public ValueTask<AuthenticationResult> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            var userId = request.UserName switch
            {
                "alice" => aliceId,
                "bob" => bobId,
                _ => (UserId?)null
            };
            return ValueTask.FromResult(userId is { } value
                ? new AuthenticationResult(true, value, $"stamp-{value.Value:D}", null, null)
                : new AuthenticationResult(false, null, null, "AUTH_INVALID_CREDENTIALS", null));
        }

        public ValueTask<bool> ValidateSessionAsync(UserId userId, string securityStamp, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return ValueTask.FromResult(
                SessionsValid && (userId == aliceId || userId == bobId) && securityStamp == $"stamp-{userId.Value:D}");
        }
    }

    /// <summary>
    /// 为所属测试提供可观测且无外部副作用的 FakeConversationStore 测试替身。
    /// </summary>
    private sealed class FakeConversationStore(UserId ownerId, ConversationId conversationId) : IConversationStore
    {
        private ConversationSummary summary = new(
            conversationId,
            ownerId,
            "Test conversation",
            ConversationStatus.Active,
            DateTimeOffset.UtcNow,
            [1, 2, 3, 4, 5, 6, 7, 8]);

        public bool FailReads { get; set; }

        public List<ConversationMessage> PersistedMessages { get; } = [];

        public ValueTask<ConversationSummary> CreateAsync(UserId userId, string title, CancellationToken cancellationToken) =>
            ValueTask.FromResult(summary with { UserId = userId, Title = title });

        public ValueTask<ConversationSnapshot?> GetAsync(ConversationId id, UserId userId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ConversationSnapshot?>(
                id == conversationId && userId == ownerId ? new ConversationSnapshot(summary, [.. PersistedMessages]) : null);

        public ValueTask<IReadOnlyList<ConversationSummary>> ListAsync(
            UserId userId,
            ConversationStatus status,
            CancellationToken cancellationToken) =>
            FailReads
                ? throw new InvalidOperationException("sensitive store details")
                : ValueTask.FromResult<IReadOnlyList<ConversationSummary>>(
                    userId == ownerId && summary.Status == status ? [summary] : []);

        public ValueTask<bool> ArchiveAsync(ConversationId id, UserId userId, CancellationToken cancellationToken) =>
            ChangeStatus(id, userId, ConversationStatus.Active, ConversationStatus.Archived);

        public ValueTask<bool> RestoreAsync(ConversationId id, UserId userId, CancellationToken cancellationToken) =>
            ChangeStatus(id, userId, ConversationStatus.Archived, ConversationStatus.Active);

        private ValueTask<bool> ChangeStatus(
            ConversationId id,
            UserId userId,
            ConversationStatus expected,
            ConversationStatus target)
        {
            if (id != conversationId || userId != ownerId || summary.Status != expected)
            {
                return ValueTask.FromResult(false);
            }

            summary = summary with
            {
                Status = target,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                RowVersion = [8, 7, 6, 5, 4, 3, 2, 1]
            };
            return ValueTask.FromResult(true);
        }
    }

    /// <summary>
    /// 为所属测试提供可观测且无外部副作用的 FakeTurnStore 测试替身。
    /// </summary>
    public sealed class FakeTurnStore : IAgentTurnStore
    {
        private readonly TaskCompletionSource bothStartsEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource completionEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource completionReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int startCalls;

        public List<AgentTurnCompletion> Completions { get; } = [];

        public bool CoordinateConcurrentStarts { get; set; }

        public bool BlockCompletion { get; set; }

        public bool FailCompletion { get; set; }

        public Task CompletionEntered => completionEntered.Task;

        public int StartCalls => Volatile.Read(ref startCalls);

        public async ValueTask<byte[]> StartAsync(AgentTurnStart turn, CancellationToken cancellationToken)
        {
            _ = turn;
            var ordinal = Interlocked.Increment(ref startCalls);
            if (CoordinateConcurrentStarts)
            {
                if (ordinal == 1)
                {
                    await bothStartsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                }
                else
                {
                    bothStartsEntered.TrySetResult();
                    throw new PersistenceConcurrencyException("STORE_CONVERSATION_CONFLICT");
                }
            }

            return [8, 7, 6, 5, 4, 3, 2, 1];
        }

        public async ValueTask<byte[]> CompleteAsync(AgentTurnCompletion turn, CancellationToken cancellationToken)
        {
            completionEntered.TrySetResult();
            if (BlockCompletion)
            {
                await completionReleased.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }

            if (FailCompletion)
            {
                throw new InvalidOperationException("synthetic persistence failure");
            }

            Completions.Add(turn);
            return [9, 8, 7, 6, 5, 4, 3, 2];
        }

        public void ReleaseCompletion() => completionReleased.TrySetResult();
    }

    /// <summary>
    /// 为所属测试提供可观测且无外部副作用的 FakeAgentRuntime 测试替身。
    /// </summary>
    public sealed class FakeAgentRuntime : IAgentRuntime
    {
        private int invocations;

        public int Invocations => Volatile.Read(ref invocations);

        public async IAsyncEnumerable<AgentEvent> RunAsync(
            AgentTurnRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            Interlocked.Increment(ref invocations);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new AgentEvent(AgentEventKind.TurnStarted, "SYSTEM_PROMPT_SECRET", "unsafe", null);
            yield return new AgentEvent(AgentEventKind.Clarification, "SYSTEM_PROMPT_SECRET", null, null);
            yield return new AgentEvent(AgentEventKind.ToolStarted, null, "query_person", null);
            yield return new AgentEvent(AgentEventKind.ToolCompleted, null, "query_person", null);
            yield return new AgentEvent(AgentEventKind.TextDelta, "safe answer", null, null);
            yield return new AgentEvent(AgentEventKind.TurnCompleted, null, null, null, 2, 1);
        }

        public ValueTask<AgentTurnResult> RunOnceAsync(AgentTurnRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AgentTurnResult("safe answer", 2, 1, "test"));
    }

    /// <summary>
    /// 为所属测试提供可观测且无外部副作用的 StubWebHostEnvironment 测试替身。
    /// </summary>
    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Fadada.CertificationQueryAgent.WebTests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
