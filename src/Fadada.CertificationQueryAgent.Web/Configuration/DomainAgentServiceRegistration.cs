// Registers production adapters only after validating persistence, model, provider, and telemetry settings.
using Fadada.CertificationQueryAgent.AgentHost.Middleware;
using Fadada.CertificationQueryAgent.AgentHost.Runtime;
using Fadada.CertificationQueryAgent.Application.AgentTurns;
using Fadada.CertificationQueryAgent.Application.Auditing;
using Fadada.CertificationQueryAgent.Application.Authentication;
using Fadada.CertificationQueryAgent.Application.Conversations;
using Fadada.CertificationQueryAgent.Application.DomainTools;
using Fadada.CertificationQueryAgent.Application.Persistence;
using Fadada.CertificationQueryAgent.Infrastructure.Ai;
using Fadada.CertificationQueryAgent.Infrastructure.Authentication;
using Fadada.CertificationQueryAgent.Infrastructure.DomainTools;
using Fadada.CertificationQueryAgent.Infrastructure.Fadada;
using Fadada.CertificationQueryAgent.Infrastructure.Maintenance;
using Fadada.CertificationQueryAgent.Infrastructure.Security;
using Fadada.CertificationQueryAgent.Infrastructure.SqlServer2012;
using Microsoft.Extensions.AI;

namespace Fadada.CertificationQueryAgent.Web.Configuration;

/// <summary>
/// 根据显式持久化 Profile 组装生产或演示依赖，阻止演示适配器进入生产。
/// </summary>
public static class DomainAgentServiceRegistration
{
    public static IServiceCollection AddDomainAgentRuntime(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        if (string.Equals(configuration["Persistence:Profile"], "UiDemo", StringComparison.Ordinal))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException("CONFIG_UI_DEMO_DEVELOPMENT_ONLY");
            }

