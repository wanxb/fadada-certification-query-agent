// Converts validated registry arguments into typed queries; arbitrary reflection or dispatch is forbidden.
using System.Text.Json;
using System.Text.Json.Serialization;
using Fadada.CertificationQueryAgent.Application.DomainTools;
using Fadada.CertificationQueryAgent.Domain.Queries;

namespace Fadada.CertificationQueryAgent.Infrastructure.DomainTools;

/// <summary>
/// 只执行注册表中的领域工具，把模型工具名映射到受控只读查询服务。
/// </summary>
public sealed class RegisteredDomainToolExecutor(IDomainQueryService queryService) : IRegisteredToolExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<ToolExecutionResult> ExecuteAsync(
        ToolExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        object result = request.ToolName switch
        {
            "query_person" => await queryService.QueryPersonAsync(
                request.Context,
                new PersonQuery(Mobile(request), OptionalName(request)),
                cancellationToken).ConfigureAwait(false),
            "query_company" => await queryService.QueryCompanyAsync(
                request.Context,
                new CompanyQuery(Company(request)),
                cancellationToken).ConfigureAwait(false),
            "query_relationship" => await queryService.QueryRelationshipAsync(
                request.Context,
                new RelationshipQuery(Mobile(request), Company(request), OptionalName(request)),
                cancellationToken).ConfigureAwait(false),
            "query_seals" => await queryService.QuerySealsAsync(
                request.Context,
                new SealsQuery(Company(request), OptionalMobile(request)),
                cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException("POLICY_TOOL_NOT_REGISTERED")
        };

        return new ToolExecutionResult(
            JsonSerializer.Serialize(result, JsonOptions),
            IntegrityLabel.ExternalUntrusted);
    }

    private static MobileNumber Mobile(ToolExecutionRequest request) =>
        MobileNumber.Create(Required(request, "mobile"));

    private static MobileNumber? OptionalMobile(ToolExecutionRequest request) =>
        request.CanonicalArguments.TryGetValue("mobile", out var value)
            ? MobileNumber.Create(value)
            : null;

    private static CompanyFullName Company(ToolExecutionRequest request) =>
        CompanyFullName.Create(Required(request, "companyFullName"));

    private static PersonName? OptionalName(ToolExecutionRequest request) =>
        request.CanonicalArguments.TryGetValue("claimedName", out var value)
            ? PersonName.Create(value)
            : null;

    private static string Required(ToolExecutionRequest request, string name) =>
        request.CanonicalArguments.TryGetValue(name, out var value)
            ? value
            : throw new InvalidOperationException("POLICY_TOOL_ARGUMENT_MISSING");
}
