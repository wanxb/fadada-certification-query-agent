// Keeps runtime budgets within the release-tested ceilings; configuration may only tighten them.
using Fadada.CertificationQueryAgent.Application.AgentTurns;

namespace Fadada.CertificationQueryAgent.AgentHost.Runtime;

/// <summary>
/// 集中配置 Agent 单轮预算，并限制配置只能收紧已经过测试的发布上限。
/// </summary>
public sealed record AgentRuntimeOptions
{
    public const int DefaultMaxModelCalls = AgentExecutionLimits.MaximumModelCallsPerTurn;
    public const int DefaultMaxDomainToolCalls = AgentExecutionLimits.MaximumDomainToolCallsPerTurn;

    public int MaxModelCalls { get; init; } = DefaultMaxModelCalls;

    public int MaxDomainToolCalls { get; init; } = DefaultMaxDomainToolCalls;

    internal void Validate()
    {
        if (MaxModelCalls is < 1 or > DefaultMaxModelCalls)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxModelCalls),
                $"The model-call budget must be between 1 and {DefaultMaxModelCalls}.");
        }

        if (MaxDomainToolCalls is < 0 or > DefaultMaxDomainToolCalls)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxDomainToolCalls),
                $"The domain-tool budget must be between 0 and {DefaultMaxDomainToolCalls}.");
        }
    }
}

/// <summary>
/// 表示 Agent 运行期可安全映射的失败，仅向调用方暴露稳定错误码。
/// </summary>
public sealed class AgentRuntimeException : Exception
{
    public AgentRuntimeException(string errorCode)
        : base(errorCode)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
