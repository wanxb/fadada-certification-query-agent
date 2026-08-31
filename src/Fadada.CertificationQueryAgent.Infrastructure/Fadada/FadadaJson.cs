// Parses provider envelopes defensively while retaining only fields required for normalized evidence.
using System.Globalization;
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
        // Provider arrays may contain malformed entries; treating non-objects as absent avoids leaking schema drift as runtime exceptions.
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

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

    public static SealAuthorizedUserCollection AuthorizedUsers(JsonElement element)
    {
        var users = new List<SealAuthorizedUserRecord>();
        var seenUsers = new HashSet<SealAuthorizedUserRecord>();
        var listFound = false;
        var isComplete = true;
        foreach (var name in new[] { "permissions", "permissionAccounts", "authorizedAccounts", "authorizeUserInfoList", "authorizedUserInfoList" })
        {
            if (!element.TryGetProperty(name, out var values))
            {
                continue;
            }

            listFound = true;
            if (values.ValueKind != JsonValueKind.Array)
            {
                isComplete = false;
                continue;
            }

            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Object)
                {
                    isComplete = false;
                    continue;
                }

                // Authorization dates remain strings because the provider contract does not guarantee one parseable date format.
                var userFieldsValid = true;
                var (useTimes, useTimesValid) = GetNullableInt(value, "useTimes");
                var user = new SealAuthorizedUserRecord(
                    GetAuthorizedUserString(value, ref userFieldsValid, "accountId", "account_id", "id"),
                    GetAuthorizedUserString(value, ref userFieldsValid, "tpAccountId", "thirdPartyAccountId", "tp_account_id"),
                    GetAuthorizedUserString(value, ref userFieldsValid, "userName", "name"),
                    GetAuthorizedUserString(value, ref userFieldsValid, "areaCode", "area_code"),
                    GetAuthorizedUserString(value, ref userFieldsValid, "mobile", "phone"),
                    GetAuthorizedUserString(value, ref userFieldsValid, "email"),
                    GetAuthorizedUserString(value, ref userFieldsValid, "createdDate", "authorizedAt"),
                    GetAuthorizedUserString(value, ref userFieldsValid, "expiryDateBegin", "validFrom"),
                    GetAuthorizedUserString(value, ref userFieldsValid, "expiryDateEnd", "validUntil"),
                    useTimes);
                isComplete &= userFieldsValid && useTimesValid;
                // Preserve provider order for stable answers while suppressing duplicate aliases or repeated entries.
                if (IsEmpty(user))
                {
                    isComplete = false;
                }
                else if (seenUsers.Add(user))
                {
                    users.Add(user);
                }
            }
        }

        return new SealAuthorizedUserCollection(users.ToArray(), listFound && isComplete);
    }

    public static IReadOnlyList<string> PermissionAccountIds(
        JsonElement element,
        IReadOnlyList<SealAuthorizedUserRecord> authorizedUsers)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in new[] { "permissionAccountIds", "accountIds", "permission_account_ids" })
        {
            if (element.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array)
            {
                foreach (var value in values.EnumerateArray())
                {
                    if (value.ValueKind == JsonValueKind.String && Clean(value.GetString()) is { } id)
                    {
                        ids.Add(id);
                    }
                }
            }
        }

        foreach (var user in authorizedUsers)
        {
            if (user.AccountId is { } accountId)
            {
                ids.Add(accountId);
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

    private static string? GetAuthorizedUserString(
        JsonElement element,
        ref bool isValid,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            // Scalar drift remains representable, but nested values indicate a damaged user entry that must surface as partial evidence.
            if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                isValid = false;
                return null;
            }

            return Clean(GetString(element, name));
        }

        return null;
    }

    private static (int? Value, bool IsValid) GetNullableInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return (null, true);
        }

        // Numeric strings are accepted for compatibility, while invalid or overflowing values safely become unknown.
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => (null, true),
            JsonValueKind.Number when value.TryGetInt32(out var number) => (number, true),
            JsonValueKind.String when int.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var number) => (number, true),
            _ => (null, false)
        };
    }

    private static bool IsEmpty(SealAuthorizedUserRecord user) =>
        user.AccountId is null &&
        user.ThirdPartyAccountId is null &&
        user.UserName is null &&
        user.AreaCode is null &&
        user.Mobile is null &&
        user.Email is null &&
        user.AuthorizedAt is null &&
        user.ValidFrom is null &&
        user.ValidUntil is null &&
        user.UseTimes is null;
}
