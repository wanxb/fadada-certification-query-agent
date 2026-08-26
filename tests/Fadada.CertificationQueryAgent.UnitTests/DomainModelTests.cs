// Tests value-object invariants, normalization, and deterministic evidence conclusions.
using Fadada.CertificationQueryAgent.Domain.Errors;
using Fadada.CertificationQueryAgent.Domain.Evidence;
using Fadada.CertificationQueryAgent.Domain.Queries;
using Fadada.CertificationQueryAgent.Application.Conversations;

namespace Fadada.CertificationQueryAgent.UnitTests;

/// <summary>
/// 验证 DomainModelTests 所覆盖的行为、边界条件和安全回归约束。
/// </summary>
public sealed class DomainModelTests
{
    [Fact]
    public void ConversationTitle_UsesNormalizedFirstUserMessageAndLimitsStorageLength()
    {
        var message = "  测试用户甲，13800000018\r\n" + new string('查', 220);

        var title = ConversationTitle.FromFirstUserMessage(message);

        Assert.StartsWith("测试用户甲，13800000018 查", title, StringComparison.Ordinal);
        Assert.Equal(ConversationTitle.MaximumLength, title.Length);
        Assert.True(ConversationTitle.ForDisplay(message).Length > title.Length);
    }

    [Fact]
    public void MobileNumber_NormalizesOuterWhitespace()
    {
        Assert.Equal("13800000000", MobileNumber.Create(" 13800000000 ").Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("23800000000")]
    [InlineData("1380000000")]
    public void MobileNumber_RejectsInvalidValues(string value)
    {
        Assert.Throws<ArgumentException>(() => MobileNumber.Create(value));
    }

    [Fact]
    public void CompanyName_CollapsesWhitespace()
    {
        Assert.Equal("星河 测试有限公司", CompanyFullName.Create("  星河   测试有限公司  ").Value);
    }

    [Fact]
    public void Relationship_RequiresMatchingReliableAccountIds()
    {
        var confirmed = EvidenceRules.EvaluateRelationship(Relationship("account-1", "account-1", false, false));
        var mismatch = EvidenceRules.EvaluateRelationship(Relationship("account-1", "account-2", true, true));

        Assert.Equal(ConclusionStatus.Confirmed, confirmed.Status);
        Assert.Equal(ConclusionStatus.Mismatch, mismatch.Status);
    }

    [Fact]
    public void Relationship_AuxiliaryMatchesDoNotEstablishFact()
    {
        var conclusion = EvidenceRules.EvaluateRelationship(Relationship(null, null, true, true));

        Assert.Equal(ConclusionStatus.Unknown, conclusion.Status);
        Assert.Equal("RELATIONSHIP_EVIDENCE_INSUFFICIENT", conclusion.Code);
    }

    [Fact]
    public void SealAuthorization_UsesAccountIdSet()
    {
        Assert.Equal(
            ConclusionStatus.Confirmed,
            EvidenceRules.EvaluateSealAuthorization("account-1", ["account-2", "account-1"]).Status);
        Assert.Equal(
            ConclusionStatus.Mismatch,
            EvidenceRules.EvaluateSealAuthorization("account-1", ["account-2"]).Status);
    }

    [Fact]
    public void AggregateStatus_PreservesPartialFailure()
    {
        Assert.Equal(
            EvidenceStatus.Partial,
            EvidenceRules.AggregateStatus([EvidenceStatus.Succeeded, EvidenceStatus.Failed]));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("3")]
    [InlineData("true")]
    [InlineData("verified")]
    [InlineData("passed")]
    public void CertificationStatus_RecognizesVerifiedProviderValues(string value)
    {
        Assert.Equal(BusinessStatus.Verified, ExternalStatusNormalizer.NormalizeCertification(value));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("not_verified")]
    [InlineData("unverified")]
    public void CertificationStatus_RecognizesNotVerifiedProviderValues(string value)
    {
        Assert.Equal(BusinessStatus.NotVerified, ExternalStatusNormalizer.NormalizeCertification(value));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("active")]
    [InlineData("enabled")]
    public void OperationalStatus_RecognizesActiveProviderValues(string value)
    {
        Assert.Equal(BusinessStatus.Active, ExternalStatusNormalizer.NormalizeOperational(value));
    }

    [Fact]
    public void UnknownExternalStatus_IsNotGuessed()
    {
        Assert.Equal(BusinessStatus.Unknown, ExternalStatusNormalizer.NormalizeCertification("future-status"));
        Assert.Equal(BusinessStatus.Unknown, ExternalStatusNormalizer.NormalizeOperational("future-status"));
    }

    [Theory]
    [InlineData(DomainErrorCodes.AuthenticationRequired)]
    [InlineData(DomainErrorCodes.AgentUnavailable)]
    [InlineData(DomainErrorCodes.ToolRejected)]
    [InlineData(DomainErrorCodes.AuditUnavailable)]
    [InlineData(DomainErrorCodes.ExternalTimeout)]
    [InlineData(DomainErrorCodes.PersistenceUnavailable)]
    public void ErrorCodes_BelongToStableFamilies(string code)
    {
        Assert.True(DomainErrorCodes.IsStableFamily(code));
    }

    private static RelationshipEvidence Relationship(
        string? personAccountId,
        string? administratorAccountId,
        bool nameMatches,
        bool mobileMatches) => new(
            new PersonEvidence(personAccountId, BusinessStatus.Active, BusinessStatus.Verified, "测试甲", nameMatches),
            new CompanyEvidence(
                "company-1",
                BusinessStatus.Active,
                BusinessStatus.Verified,
                administratorAccountId is null
                    ? null
                    : new AdministratorEvidence(administratorAccountId, "测试管理员", "13800000000")),
            nameMatches,
            mobileMatches);
}
