using System.Diagnostics;
using System.Runtime.CompilerServices;
using Fadada.CertificationQueryAgent.AgentHost.Runtime;
using Fadada.CertificationQueryAgent.Application.Auditing;
using Fadada.CertificationQueryAgent.Application.Common;
using Fadada.CertificationQueryAgent.AgentHost.Telemetry;
using Microsoft.Extensions.AI;

namespace Fadada.CertificationQueryAgent.AgentHost.Middleware;

/// <summary>
/// 为模型调用执行先写后调的结构化审计，并记录令牌、成本和安全终态。
/// </summary>
internal sealed class ModelCallAuditChatClient : DelegatingChatClient
{
    private readonly IModelCallAuditStore auditStore;
    private readonly string provider;
    private readonly string modelName;
    private readonly ModelPricing pricing;

    public ModelCallAuditChatClient(
        IChatClient innerClient,
        IModelCallAuditStore auditStore,
        ModelPricing? pricing = null)
        : base(innerClient)
    {
        this.auditStore = auditStore ?? throw new ArgumentNullException(nameof(auditStore));
        var metadata = innerClient.GetService<ChatClientMetadata>();
        provider = SafeMetadata(metadata?.ProviderName, "configured-provider", 64);
        modelName = SafeMetadata(metadata?.DefaultModelId, "configured-model", 128);
        this.pricing = pricing ?? new ModelPricing(0, 0);
        this.pricing.Validate();
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var scope = await StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            await CompleteAsync(
                scope,
                AuditOperationStatus.Succeeded,
                response.Usage?.InputTokenCount ?? 0,
                response.Usage?.OutputTokenCount ?? 0,
                null,
                cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException)
        {
            await TryCompleteFailureAsync(scope, AuditOperationStatus.Cancelled, "MODEL_CANCELLED").ConfigureAwait(false);
            throw;
        }
        catch
        {
            await TryCompleteFailureAsync(scope, AuditOperationStatus.Failed, "MODEL_CALL_FAILED").ConfigureAwait(false);
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var scope = await StartAsync(cancellationToken).ConfigureAwait(false);
        var completed = false;
        long inputTokens = 0;
        long outputTokens = 0;
        await using var enumerator = base
            .GetStreamingResponseAsync(messages, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        try
        {
            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                foreach (var usage in enumerator.Current.Contents.OfType<UsageContent>())
                {
                    inputTokens = usage.Details.InputTokenCount ?? inputTokens;
                    outputTokens = usage.Details.OutputTokenCount ?? outputTokens;
                }

                yield return enumerator.Current;
            }

            completed = true;
        }
        finally
        {
            await CompleteAsync(
                scope,
                completed ? AuditOperationStatus.Succeeded :
                    cancellationToken.IsCancellationRequested ? AuditOperationStatus.Cancelled : AuditOperationStatus.Failed,
                inputTokens,
                outputTokens,
                completed ? null : cancellationToken.IsCancellationRequested ? "MODEL_CANCELLED" : "MODEL_STREAM_INCOMPLETE",
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async ValueTask<AuditScope> StartAsync(CancellationToken cancellationToken)
    {
        var context = AgentTurnContextAccessor.Current;
        var scope = new AuditScope(Guid.NewGuid(), context.Request.TurnId, Stopwatch.StartNew());
        await auditStore.PrewriteAsync(
            new ModelCallAuditStart(
                scope.Id,
                context.Request.UserId,
                context.Request.ConversationId,
                context.Request.TurnId,
                context.ModelCalls + 1,
                provider,
                modelName,
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
        return scope;
    }

    private async ValueTask CompleteAsync(
        AuditScope scope,
        AuditOperationStatus status,
        long inputTokens,
        long outputTokens,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        scope.Stopwatch.Stop();
        var estimatedCost = pricing.Estimate(inputTokens, outputTokens);
        await auditStore.CompleteAsync(
            new ModelCallAuditCompletion(
                scope.Id,
                scope.TurnId,
                status,
                checked((int)Math.Min(inputTokens, int.MaxValue)),
                checked((int)Math.Min(outputTokens, int.MaxValue)),
                estimatedCost,
                scope.Stopwatch.ElapsedMilliseconds,
                errorCode,
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
        AgentTelemetry.RecordModelCall(inputTokens, outputTokens, estimatedCost, status.ToString());
    }

    private async ValueTask TryCompleteFailureAsync(
        AuditScope scope,
        AuditOperationStatus status,
        string errorCode)
    {
        try
        {
            await CompleteAsync(scope, status, 0, 0, errorCode, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original model failure without exposing persistence details.
        }
    }

    private static string SafeMetadata(string? value, string fallback, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Length <= maximumLength ? value : value[..maximumLength];

    /// <summary>
    /// 关联一次模型调用的审计标识、轮次和耗时，确保开始与完成记录成对写入。
    /// </summary>
    private sealed record AuditScope(Guid Id, TurnId TurnId, Stopwatch Stopwatch);
}
