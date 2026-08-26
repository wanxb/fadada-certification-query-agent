// Maps known provider statuses conservatively; unfamiliar values remain Unknown instead of guessed.
namespace Fadada.CertificationQueryAgent.Domain.Evidence;

/// <summary>
/// 将 ExternalStatusNormalizer 负责的输入转换为稳定规范形式，保证比较和策略判断具有一致语义。
/// </summary>
public static class ExternalStatusNormalizer
{
    private static readonly IReadOnlyDictionary<string, BusinessStatus> CertificationStatuses =
        new Dictionary<string, BusinessStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["0"] = BusinessStatus.NotVerified,
            ["1"] = BusinessStatus.Verified,
            ["2"] = BusinessStatus.InProgress,
            ["3"] = BusinessStatus.Verified,
            ["true"] = BusinessStatus.Verified,
            ["false"] = BusinessStatus.NotVerified,
            ["verified"] = BusinessStatus.Verified,
            ["certified"] = BusinessStatus.Verified,
            ["success"] = BusinessStatus.Verified,
            ["passed"] = BusinessStatus.Verified,
            ["not_verified"] = BusinessStatus.NotVerified,
            ["notverified"] = BusinessStatus.NotVerified,
            ["unverified"] = BusinessStatus.NotVerified,
            ["uncertified"] = BusinessStatus.NotVerified,
            ["pending"] = BusinessStatus.InProgress,
            ["processing"] = BusinessStatus.InProgress,
            ["in_progress"] = BusinessStatus.InProgress,
            ["inprogress"] = BusinessStatus.InProgress,
            ["unregistered"] = BusinessStatus.Unregistered,
            ["not_found"] = BusinessStatus.NotFound,
            ["failed"] = BusinessStatus.Failed
        };

    private static readonly IReadOnlyDictionary<string, BusinessStatus> OperationalStatuses =
        new Dictionary<string, BusinessStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["0"] = BusinessStatus.Active,
            ["true"] = BusinessStatus.Active,
            ["false"] = BusinessStatus.Inactive,
            ["active"] = BusinessStatus.Active,
            ["enabled"] = BusinessStatus.Active,
            ["normal"] = BusinessStatus.Active,
            ["valid"] = BusinessStatus.Active,
            ["inactive"] = BusinessStatus.Inactive,
            ["disabled"] = BusinessStatus.Inactive,
            ["invalid"] = BusinessStatus.Inactive,
            ["pending"] = BusinessStatus.InProgress,
            ["processing"] = BusinessStatus.InProgress,
            ["in_progress"] = BusinessStatus.InProgress,
            ["inprogress"] = BusinessStatus.InProgress,
            ["not_found"] = BusinessStatus.NotFound,
            ["failed"] = BusinessStatus.Failed
        };

    public static BusinessStatus NormalizeCertification(string? rawStatus) =>
        Normalize(rawStatus, CertificationStatuses);

    public static BusinessStatus NormalizeOperational(string? rawStatus) =>
        Normalize(rawStatus, OperationalStatuses);

    private static BusinessStatus Normalize(
        string? rawStatus,
        IReadOnlyDictionary<string, BusinessStatus> statuses) =>
        !string.IsNullOrWhiteSpace(rawStatus) && statuses.TryGetValue(rawStatus.Trim(), out var status)
            ? status
            : BusinessStatus.Unknown;
}
