// Protects application-port shapes from accidental provider or infrastructure coupling.
using System.Reflection;
using Fadada.CertificationQueryAgent.Application.AgentTurns;
using Fadada.CertificationQueryAgent.Application.Authentication;
using Fadada.CertificationQueryAgent.Application.Auditing;
using Fadada.CertificationQueryAgent.Application.Common;
using Fadada.CertificationQueryAgent.Application.Conversations;
using Fadada.CertificationQueryAgent.Application.DomainTools;
using Fadada.CertificationQueryAgent.Application.Persistence;

namespace Fadada.CertificationQueryAgent.ContractTests;

/// <summary>
/// 验证 ApplicationPortContractTests 所覆盖的行为、边界条件和安全回归约束。
/// </summary>
public sealed class ApplicationPortContractTests
{
    private static readonly Type[] PortTypes =
    [
        typeof(IAuthenticationService),
        typeof(IAccountAdministrationService),
        typeof(IUserStore),
        typeof(IConversationStore),
        typeof(IAgentTurnStore),
        typeof(IAgentRuntime),
        typeof(IModelRuntime),
        typeof(IDomainQueryService),
        typeof(IAuditStore),
        typeof(IModelCallAuditStore),
        typeof(IAgentSessionStateStore),
        typeof(IDiagnosticPayloadStore),
        typeof(IDiagnosticCaptureService),
        typeof(IDataLifecycleStore)
    ];

    [Fact]
    public void EveryAsyncPortMethod_RequiresCancellationToken()
    {
        var violations = PortTypes
            .SelectMany(type => type.GetMethods())
            .Where(method => method.GetParameters().LastOrDefault()?.ParameterType != typeof(CancellationToken))
            .Select(method => $"{method.DeclaringType?.Name}.{method.Name}")
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void UserScopedReads_RequireExplicitUserId()
    {
        var scopedReads = new[]
        {
            Method<IConversationStore>(nameof(IConversationStore.GetAsync)),
            Method<IUserStore>(nameof(IUserStore.GetByIdAsync)),
            Method<IAgentSessionStateStore>(nameof(IAgentSessionStateStore.GetAsync)),
            Method<IDiagnosticPayloadStore>(nameof(IDiagnosticPayloadStore.GetAsync))
        };

        Assert.All(scopedReads, method =>
            Assert.Contains(method.GetParameters(), parameter => parameter.ParameterType == typeof(UserId)));
    }

    [Fact]
    public void DomainQueryPort_ExposesExactlyFourCoarseQueries()
    {
        Assert.Equal(
            ["QueryCompanyAsync", "QueryPersonAsync", "QueryRelationshipAsync", "QuerySealsAsync"],
            typeof(IDomainQueryService).GetMethods().Select(method => method.Name).Order().ToArray());
    }

    private static MethodInfo Method<T>(string name) =>
        typeof(T).GetMethod(name) ?? throw new InvalidOperationException($"Method '{name}' was not found.");
}
