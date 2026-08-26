using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Fadada.CertificationQueryAgent.Application.Auditing;
using Fadada.CertificationQueryAgent.Application.Common;
using Fadada.CertificationQueryAgent.Application.DomainTools;
using Fadada.CertificationQueryAgent.AgentHost.Runtime;
using Fadada.CertificationQueryAgent.AgentHost.Telemetry;

namespace Fadada.CertificationQueryAgent.AgentHost.Middleware;

/// <summary>
/// 按所有权、参数来源、Schema、预算和审计顺序执行工具安全策略。
/// </summary>
public sealed class ToolPolicyPipeline(
    IConversationOwnershipVerifier ownershipVerifier,
    IUserProvenanceStore provenanceStore,
    IAuditStore auditStore,
    IRegisteredToolExecutor executor,
    AgentRuntimeOptions runtimeOptions) : IToolPolicyPipeline
{
    public static IReadOnlyList<string> OrderedPolicyIds { get; } = Array.AsReadOnly(new[]
    {
        "authenticated-principal",
        "conversation-ownership",
        "registered-tool",
        "tool-schema",
        "argument-provenance",
        "turn-budget",
        "tool-audit-gate",
        "tool-execution",
        "tool-result-sanitization",
        "post-response-evidence"
    });

    private readonly ConcurrentDictionary<TurnId, TurnBudget> budgets = new();
    private readonly int maxDomainToolCalls = ValidateOptions(runtimeOptions);

    public async ValueTask<ToolPolicyResult> InvokeAsync(
        ToolInvocationRequest request,
        CancellationToken cancellationToken)
    {
        var decisions = new List<ToolPolicyDecision>(OrderedPolicyIds.Count);
        if (request.UserId is not { } userId)
        {
            return Reject(decisions, "authenticated-principal", "AUTH_REQUIRED");
        }
        Allow(decisions, "authenticated-principal");

        if (!await ownershipVerifier.IsOwnerAsync(request.ConversationId, userId, cancellationToken))
        {
            return Reject(decisions, "conversation-ownership", "AUTH_OWNERSHIP_REJECTED");
        }
        Allow(decisions, "conversation-ownership");

        if (!DomainToolRegistry.TryGet(request.ToolName, out var tool))
        {
            return Reject(decisions, "registered-tool", "POLICY_TOOL_NOT_REGISTERED");
        }
        Allow(decisions, "registered-tool");

        if (!ToolArgumentValidator.TryValidate(tool, request.ArgumentsJson, out var arguments, out var schemaError))
        {
            return Reject(decisions, "tool-schema", schemaError);
        }
        Allow(decisions, "tool-schema");

        if (!TryCanonicalizeArguments(tool, arguments, out var canonicalArguments))
        {
            return Reject(decisions, "argument-provenance", "POLICY_PROVENANCE_REJECTED");
        }

        var candidates = arguments.Select(argument =>
            new ProvenanceCandidate(tool.Arguments[argument.Key].FieldKind, argument.Value)).ToArray();
        var activeProvenance = await provenanceStore.ResolveAsync(
            request.ConversationId,
            userId,
            candidates,
            cancellationToken);
        if (!AreArgumentsAuthorized(tool, canonicalArguments, activeProvenance, userId, request.ConversationId))
        {
            return Reject(decisions, "argument-provenance", "POLICY_PROVENANCE_REJECTED");
        }
        Allow(decisions, "argument-provenance");

        var reservation = Reserve(request.TurnId, request.ToolName, canonicalArguments);
        if (reservation is not null)
        {
            return Reject(decisions, "turn-budget", reservation);
        }
        Allow(decisions, "turn-budget");

        var auditId = request.ToolCallId.Value;
        try
        {
            await auditStore.PrewriteAsync(
                new AuditPrewrite(
                    auditId,
                    userId,
                    request.ConversationId,
                    request.TurnId,
                    $"Tool:{request.ToolName}",
                    DateTimeOffset.UtcNow,
                    string.Join(',', canonicalArguments.Keys.Order(StringComparer.Ordinal))),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Reject(decisions, "tool-audit-gate", "AUDIT_PREWRITE_FAILED");
        }
        Allow(decisions, "tool-audit-gate");

        ToolExecutionResult executionResult;
        using var toolActivity = AgentTelemetry.StartTool(request.ToolName);
        try
        {
            executionResult = await executor.ExecuteAsync(
                new ToolExecutionRequest(
                    new DomainQueryContext(
                        userId,
                        request.ConversationId,
                        request.TurnId,
                        request.ToolCallId,
                        request.TraceId),
                    request.ToolName,
                    canonicalArguments),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            AgentTelemetry.RecordTool(request.ToolName, "cancelled");
            await TryCompleteAuditAsync(auditId, AuditOperationStatus.Failed, "POLICY_TOOL_CANCELLED");
            throw;
        }
        catch
        {
            AgentTelemetry.RecordTool(request.ToolName, "failed");
            try
            {
                await auditStore.CompleteAsync(
                    new AuditCompletion(auditId, AuditOperationKind.Tool, AuditOperationStatus.Failed, "POLICY_TOOL_EXECUTION_FAILED", 0, DateTimeOffset.UtcNow),
                    cancellationToken);
            }
            catch
            {
                // The public result remains fail-closed even when completion audit also fails.
            }
            return Reject(decisions, "tool-execution", "POLICY_TOOL_EXECUTION_FAILED");
        }
        Allow(decisions, "tool-execution");

        if (executionResult.Integrity == IntegrityLabel.Secret ||
            !ToolResultSanitizer.TrySanitize(executionResult.Json, out var sanitized))
        {
            AgentTelemetry.RecordTool(request.ToolName, "rejected");
            await TryCompleteAuditAsync(auditId, AuditOperationStatus.Rejected, "POLICY_RESULT_REJECTED");
            return Reject(decisions, "tool-result-sanitization", "POLICY_RESULT_REJECTED");
        }
        Allow(decisions, "tool-result-sanitization");

        if (!ToolResultSanitizer.HasEvidenceShape(sanitized))
        {
            AgentTelemetry.RecordTool(request.ToolName, "rejected");
            await TryCompleteAuditAsync(auditId, AuditOperationStatus.Rejected, "POLICY_EVIDENCE_INVALID");
            return Reject(decisions, "post-response-evidence", "POLICY_EVIDENCE_INVALID");
        }
        try
        {
            await auditStore.CompleteAsync(
                new AuditCompletion(
                    auditId,
                    AuditOperationKind.Tool,
                    AuditOperationStatus.Succeeded,
                    null,
                    0,
                    DateTimeOffset.UtcNow,
                    "EvidenceValidated"),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Reject(decisions, "post-response-evidence", "AUDIT_COMPLETION_FAILED");
        }
        Allow(decisions, "post-response-evidence");
        AgentTelemetry.RecordTool(request.ToolName, "succeeded");
        return new ToolPolicyResult(true, sanitized, null, decisions);

        async ValueTask TryCompleteAuditAsync(
            Guid completionAuditId,
            AuditOperationStatus status,
            string errorCode)
        {
            try
            {
                await auditStore.CompleteAsync(
                    new AuditCompletion(completionAuditId, AuditOperationKind.Tool, status, errorCode, 0, DateTimeOffset.UtcNow),
                    CancellationToken.None);
            }
            catch
            {
                // A rejected public result must remain rejected even if completion audit is unavailable.
            }
        }
    }

    public void ReleaseTurn(TurnId turnId) => budgets.TryRemove(turnId, out _);

    private static bool TryCanonicalizeArguments(
        DomainToolDefinition tool,
        IReadOnlyDictionary<string, string> arguments,
        out IReadOnlyDictionary<string, string> canonicalArguments)
    {
        var canonical = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in arguments)
        {
            var definition = tool.Arguments[name];
            string normalized;
            try
            {
                normalized = ProvenanceCanonicalizer.Canonicalize(definition.FieldKind, value);
            }
            catch (ArgumentException)
            {
                canonicalArguments = new Dictionary<string, string>();
                return false;
            }

            canonical[name] = normalized;
        }

        canonicalArguments = canonical;
        return true;
    }

    private static bool AreArgumentsAuthorized(
        DomainToolDefinition tool,
        IReadOnlyDictionary<string, string> canonicalArguments,
        IReadOnlyList<UserProvidedValue> provenance,
        UserId userId,
        ConversationId conversationId) => canonicalArguments.All(argument =>
    {
        var definition = tool.Arguments[argument.Key];
        return provenance.Any(item =>
            item.UserId == userId &&
            item.ConversationId == conversationId &&
            item.FieldKind == definition.FieldKind &&
            item.Integrity == IntegrityLabel.UserAuthorized &&
            item.ConfirmationState is ConfirmationState.UserExplicit or ConfirmationState.UserConfirmed &&
            string.Equals(item.CanonicalValue, argument.Value, StringComparison.Ordinal));
    });

    private string? Reserve(
        TurnId turnId,
        string toolName,
        IReadOnlyDictionary<string, string> arguments)
    {
        var fingerprintSource = toolName + "\n" + string.Join('\n', arguments.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource)));
        var budget = budgets.GetOrAdd(turnId, _ => new TurnBudget());
        lock (budget)
        {
            if (!budget.Fingerprints.Add(fingerprint))
            {
                return "POLICY_DUPLICATE_TOOL_CALL";
            }

            if (budget.CallCount >= maxDomainToolCalls)
            {
                return "POLICY_TOOL_BUDGET_EXCEEDED";
            }

            budget.CallCount++;
            return null;
        }
    }

    private static void Allow(ICollection<ToolPolicyDecision> decisions, string policyId) =>
        decisions.Add(new ToolPolicyDecision(policyId, true, null));

    private static ToolPolicyResult Reject(
        ICollection<ToolPolicyDecision> decisions,
        string policyId,
        string errorCode)
    {
        decisions.Add(new ToolPolicyDecision(policyId, false, errorCode));
        return new ToolPolicyResult(false, null, errorCode, decisions.ToArray());
    }

    /// <summary>
    /// 维护单轮内已调用的领域工具集合和次数，阻止重复或超预算调用。
    /// </summary>
    private sealed class TurnBudget
    {
        public int CallCount { get; set; }
        public HashSet<string> Fingerprints { get; } = new(StringComparer.Ordinal);
    }

    private static int ValidateOptions(AgentRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return options.MaxDomainToolCalls;
    }
}
