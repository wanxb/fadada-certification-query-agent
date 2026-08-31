// Adapts the closed domain-tool registry to Microsoft.Extensions.AI function contracts.
using System.ComponentModel;
using System.Text.Json;
using Fadada.CertificationQueryAgent.AgentHost.Runtime;
using Fadada.CertificationQueryAgent.Application.Common;
using Fadada.CertificationQueryAgent.Application.DomainTools;
using Microsoft.Extensions.AI;

namespace Fadada.CertificationQueryAgent.AgentHost.Tools;

/// <summary>
/// 向模型暴露固定的四个领域函数，并把每次调用送入确定性策略管线。
/// </summary>
internal sealed class DomainAgentFunctions(IToolPolicyPipeline policyPipeline)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<AIFunction> CreateTools() =>
    [
        Create(
            (Func<string, string?, CancellationToken, ValueTask<string>>)QueryPersonAsync,
            "query_person",
            "Query person account and verification evidence."),
        Create(
            (Func<string, CancellationToken, ValueTask<string>>)QueryCompanyAsync,
            "query_company",
            "Query company and verification evidence."),
        Create(
            (Func<string, string, string?, CancellationToken, ValueTask<string>>)QueryRelationshipAsync,
            "query_relationship",
            "Query person verification, company verification, and their administrator relationship as one combined evidence operation. Prefer this tool when a request includes a mobile number, a company full name, and any relationship or administrator question."),
        Create(
            (Func<string, string?, CancellationToken, ValueTask<string>>)QuerySealsAsync,
            "query_seals",
            "Query company seals, each seal's authorized users, and optional authorization evidence for one mobile number.")
    ];

    private static AIFunction Create(Delegate method, string name, string description)
    {
        var inner = AIFunctionFactory.Create(method, new AIFunctionFactoryOptions
        {
            Name = name,
            Description = description,
            ExcludeResultSchema = true
        });
        if (!DomainToolRegistry.TryGet(name, out var definition))
        {
            throw new InvalidOperationException($"Tool '{name}' is not registered.");
        }

        using var document = JsonDocument.Parse(definition.JsonSchema);
        return new StrictSchemaFunction(inner, document.RootElement.Clone());
    }

    private ValueTask<string> QueryPersonAsync(
        [Description("Mobile number explicitly supplied or confirmed by the user.")] string mobile,
        [Description("Optional person name explicitly supplied or confirmed by the user.")] string? claimedName = null,
        CancellationToken cancellationToken = default) =>
        InvokeAsync("query_person", Required("mobile", mobile), Optional("claimedName", claimedName), cancellationToken);

    private ValueTask<string> QueryCompanyAsync(
        [Description("Company full legal name explicitly supplied or confirmed by the user.")] string companyFullName,
        CancellationToken cancellationToken = default) =>
        InvokeAsync("query_company", Required("companyFullName", companyFullName), null, cancellationToken);

    private ValueTask<string> QueryRelationshipAsync(
        [Description("Mobile number explicitly supplied or confirmed by the user.")] string mobile,
        [Description("Company full legal name explicitly supplied or confirmed by the user.")] string companyFullName,
        [Description("Optional person name explicitly supplied or confirmed by the user.")] string? claimedName = null,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(
            "query_relationship",
            Required("mobile", mobile, "companyFullName", companyFullName),
            Optional("claimedName", claimedName),
            cancellationToken);

    private ValueTask<string> QuerySealsAsync(
        [Description("Company full legal name explicitly supplied or confirmed by the user.")] string companyFullName,
        [Description("Optional mobile number explicitly supplied or confirmed by the user.")] string? mobile = null,
        CancellationToken cancellationToken = default) =>
        InvokeAsync("query_seals", Required("companyFullName", companyFullName), Optional("mobile", mobile), cancellationToken);

    private async ValueTask<string> InvokeAsync(
        string toolName,
        Dictionary<string, string> required,
        KeyValuePair<string, string>? optional,
        CancellationToken cancellationToken)
    {
        var context = AgentTurnContextAccessor.Current;
        if (optional is { } value)
        {
            required.Add(value.Key, value.Value);
        }

        var result = await policyPipeline.InvokeAsync(
            new ToolInvocationRequest(
                context.Request.UserId,
                context.Request.ConversationId,
                context.Request.TurnId,
                context.CurrentToolCallId,
                context.Request.TraceId,
                toolName,
                JsonSerializer.Serialize(required, JsonOptions)),
            cancellationToken).ConfigureAwait(false);

        return result.Allowed
            ? result.SanitizedResultJson ?? "{}"
            : JsonSerializer.Serialize(
                new { status = "ToolRejected", errorCode = result.ErrorCode ?? "POLICY_REJECTED" },
                JsonOptions);
    }

    private static Dictionary<string, string> Required(params string[] namesAndValues)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < namesAndValues.Length; index += 2)
        {
            result.Add(namesAndValues[index], namesAndValues[index + 1]);
        }

        return result;
    }

    private static KeyValuePair<string, string>? Optional(string name, string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : KeyValuePair.Create(name, value);

    /// <summary>
    /// 包装 Agent 函数并固定严格 JSON Schema，防止模型绕过参数契约。
    /// </summary>
    private sealed class StrictSchemaFunction(AIFunction inner, JsonElement schema)
        : DelegatingAIFunction(inner)
    {
        public override JsonElement JsonSchema => schema;
    }
}
