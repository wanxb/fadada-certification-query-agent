// The immutable registry is the sole allowlist of tool names, descriptions, and accepted arguments.
using System.Collections.Frozen;
using System.Text.Json;

namespace Fadada.CertificationQueryAgent.Application.DomainTools;

/// <summary>
/// 以不可变数据契约表达 ToolArgumentDefinition，确保跨层传递时字段语义和边界保持稳定。
/// </summary>
public sealed record ToolArgumentDefinition(
    string Name,
    ProvenanceFieldKind FieldKind,
    bool Required);

/// <summary>
/// 以不可变数据契约表达 DomainToolDefinition，确保跨层传递时字段语义和边界保持稳定。
/// </summary>
public sealed record DomainToolDefinition(
    string Name,
    string Description,
    IReadOnlyDictionary<string, ToolArgumentDefinition> Arguments,
    string JsonSchema);

/// <summary>
/// 集中维护 DomainToolRegistry 的受控定义集合，禁止运行时接纳未注册的自由输入。
/// </summary>
public static class DomainToolRegistry
{
    private static readonly FrozenDictionary<string, DomainToolDefinition> Tools =
        new[]
        {
            Define("query_person", "Query person account and verification evidence.",
                new ToolArgumentDefinition("mobile", ProvenanceFieldKind.Mobile, true),
                new ToolArgumentDefinition("claimedName", ProvenanceFieldKind.PersonName, false)),
            Define("query_company", "Query company and verification evidence.",
                new ToolArgumentDefinition("companyFullName", ProvenanceFieldKind.CompanyFullName, true)),
            Define("query_relationship", "Query person verification, company verification, and their administrator relationship as one combined evidence operation. Prefer this tool when a request includes a mobile number, a company full name, and any relationship or administrator question.",
                new ToolArgumentDefinition("mobile", ProvenanceFieldKind.Mobile, true),
                new ToolArgumentDefinition("companyFullName", ProvenanceFieldKind.CompanyFullName, true),
                new ToolArgumentDefinition("claimedName", ProvenanceFieldKind.PersonName, false)),
            Define("query_seals", "Query company seals and optional person authorization evidence.",
                new ToolArgumentDefinition("companyFullName", ProvenanceFieldKind.CompanyFullName, true),
                new ToolArgumentDefinition("mobile", ProvenanceFieldKind.Mobile, false))
        }.ToFrozenDictionary(tool => tool.Name, StringComparer.Ordinal);

    public static IReadOnlyCollection<DomainToolDefinition> All { get; } =
        Array.AsReadOnly(Tools.Values.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray());

    public static bool TryGet(string name, out DomainToolDefinition definition) =>
        Tools.TryGetValue(name, out definition!);

    private static DomainToolDefinition Define(
        string name,
        string description,
        params ToolArgumentDefinition[] arguments)
    {
        var properties = arguments.ToDictionary(
            argument => argument.Name,
            _ => (object)new Dictionary<string, object>
            {
                ["type"] = "string",
                ["minLength"] = 1
            },
            StringComparer.Ordinal);
        foreach (var argument in arguments.Where(argument => !argument.Required))
        {
            properties[argument.Name] = new Dictionary<string, object>
            {
                ["type"] = new[] { "string", "null" },
                ["minLength"] = 1
            };
        }

        var schema = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = properties,
            ["required"] = arguments.Select(argument => argument.Name).ToArray()
        });
        return new DomainToolDefinition(
            name,
            description,
            arguments.ToFrozenDictionary(argument => argument.Name, StringComparer.Ordinal),
            schema);
    }
}
