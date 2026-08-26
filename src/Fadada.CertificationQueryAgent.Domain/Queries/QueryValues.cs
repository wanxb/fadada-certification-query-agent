// Value objects validate query identifiers at construction so invalid data cannot reach adapters.
using System.Text.RegularExpressions;

namespace Fadada.CertificationQueryAgent.Domain.Queries;

/// <summary>
/// 以强类型值表示 MobileNumber，防止不同业务标识在编译期被误用或混传。
/// </summary>
public readonly partial record struct MobileNumber
{
    private MobileNumber(string value) => Value = value;

    public string Value { get; }

    public static MobileNumber Create(string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (!MobilePattern().IsMatch(normalized))
        {
            throw new ArgumentException("Mobile number must be an 11-digit mainland number.", nameof(value));
        }

        return new MobileNumber(normalized);
    }

    public override string ToString() => Value;

    [GeneratedRegex("^1[0-9]{10}$", RegexOptions.CultureInvariant)]
    private static partial Regex MobilePattern();
}

/// <summary>
/// 以强类型值表示 PersonName，防止不同业务标识在编译期被误用或混传。
/// </summary>
public readonly partial record struct PersonName
{
    private PersonName(string value) => Value = value;

    public string Value { get; }

    public static PersonName Create(string value)
    {
        var normalized = NormalizeSpaces(value);
        if (normalized.Length is < 2 or > 100)
        {
            throw new ArgumentException("Person name length is invalid.", nameof(value));
        }

        return new PersonName(normalized);
    }

    public override string ToString() => Value;

    private static string NormalizeSpaces(string? value) =>
        Whitespace().Replace(value?.Trim() ?? string.Empty, " ");

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}

/// <summary>
/// 以强类型值表示 CompanyFullName，防止不同业务标识在编译期被误用或混传。
/// </summary>
public readonly partial record struct CompanyFullName
{
    private CompanyFullName(string value) => Value = value;

    public string Value { get; }

    public static CompanyFullName Create(string value)
    {
        var normalized = Whitespace().Replace(value?.Trim() ?? string.Empty, " ");
        if (normalized.Length is < 2 or > 256)
        {
            throw new ArgumentException("Company full name length is invalid.", nameof(value));
        }

        return new CompanyFullName(normalized);
    }

    public override string ToString() => Value;

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
