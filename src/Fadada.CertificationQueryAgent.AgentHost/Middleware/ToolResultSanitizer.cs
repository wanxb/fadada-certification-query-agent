// Removes secret-bearing and instruction-bearing fields before tool evidence returns to the model.
using System.Text;
using System.Text.Json;

namespace Fadada.CertificationQueryAgent.AgentHost.Middleware;

/// <summary>
/// 清洗外部工具结果的深度、长度和敏感字段，限制不可信内容进入模型上下文。
/// </summary>
internal static partial class ToolResultSanitizer
{
    private static readonly HashSet<string> ForbiddenProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwordHash", "accessToken", "appSecret", "apiKey", "authorization",
        "raw", "rawPayload", "rawResponse", "prompt", "argumentsJson", "chainOfThought",
        "code", "safeErrorCode"
    };

    public static bool TrySanitize(string json, out string sanitized)
    {
        sanitized = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteElement(writer, document.RootElement, depth: 0);
            }

            sanitized = Encoding.UTF8.GetString(stream.ToArray());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool HasEvidenceShape(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return root.TryGetProperty("status", out _) &&
            root.TryGetProperty("conclusion", out var conclusion) && conclusion.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object;
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element, int depth)
    {
        if (depth > 24)
        {
            writer.WriteStringValue("[CONTENT_REDACTED]");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    if (ForbiddenProperties.Contains(property.Name))
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value, depth + 1);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray().Take(100))
                {
                    WriteElement(writer, item, depth + 1);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                var value = element.GetString() ?? string.Empty;
                writer.WriteStringValue(PolicyContentClassifier.ContainsInstruction(value)
                    ? "[UNTRUSTED_CONTENT_REDACTED]"
                    : value.Length > 2048 ? value[..2048] : value);
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

}
