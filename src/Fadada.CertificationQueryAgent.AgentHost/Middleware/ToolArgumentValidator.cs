// Validates model-generated arguments against the closed tool schema and rejects unknown fields.
using System.Text.Json;
using Fadada.CertificationQueryAgent.Application.DomainTools;

namespace Fadada.CertificationQueryAgent.AgentHost.Middleware;

/// <summary>
/// 对模型给出的工具参数执行确定性格式校验，不依赖模型自行纠错。
/// </summary>
internal static class ToolArgumentValidator
{
    public static bool TryValidate(
        DomainToolDefinition tool,
        string argumentsJson,
        out IReadOnlyDictionary<string, string> arguments,
        out string errorCode)
    {
        arguments = new Dictionary<string, string>();
        errorCode = "POLICY_SCHEMA_INVALID";
        try
        {
            using var document = JsonDocument.Parse(argumentsJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!tool.Arguments.ContainsKey(property.Name) ||
                    property.Value.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(property.Value.GetString()) ||
                    !parsed.TryAdd(property.Name, property.Value.GetString()!))
                {
                    return false;
                }
            }

            if (tool.Arguments.Values.Any(argument => argument.Required && !parsed.ContainsKey(argument.Name)))
            {
                return false;
            }

            arguments = parsed;
            errorCode = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
