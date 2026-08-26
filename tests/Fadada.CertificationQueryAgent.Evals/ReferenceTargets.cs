// Reference and seeded-regression targets calibrate whether the evaluators can detect known faults.
namespace Fadada.CertificationQueryAgent.Evals;

/// <summary>
/// 支撑离线测试中的 ExpectedReferenceTarget 职责，确保测试过程确定且不访问真实外部系统。
/// </summary>
public sealed class ExpectedReferenceTarget : IEvaluationTarget
{
    public string Name => "expected-reference";

    public ValueTask<EvaluationTargetOutput> ExecuteAsync(
        EvaluationCase scenario,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new EvaluationTargetOutput(
            scenario.ExpectedClarification,
            scenario.ExpectedToolCalls,
            scenario.ExpectedArguments,
            scenario.ExpectedEvidence,
            scenario.ExpectedSafetyDecisions,
            ModelCalls: 0,
            InputTokens: 0,
            OutputTokens: 0,
            EstimatedCost: 0m,
            DurationMilliseconds: 0));
    }
}

/// <summary>
/// 支撑离线测试中的 SeededToolRegressionTarget 职责，确保测试过程确定且不访问真实外部系统。
/// </summary>
public sealed class SeededToolRegressionTarget(IEvaluationTarget inner) : IEvaluationTarget
{
    public string Name => "seeded-tool-regression";

    public async ValueTask<EvaluationTargetOutput> ExecuteAsync(
        EvaluationCase scenario,
        CancellationToken cancellationToken)
    {
        var output = await inner.ExecuteAsync(scenario, cancellationToken);
        if (scenario.Id != "golden-001")
        {
            return output;
        }

        return output with { ToolCalls = ["query_company"] };
    }
}

/// <summary>
/// 支撑离线测试中的 SeededSafetyRegressionTarget 职责，确保测试过程确定且不访问真实外部系统。
/// </summary>
public sealed class SeededSafetyRegressionTarget(
    IEvaluationTarget inner,
    string scenarioId,
    string forbiddenTool) : IEvaluationTarget
{
    public string Name => $"seeded-safety-regression-{scenarioId}";

    public string EvaluationMode => inner.EvaluationMode;

    public bool SupportsModelQualityClaims => inner.SupportsModelQualityClaims;

    public async ValueTask<EvaluationTargetOutput> ExecuteAsync(
        EvaluationCase scenario,
        CancellationToken cancellationToken)
    {
        var output = await inner.ExecuteAsync(scenario, cancellationToken);
        if (!string.Equals(scenario.Id, scenarioId, StringComparison.Ordinal))
        {
            return output;
        }

        return output with
        {
            ToolCalls = [.. output.ToolCalls, forbiddenTool],
            EvidenceStatus = "Succeeded",
            SafetyDecisions = []
        };
    }
}
