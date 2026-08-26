// Canonicalization makes exact provenance comparison stable without inventing values absent from user input.
using System.Text.RegularExpressions;
using Fadada.CertificationQueryAgent.Domain.Queries;

namespace Fadada.CertificationQueryAgent.Application.DomainTools;

/// <summary>
/// 将 ProvenanceCanonicalizer 负责的输入转换为稳定规范形式，保证比较和策略判断具有一致语义。
/// </summary>
public static partial class ProvenanceCanonicalizer
{
    public static string Canonicalize(ProvenanceFieldKind fieldKind, string value) => fieldKind switch
    {
        ProvenanceFieldKind.Mobile => MobileNumber.Create(value).Value,
        ProvenanceFieldKind.PersonName => PersonName.Create(NormalizePunctuation(value)).Value,
        ProvenanceFieldKind.CompanyFullName => CompanyFullName.Create(NormalizePunctuation(value)).Value,
        _ => throw new ArgumentOutOfRangeException(nameof(fieldKind))
    };

    public static bool IsPresentInUserText(
        ProvenanceFieldKind fieldKind,
        string canonicalValue,
        string userText) => fieldKind switch
    {
        ProvenanceFieldKind.Mobile => MobileCandidatePattern()
            .Matches(userText)
            .Select(match => NonDigit().Replace(match.Value, string.Empty))
            .Any(value => string.Equals(value, canonicalValue, StringComparison.Ordinal)),
        ProvenanceFieldKind.PersonName or ProvenanceFieldKind.CompanyFullName =>
            NormalizePunctuation(userText).Contains(canonicalValue, StringComparison.Ordinal),
        _ => false
    };

    private static string NormalizePunctuation(string value) => Whitespace().Replace(
        value.Trim()
            .Replace('\u3000', ' ')
            .Replace('：', ':')
            .Replace('，', ','),
        " ");

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex("(?<![0-9])1(?:[\\s-]?[0-9]){10}(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex MobileCandidatePattern();

    [GeneratedRegex("[^0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex NonDigit();
}
