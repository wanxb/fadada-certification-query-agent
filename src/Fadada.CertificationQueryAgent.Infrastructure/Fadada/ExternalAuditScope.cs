// Guarantees each external request has a started audit and exactly one terminal audit outcome.
using System.Diagnostics;
using Fadada.CertificationQueryAgent.Application.Auditing;
using Fadada.CertificationQueryAgent.Application.DomainTools;

namespace Fadada.CertificationQueryAgent.Infrastructure.Fadada;

/// <summary>
/// 封装外部调用的先写审计生命周期，保证成功和异常路径都记录终态。
/// </summary>
internal sealed class ExternalAuditScope
{
    private readonly IAuditStore auditStore;
    private readonly Guid auditId;
    private readonly Stopwatch stopwatch;

    private ExternalAuditScope(IAuditStore auditStore, Guid auditId)
    {
        this.auditStore = auditStore;
        this.auditId = auditId;
        stopwatch = Stopwatch.StartNew();
    }

    public static async ValueTask<ExternalAuditScope> StartAsync(
        IAuditStore auditStore,
        DomainQueryContext context,
        FadadaEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        var auditId = Guid.NewGuid();
        await auditStore.PrewriteAsync(
            new AuditPrewrite(
                auditId,
                context.UserId,
                context.ConversationId,
                context.TurnId,
                $"Fadada:{endpoint.Key}:{endpoint.Method.Method}",
                DateTimeOffset.UtcNow,
                ParentToolCallId: context.ToolCallId),
            cancellationToken);
        return new ExternalAuditScope(auditStore, auditId);
    }

    public async ValueTask CompleteAsync(
        AuditOperationStatus status,
        string? safeErrorCode,
        CancellationToken cancellationToken)
    {
        stopwatch.Stop();
        await auditStore.CompleteAsync(
            new AuditCompletion(
                auditId,
                AuditOperationKind.External,
                status,
                safeErrorCode,
                stopwatch.ElapsedMilliseconds,
                DateTimeOffset.UtcNow),
            cancellationToken);
    }
}
