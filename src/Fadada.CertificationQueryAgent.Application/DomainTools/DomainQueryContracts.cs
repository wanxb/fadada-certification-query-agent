// Coarse-grained read-only tool contracts keep provider call sequences deterministic and model-independent.
using Fadada.CertificationQueryAgent.Application.Common;
using Fadada.CertificationQueryAgent.Domain.Evidence;
using Fadada.CertificationQueryAgent.Domain.Queries;

namespace Fadada.CertificationQueryAgent.Application.DomainTools;

/// <summary>
/// 以不可变数据契约表达 DomainQueryContext，确保跨层传递时字段语义和边界保持稳定。
/// </summary>
public sealed record DomainQueryContext(
    UserId UserId,
    ConversationId ConversationId,
    TurnId TurnId,
    ToolCallId ToolCallId,
    Guid TraceId);

/// <summary>
/// 定义 IDomainQueryService 的稳定端口，使应用逻辑不依赖具体基础设施并便于替换测试实现。
/// </summary>
public interface IDomainQueryService
{
    ValueTask<EvidenceEnvelope<PersonEvidence>> QueryPersonAsync(
        DomainQueryContext context,
        PersonQuery query,
        CancellationToken cancellationToken);

    ValueTask<EvidenceEnvelope<CompanyEvidence>> QueryCompanyAsync(
        DomainQueryContext context,
        CompanyQuery query,
        CancellationToken cancellationToken);

    ValueTask<EvidenceEnvelope<RelationshipEvidence>> QueryRelationshipAsync(
        DomainQueryContext context,
        RelationshipQuery query,
        CancellationToken cancellationToken);

    ValueTask<EvidenceEnvelope<SealsEvidence>> QuerySealsAsync(
        DomainQueryContext context,
        SealsQuery query,
        CancellationToken cancellationToken);
}
