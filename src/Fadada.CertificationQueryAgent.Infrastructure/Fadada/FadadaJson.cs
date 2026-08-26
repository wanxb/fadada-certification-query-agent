// Parses provider envelopes defensively while retaining only fields required for normalized evidence.
using System.Text.Json;
using Fadada.CertificationQueryAgent.Domain.Evidence;

namespace Fadada.CertificationQueryAgent.Infrastructure.Fadada;

/// <summary>
/// 集中提供法大大传输 JSON 配置，保证字段绑定和序列化策略一致。
/// </summary>
internal static class FadadaJson
{
    public static bool TryParseSuccess(string json, out JsonDocument? document, out string? businessCode)
    {
        document = null;
        businessCode = null;
        try
        {
            document = JsonDocument.Parse(json);
            businessCode = GetString(document.RootElement, "code");
            return businessCode is "0" or "success" or "SUCCESS";
        }
        catch (JsonException)
        {
            document?.Dispose();
            document = null;
            return false;
        }
    }

    public static IReadOnlyList<JsonElement> DataItems(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        return data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToArray()
            : data.ValueKind == JsonValueKind.Object ? [data] : [];
    }

    public static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }

        return null;
    }

    public static AdministratorRecord? Administrator(JsonElement element)
    {
        JsonElement source = default;
        var hasNestedSource = false;
        foreach (var name in new[] { "managerInfo", "administrator", "admin", "companyAdministrator" })
        {
            if (element.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Object)
            {
                source = item;
                hasNestedSource = true;
                break;
            }
        }

        if (!hasNestedSource)
        {
            foreach (var name in new[] { "adminInfo", "administratorInfo" })
            {
                if (element.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    var first = items.EnumerateArray().FirstOrDefault(item => item.ValueKind == JsonValueKind.Object);
                    if (first.ValueKind == JsonValueKind.Object)
                    {
                        source = first;
                        hasNestedSource = true;
                        break;
                    }
                }
            }
        }

        var accountId = Clean(hasNestedSource
            ? GetString(source, "accountId", "account_id", "administratorAccountId", "adminAccountId")
            : GetString(element, "administratorAccountId", "adminAccountId", "managerAccountId"));
        var administratorName = Clean(hasNestedSource
            ? GetString(source, "name", "userName", "administratorName", "adminName")
            : GetString(element, "administratorName", "adminName", "managerName"));
        var mobile = Clean(hasNestedSource
            ? GetString(source, "mobile", "phone", "administratorMobile", "adminMobile")
            : GetString(element, "administratorMobile", "adminMobile", "managerMobile"));
        return accountId is null && administratorName is null && mobile is null
            ? null
            : new AdministratorRecord(accountId, administratorName, mobile);
    }

    public static IReadOnlyList<string> PermissionAccountIds(JsonElement element)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in new[] { "permissionAccountIds", "accountIds", "permission_account_ids" })
        {
            if (element.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array)
            {
                foreach (var value in values.EnumerateArray())
                {
                    if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                    {
                        ids.Add(value.GetString()!);
                    }
                }
            }
        }

        foreach (var name in new[] { "permissions", "permissionAccounts", "authorizedAccounts", "authorizeUserInfoList", "authorizedUserInfoList" })
        {
            if (element.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array)
            {
                foreach (var value in values.EnumerateArray())
                {
                    var id = GetString(value, "accountId", "account_id", "id");
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        ids.Add(id);
                    }
                }
            }
        }

        return ids.ToArray();
    }

    public static BusinessStatus CertificationStatus(string? value) =>
        ExternalStatusNormalizer.NormalizeCertification(value);

    public static BusinessStatus OperationalStatus(string? value) =>
        ExternalStatusNormalizer.NormalizeOperational(value);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
