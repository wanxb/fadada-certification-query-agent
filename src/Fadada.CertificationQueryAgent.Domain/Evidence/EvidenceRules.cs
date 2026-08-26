// Deterministic rules derive conclusions from reliable identifiers rather than model interpretation.
namespace Fadada.CertificationQueryAgent.Domain.Evidence;

/// <summary>
/// 集中执行 EvidenceRules 的确定性规则，避免关键判断依赖模型或调用方约定。
/// </summary>
public static class EvidenceRules
{
    public static DeterministicConclusion EvaluateRelationship(RelationshipEvidence evidence)
    {
        var personAccountId = NormalizeId(evidence.Person.AccountId);
        var administratorAccountId = NormalizeId(evidence.Company.Administrator?.AccountId);
        if (personAccountId is not null && administratorAccountId is not null)
        {
            return string.Equals(personAccountId, administratorAccountId, StringComparison.Ordinal)
                ? new DeterministicConclusion(ConclusionStatus.Confirmed, "RELATIONSHIP_CONFIRMED", "Reliable account identifiers match.")
                : new DeterministicConclusion(ConclusionStatus.Mismatch, "RELATIONSHIP_MISMATCH", "Reliable account identifiers do not match.");
        }

        return new DeterministicConclusion(
            ConclusionStatus.Unknown,
            "RELATIONSHIP_EVIDENCE_INSUFFICIENT",
            "Auxiliary name or mobile evidence cannot establish the relationship.");
    }

    public static DeterministicConclusion EvaluateSealAuthorization(
        string? personAccountId,
        IEnumerable<string> authorizedAccountIds)
    {
        var normalizedPersonId = NormalizeId(personAccountId);
        if (normalizedPersonId is null)
        {
            return new DeterministicConclusion(
                ConclusionStatus.Unknown,
                "SEAL_AUTHORIZATION_EVIDENCE_INSUFFICIENT",
                "A reliable person account identifier is required.");
        }

        var ids = authorizedAccountIds
            .Select(NormalizeId)
            .Where(id => id is not null)
            .ToHashSet(StringComparer.Ordinal);
        return ids.Contains(normalizedPersonId)
            ? new DeterministicConclusion(ConclusionStatus.Confirmed, "SEAL_AUTHORIZATION_CONFIRMED", "The person account is authorized.")
            : new DeterministicConclusion(ConclusionStatus.Mismatch, "SEAL_AUTHORIZATION_NOT_FOUND", "The person account is not in the authorization set.");
    }

    public static EvidenceStatus AggregateStatus(IEnumerable<EvidenceStatus> statuses)
    {
        var values = statuses.ToArray();
        if (values.Length == 0)
        {
            return EvidenceStatus.NotFound;
        }

        if (values.All(status => status == EvidenceStatus.Succeeded))
        {
            return EvidenceStatus.Succeeded;
        }

        if (values.All(status => status == EvidenceStatus.Failed))
        {
            return EvidenceStatus.Failed;
        }

        return values.Any(status => status == EvidenceStatus.Succeeded)
            ? EvidenceStatus.Partial
            : values[0];
    }

    private static string? NormalizeId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
