// Maps variant provider response fields into stable internal records without forwarding raw payloads.
using System.Text.Json;
using Fadada.CertificationQueryAgent.Domain.Evidence;

namespace Fadada.CertificationQueryAgent.Infrastructure.Fadada;

/// <summary>
/// 将不可信的法大大传输对象映射为受控领域记录并规范化状态。
/// </summary>
internal static class FadadaResponseMapper
{
    public static FadadaResult<AccountRecord> Account(FadadaResult<string> response, string mobile) =>
        Map(response, FadadaEndpointKey.GetAccount, item => new AccountRecord(
            FadadaJson.GetString(item, "accountId", "account_id", "id") ?? string.Empty,
            FadadaJson.GetString(item, "mobile", "phone") ?? mobile,
            FadadaJson.OperationalStatus(FadadaJson.GetString(item, "accountStatus", "status"))));

    public static FadadaResult<PersonVerificationRecord> PersonVerification(
        FadadaResult<string> response,
        string accountId) => Map(response, FadadaEndpointKey.GetPersonVerification, item => new PersonVerificationRecord(
            FadadaJson.GetString(item, "accountId", "account_id") ?? accountId,
            FadadaJson.GetString(item, "name", "userName", "verifiedName"),
            FadadaJson.CertificationStatus(FadadaJson.GetString(item, "isCerdit", "verificationStatus", "verifyStatus", "status"))));

    public static FadadaResult<CompanyRecord> Company(FadadaResult<string> response, string companyFullName) =>
        Map(response, FadadaEndpointKey.GetCompany, item => new CompanyRecord(
            FadadaJson.GetString(item, "companyId", "company_id", "id") ?? string.Empty,
            FadadaJson.GetString(item, "companyFullName", "companyName", "name") ?? companyFullName,
            CompanyStatus(item),
            FadadaJson.Administrator(item)));

    public static FadadaResult<CompanyRecord> CompanyVerification(
        FadadaResult<string> response,
        CompanyRecord company) => Map(response, FadadaEndpointKey.GetCompanyVerification, item => company with
        {
            Status = FadadaJson.CertificationStatus(FadadaJson.GetString(item, "isCerdit", "verificationStatus", "status")),
            Administrator = MergeAdministrator(FadadaJson.Administrator(item), company.Administrator)
        });

    public static FadadaResult<IReadOnlyList<SealRecord>> Seals(FadadaResult<string> response)
    {
        if (!TryItems(response, FadadaEndpointKey.GetSeals, out var items, out var failure))
        {
            return failure!;
        }

        return FadadaResult<IReadOnlyList<SealRecord>>.Success(items!.Select(item => new SealRecord(
            FadadaJson.GetString(item, "sealId", "seal_id", "id") ?? string.Empty,
            FadadaJson.GetString(item, "sealName", "name") ?? string.Empty,
            FadadaJson.GetString(item, "sealType", "type") ?? string.Empty,
            FadadaJson.OperationalStatus(FadadaJson.GetString(item, "sealStatus", "status")))).ToArray());
    }

    public static FadadaResult<SealInfoRecord> SealInfo(FadadaResult<string> response, SealRecord seal) =>
        Map(response, FadadaEndpointKey.GetSealInfo, item => new SealInfoRecord(
            FadadaJson.GetString(item, "sealId", "seal_id", "id") ?? seal.SealId,
            FadadaJson.GetString(item, "sealName", "name") ?? seal.Name,
            FadadaJson.GetString(item, "sealType", "type") ?? seal.Type,
            FadadaJson.OperationalStatus(FadadaJson.GetString(item, "sealStatus", "status")),
            FadadaJson.PermissionAccountIds(item)));

    private static FadadaResult<T> Map<T>(
        FadadaResult<string> response,
        FadadaEndpointKey endpoint,
        Func<JsonElement, T> map)
    {
        if (!TryItems(response, endpoint, out var items, out var failure))
        {
            return FadadaResult<T>.Failure(
                failure!.Error?.Code ?? "FDD_RESPONSE_INVALID",
                endpoint.ToString(),
                failure.Error?.Retryable ?? false);
        }

        return items!.Count == 0 ? FadadaResult<T>.NotFound() : FadadaResult<T>.Success(map(items[0]));
    }

    private static bool TryItems(
        FadadaResult<string> response,
        FadadaEndpointKey endpoint,
        out IReadOnlyList<JsonElement>? items,
        out FadadaResult<IReadOnlyList<SealRecord>>? failure)
    {
        items = null;
        failure = null;
        if (!response.IsSuccess || response.Value is null)
        {
            failure = FadadaResult<IReadOnlyList<SealRecord>>.Failure(
                response.Error?.Code ?? "FDD_REQUEST_FAILED",
                endpoint.ToString(),
                response.Error?.Retryable ?? false);
            return false;
        }

        if (!FadadaJson.TryParseSuccess(response.Value, out var document, out var businessCode) || document is null)
        {
            document?.Dispose();
            failure = FadadaResult<IReadOnlyList<SealRecord>>.Failure(
                businessCode is null ? "FDD_RESPONSE_INVALID" : $"FDD_BUSINESS_{SafeCode(businessCode)}",
                endpoint.ToString());
            return false;
        }

        using (document)
        {
            items = FadadaJson.DataItems(document.RootElement).Select(item => item.Clone()).ToArray();
        }

        return true;
    }

    private static string SafeCode(string value)
    {
        var safe = new string(value.Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-').Take(48).ToArray());
        return safe.Length == 0 ? "UNKNOWN" : safe;
    }

    private static AdministratorRecord? MergeAdministrator(
        AdministratorRecord? primary,
        AdministratorRecord? fallback)
    {
        if (primary is null)
        {
            return fallback;
        }

        if (fallback is null)
        {
            return primary;
        }

        return new AdministratorRecord(
            primary.AccountId ?? fallback.AccountId,
            primary.Name ?? fallback.Name,
            primary.Mobile ?? fallback.Mobile);
    }

    private static BusinessStatus CompanyStatus(JsonElement item)
    {
        var certification = FadadaJson.GetString(item, "isCerdit", "verificationStatus", "verifyStatus");
        return certification is null
            ? FadadaJson.OperationalStatus(FadadaJson.GetString(item, "companyStatus", "status"))
            : FadadaJson.CertificationStatus(certification);
    }
}
