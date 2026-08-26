// Loads the versioned embedded prompt and exposes its digest for audit correlation.
using System.Security.Cryptography;
using System.Text;

namespace Fadada.CertificationQueryAgent.AgentHost.Prompts;

/// <summary>
/// 承载带版本和哈希的系统提示词，使运行审计能够精确追踪提示契约。
/// </summary>
public sealed record DomainQueryPrompt(string Version, string Content, string Sha256)
{
    public const string CurrentVersion = "query-agent.v2";

    public static DomainQueryPrompt LoadCurrent()
    {
        var assembly = typeof(DomainQueryPrompt).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(
            name => name.EndsWith("Prompts.query-agent.v2.md", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The embedded query Agent prompt was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var content = reader.ReadToEnd();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        return new DomainQueryPrompt(CurrentVersion, content, hash);
    }
}