            return services.AddUiDemoServices();
        }

        var sqlOptions = CreateSqlOptions(configuration);
        var modelOptions = CreateModelOptions(configuration);
        var fadadaOptions = CreateFadadaOptions(configuration);
        var runtimeOptions = new AgentRuntimeOptions();
        modelOptions.Validate();
        fadadaOptions.Validate();

        services.AddSingleton(sqlOptions);
        services.AddSingleton(runtimeOptions);
        services.AddSingleton<SqlServerConnectionFactory>();
        services.AddSingleton<IUserStore, SqlServerUserStore>();
        services.AddSingleton<IConversationStore, SqlServerConversationStore>();
        services.AddSingleton<IAgentTurnStore, SqlServerAgentTurnStore>();
        services.AddSingleton<IConversationOwnershipVerifier, SqlServerConversationOwnershipVerifier>();
        services.AddSingleton<IAuditStore, SqlServerAuditStore>();
        services.AddSingleton<IModelCallAuditStore, SqlServerModelCallAuditStore>();
        services.AddSingleton<IDataLifecycleStore, SqlServerDataLifecycleStore>();
        services.AddSingleton<IAgentSessionStateStore, SqlServerSessionStateStore>();
        services.AddSingleton<IDiagnosticPayloadStore, SqlServerDiagnosticPayloadStore>();

        services.AddSingleton<LocalAccountService>();
        services.AddSingleton<IAuthenticationService>(provider => provider.GetRequiredService<LocalAccountService>());
        services.AddSingleton<IAccountAdministrationService>(provider => provider.GetRequiredService<LocalAccountService>());
        services.AddSingleton<CredentialScrubber>();
        services.AddSingleton<IUserProvenanceStore, CanonicalUserProvenanceStore>();
        services.AddSingleton<IRegisteredToolExecutor, RegisteredDomainToolExecutor>();
        services.AddSingleton<IToolPolicyPipeline, ToolPolicyPipeline>();

        services.AddHttpClient("model", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            });
        services.AddHttpClient("fadada", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(FadadaHttpHandlerFactory.Create);
        services.AddSingleton<IChatClient>(provider => new ResponsesChatClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("model"),
            modelOptions));
        services.AddSingleton<IDomainQueryService>(provider => new FadadaDomainQueryService(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("fadada"),
            fadadaOptions,
            provider.GetRequiredService<IAuditStore>(),
            provider.GetRequiredService<CredentialScrubber>()));
        services.AddSingleton<IAgentRuntime>(provider => new DomainAgentRuntime(
            provider.GetRequiredService<IChatClient>(),
            provider.GetRequiredService<IConversationStore>(),
            provider.GetRequiredService<IToolPolicyPipeline>(),
            provider.GetRequiredService<AgentRuntimeOptions>(),
            modelCallAuditStore: provider.GetRequiredService<IModelCallAuditStore>(),
            modelPricing: CreateModelPricing(configuration)));

        if (configuration.GetValue("DataLifecycle:Enabled", false))
        {
            services.AddHostedService(provider => new DataLifecycleWorker(
                provider.GetRequiredService<IDataLifecycleStore>(),
                new DataLifecycleOptions(
                    TimeSpan.FromDays(configuration.GetValue("DataLifecycle:ArchivedConversationRetentionDays", 180)),
                    TimeSpan.FromHours(configuration.GetValue("DataLifecycle:RunIntervalHours", 24)),
                    configuration.GetValue("DataLifecycle:BatchSize", 500))));
        }

        return services;
    }

    private static SqlServer2012Options CreateSqlOptions(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("FddDomainAgent") ??
            Environment.GetEnvironmentVariable("FDD_STORE_CONNECTION_STRING") ?? string.Empty;
        var profileValue = configuration["Persistence:Profile"] ??
            Environment.GetEnvironmentVariable("FDD_STORE_PROFILE") ?? string.Empty;
        if (!Enum.TryParse<SqlPersistenceProfile>(profileValue, ignoreCase: false, out var profile))
        {
            throw new InvalidOperationException("CONFIG_STORE_PROFILE_INVALID");
        }

        return new SqlServer2012Options(connectionString, profile);
    }

    private static ResponsesChatClientOptions CreateModelOptions(IConfiguration configuration) => new(
        RequiredUri(configuration, "Model:BaseUrl", "FDD_MODEL_BASE_URL"),
        Required(configuration, "Model:ApiKey", "FDD_MODEL_API_KEY"),
        Required(configuration, "Model:Name", "FDD_MODEL_NAME"),
        TimeSpan.FromSeconds(configuration.GetValue("Model:TimeoutSeconds", 90)),
        configuration.GetValue("Model:MaximumRetries", 1));

    private static FadadaOptions CreateFadadaOptions(IConfiguration configuration) => new(
        RequiredUri(configuration, "Fadada:BaseUrl", "FDD_FADADA_BASE_URL"),
        Required(configuration, "Fadada:AppId", "FDD_FADADA_APP_ID"),
        Required(configuration, "Fadada:AppSecret", "FDD_FADADA_APP_SECRET"),
        TimeSpan.FromSeconds(configuration.GetValue("Fadada:TimeoutSeconds", 15)),
        TimeSpan.FromSeconds(configuration.GetValue("Fadada:TokenRefreshSkewSeconds", 60)),
        configuration.GetValue("Fadada:MaximumGetRetries", 1));

    private static ModelPricing CreateModelPricing(IConfiguration configuration) => new(
        configuration.GetValue("Model:InputCostPerMillionTokens", 0m),
        configuration.GetValue("Model:OutputCostPerMillionTokens", 0m));

    private static Uri RequiredUri(IConfiguration configuration, string key, string environmentName)
    {
        var value = Required(configuration, key, environmentName);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri
            : throw new InvalidOperationException($"CONFIG_{key.Replace(':', '_').ToUpperInvariant()}_INVALID");
    }

    private static string Required(IConfiguration configuration, string key, string environmentName)
    {
        var value = configuration[key] ?? Environment.GetEnvironmentVariable(environmentName);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"CONFIG_{key.Replace(':', '_').ToUpperInvariant()}_REQUIRED");
    }
}
