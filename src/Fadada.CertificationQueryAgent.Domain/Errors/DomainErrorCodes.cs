// Stable public codes expose actionable failure categories without leaking provider or database details.
namespace Fadada.CertificationQueryAgent.Domain.Errors;

/// <summary>
/// 集中定义可跨层传播的领域安全错误码，禁止业务层直接暴露外部接口或数据库异常文本。
/// </summary>
public static class DomainErrorCodes
{
    public const string AuthenticationRequired = "AUTH_REQUIRED";
    public const string OwnershipRejected = "AUTH_OWNERSHIP_REJECTED";
    public const string AgentUnavailable = "AGENT_UNAVAILABLE";
    public const string ToolRejected = "POLICY_TOOL_REJECTED";
    public const string ProvenanceRejected = "POLICY_PROVENANCE_REJECTED";
    public const string AuditUnavailable = "AUDIT_UNAVAILABLE";
    public const string ExternalTimeout = "FDD_TIMEOUT";
    public const string ExternalRejected = "FDD_REJECTED";
    public const string PersistenceUnavailable = "PERSISTENCE_UNAVAILABLE";

    public static bool IsStableFamily(string code) =>
        code.StartsWith("AUTH_", StringComparison.Ordinal) ||
        code.StartsWith("AGENT_", StringComparison.Ordinal) ||
        code.StartsWith("POLICY_", StringComparison.Ordinal) ||
        code.StartsWith("AUDIT_", StringComparison.Ordinal) ||
        code.StartsWith("FDD_", StringComparison.Ordinal) ||
        code.StartsWith("PERSISTENCE_", StringComparison.Ordinal);
}
