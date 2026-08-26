// Defines the stable dataset, target-output, and metric contracts used by reports.
using System.Text.Json.Serialization;

namespace Fadada.CertificationQueryAgent.Evals;

/// <summary>
/// 支撑离线测试中的 EvaluationDataset 职责，确保测试过程确定且不访问真实外部系统。
/// </summary>
public sealed record EvaluationDataset(
    string SchemaVersion,
    string DatasetVersion,
    IReadOnlyList<EvaluationCase> Cases);

/// <summary>
/// 支撑离线测试中的 EvaluationCase 职责，确保测试过程确定且不访问真实外部系统。
/// </summary>
public sealed record EvaluationCase(
    string Id,
    string Category,
    IReadOnlyList<string> Tags,
    IReadOnlyList<EvaluationTurn> Turns,
    bool ExpectedClarification,
    IReadOnlyList<string> ExpectedToolCalls,
    IReadOnlyDictionary<string, string?> ExpectedArguments,
    string ExpectedEvidence,
    IReadOnlyList<string> ForbiddenToolCalls,
    IReadOnlyList<string> ExpectedSafetyDecisions,
    string FixtureKey,
    EvaluationOwnership Ownership,
    int Repetitions);

/// <summary>
/// 支撑离线测试中的 EvaluationTurn 职责，确保测试过程确定且不访问真实外部系统。
/// </summary>
public sealed record EvaluationTurn(string MessageId, string Role, string Content);

/// <summary>
/// 支撑离线测试中的 EvaluationOwnership 职责，确保测试过程确定且不访问真实外部系统。
/// </summary>
public sealed record EvaluationOwnership(string AuthenticatedUserId, string ConversationOwnerUserId);

/// <summary>
/// 支撑离线测试中的 FixtureDataset 职责，确保测试过程确定且不访问真实外部系统。
/// </summary>
public sealed record FixtureDataset(
    string SchemaVersion,
    IReadOnlyDictionary<string, FixtureResponse> Responses);

/// <summary>
/// 支撑离线测试中的 FixtureResponse 职责，确保测试过程确定且不访问真实外部系统。
/// </summary>
public sealed record FixtureResponse(
    string EvidenceStatus,
    IReadOnlyList<string> SourceEndpointKeys,
    string? SafeErrorCode);

/// <summary>
/// 支撑离线测试中的 EvaluationTargetOutput 职责，确保测试过程确定且不访问真实外部系统。
/// </summary>
public sealed record EvaluationTargetOutput(
    bool ClarificationRequested,
    IReadOnlyList<string> ToolCalls,
    IReadOnlyDictionary<string, string?> Arguments,
    string EvidenceStatus,
    IReadOnlyList<string> SafetyDecisions,
    int ModelCalls,
    int InputTokens,
    int OutputTokens,
    decimal EstimatedCost,
    long DurationMilliseconds,
    bool MafEvaluationPassed = true,
    bool MeaiEvaluationPassed = true);

/// <summary>
/// 支撑离线测试中的 CaseEvaluation 职责，确保测试过程确定且不访问真实外部系统。
/// </summary>
public sealed record CaseEvaluation(
    string Id,
    string Category,
    bool Passed,
    bool ClarificationPassed,
    bool ToolCallsPassed,
    bool ArgumentsPassed,
    bool EvidencePassed,
    bool SafetyPassed,
    bool FrameworkEvaluationPassed,
    IReadOnlyList<string> Failures,
    EvaluationTargetOutput Actual);

/// <summary>
/// 支撑离线测试中的 EvaluationMetrics 职责，确保测试过程确定且不访问真实外部系统。
/// </summary>
public sealed record EvaluationMetrics(
    int TotalCases,
    int PassedCases,
    decimal PassRate,
    int SafetyViolations,
    int InvalidToolCalls,
    int ToolCalls,
    int ModelCalls,
    int InputTokens,
    int OutputTokens,
    decimal EstimatedCost,
    long DurationMilliseconds,
    long P95DurationMilliseconds,
    decimal ClarificationAccuracy,
    decimal ToolSelectionAccuracy,
    decimal ArgumentAccuracy,
    decimal GroundednessRate,
    decimal FrameworkEvaluationRate);

/// <summary>
/// 支撑离线测试中的 EvaluationReport 职责，确保测试过程确定且不访问真实外部系统。
/// </summary>
public sealed record EvaluationReport(
    string SchemaVersion,
    string DatasetVersion,
    string Target,
    string EvaluationMode,
    bool SupportsModelQualityClaims,
    DateTimeOffset GeneratedAtUtc,
    EvaluationMetrics Metrics,
    IReadOnlyList<CaseEvaluation> Cases);

/// <summary>
/// 支撑离线测试中的 IEvaluationTarget 职责，确保测试过程确定且不访问真实外部系统。
/// </summary>
public interface IEvaluationTarget
{
    string Name { get; }

    string EvaluationMode => "deterministic";

    bool SupportsModelQualityClaims => false;

    ValueTask<EvaluationTargetOutput> ExecuteAsync(
        EvaluationCase scenario,
        CancellationToken cancellationToken);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(EvaluationDataset))]
[JsonSerializable(typeof(FixtureDataset))]
[JsonSerializable(typeof(EvaluationReport))]
/// <summary>
/// 支撑离线测试中的 EvaluationJsonContext 职责，确保测试过程确定且不访问真实外部系统。
/// </summary>
internal sealed partial class EvaluationJsonContext : JsonSerializerContext;
