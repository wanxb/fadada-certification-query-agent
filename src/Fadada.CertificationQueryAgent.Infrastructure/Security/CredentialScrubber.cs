// Redacts known secrets and common credential forms before exception text is logged or audited.
using System.Text.RegularExpressions;

namespace Fadada.CertificationQueryAgent.Infrastructure.Security;

/// <summary>
/// 从日志和诊断文本中移除凭据形态，降低秘密意外进入可观测数据的风险。
/// </summary>
public sealed partial class CredentialScrubber
{
    public string Scrub(string? value, params string?[] knownSecrets)
    {
        var scrubbed = value ?? string.Empty;
        foreach (var secret in knownSecrets.Where(secret => !string.IsNullOrWhiteSpace(secret)))
        {
            scrubbed = scrubbed.Replace(secret!, "[REDACTED]", StringComparison.Ordinal);
        }

        return BearerPattern().Replace(scrubbed, "$1[REDACTED]");
    }

    [GeneratedRegex("(?i)(bearer\\s+)[A-Za-z0-9._~+/-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();
}
