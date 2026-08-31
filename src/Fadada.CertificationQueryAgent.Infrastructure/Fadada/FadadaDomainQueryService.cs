// Composes low-level provider reads into the four coarse-grained evidence-producing domain queries.
using Fadada.CertificationQueryAgent.Application.Auditing;
using Fadada.CertificationQueryAgent.Application.DomainTools;
using Fadada.CertificationQueryAgent.Domain.Evidence;
using Fadada.CertificationQueryAgent.Domain.Queries;
using Fadada.CertificationQueryAgent.Infrastructure.Security;

namespace Fadada.CertificationQueryAgent.Infrastructure.Fadada;

/// <summary>
/// 组合多个法大大只读接口并生成规范化证据，确定性结论不交由模型判断。
/// </summary>
public sealed class FadadaDomainQueryService : IDomainQueryService, IDisposable
{
    private readonly FadadaApiClient client;
    private readonly FadadaTokenProvider tokenProvider;

    public FadadaDomainQueryService(
        HttpClient httpClient,
        FadadaOptions options,
        IAuditStore auditStore,
        CredentialScrubber credentialScrubber,
        TimeProvider? timeProvider = null)
    {
        options.Validate();
        tokenProvider = new FadadaTokenProvider(
            httpClient,
            options,
            auditStore,
            credentialScrubber,
            timeProvider);
        client = new FadadaApiClient(httpClient, options, tokenProvider, auditStore, credentialScrubber);
    }

    public async ValueTask<EvidenceEnvelope<PersonEvidence>> QueryPersonAsync(
        DomainQueryContext context,
        PersonQuery query,
        CancellationToken cancellationToken)
    {
        var accountResult = FadadaResponseMapper.Account(
            await client.GetAccountAsync(context, query.Mobile.Value, cancellationToken),
            query.Mobile.Value);
        if (!accountResult.IsSuccess || accountResult.Value is null)
        {
            return Terminal<PersonEvidence>(context, accountResult, FadadaEndpointKey.GetAccount);
        }

        var account = accountResult.Value;
        var verificationResult = FadadaResponseMapper.PersonVerification(
            await client.GetPersonVerificationAsync(context, account.AccountId, cancellationToken),
            account.AccountId);
        var verification = verificationResult.Value;
        bool? nameMatches = query.ClaimedName is null || verification?.VerifiedName is null
            ? null
            : string.Equals(query.ClaimedName.Value.Value, verification.VerifiedName.Trim(), StringComparison.Ordinal);
        var evidence = new PersonEvidence(
            account.AccountId,
            account.Status,
            verification?.Status ?? BusinessStatus.Unknown,
            verification?.VerifiedName,
            nameMatches);
        var status = EvidenceRules.AggregateStatus([accountResult.Status, verificationResult.Status]);
        var conclusion = PersonConclusion(evidence, query.ClaimedName is not null);
        return Envelope(
            context,
            status,
            evidence,
            [
                new EvidenceFact("person.accountId", account.AccountId, FactReliability.ReliableIdentifier),
                new EvidenceFact("person.verifiedName", verification?.VerifiedName, FactReliability.VerifiedAttribute)
            ],
            conclusion,
            verification is null ? ["person.verification"] : [],
            Errors(verificationResult),
            [FadadaEndpointKey.GetAccount, FadadaEndpointKey.GetPersonVerification]);
    }

    public async ValueTask<EvidenceEnvelope<CompanyEvidence>> QueryCompanyAsync(
        DomainQueryContext context,
        CompanyQuery query,
        CancellationToken cancellationToken)
    {
        var companyResult = FadadaResponseMapper.Company(
            await client.GetCompanyAsync(context, query.CompanyFullName.Value, cancellationToken),
            query.CompanyFullName.Value);
        if (!companyResult.IsSuccess || companyResult.Value is null)
        {
            return Terminal<CompanyEvidence>(context, companyResult, FadadaEndpointKey.GetCompany);
        }

        var company = companyResult.Value;
        var verificationResult = FadadaResponseMapper.CompanyVerification(
            await client.GetCompanyVerificationAsync(context, company.CompanyId, cancellationToken),
            company);
        var verified = verificationResult.Value ?? company;
        var evidence = new CompanyEvidence(
            verified.CompanyId,
            company.Status,
            verified.Status,
            verified.Administrator is null
                ? null
                : new AdministratorEvidence(
                    verified.Administrator.AccountId,
                    verified.Administrator.Name,
                    verified.Administrator.Mobile));
        var status = EvidenceRules.AggregateStatus([companyResult.Status, verificationResult.Status]);
        var conclusion = verified.Status == BusinessStatus.Verified
            ? new DeterministicConclusion(ConclusionStatus.Confirmed, "COMPANY_VERIFIED", "Company verification is confirmed.")
            : new DeterministicConclusion(ConclusionStatus.NotVerified, "COMPANY_NOT_VERIFIED", "Company verification is not confirmed.");
        return Envelope(
            context,
            status,
            evidence,
            [
                new EvidenceFact("company.companyId", verified.CompanyId, FactReliability.ReliableIdentifier),
                new EvidenceFact("company.administrator.accountId", verified.Administrator?.AccountId, FactReliability.ReliableIdentifier),
                new EvidenceFact("company.administrator.name", verified.Administrator?.Name, FactReliability.VerifiedAttribute),
                new EvidenceFact("company.administrator.mobile", verified.Administrator?.Mobile, FactReliability.VerifiedAttribute)
            ],
            conclusion,
            verificationResult.Value is null ? ["company.verification"] : [],
            Errors(verificationResult),
            [FadadaEndpointKey.GetCompany, FadadaEndpointKey.GetCompanyVerification]);
    }

    public async ValueTask<EvidenceEnvelope<RelationshipEvidence>> QueryRelationshipAsync(
        DomainQueryContext context,
        RelationshipQuery query,
        CancellationToken cancellationToken)
    {
        var personTask = QueryPersonAsync(context, new PersonQuery(query.Mobile, query.ClaimedName), cancellationToken).AsTask();
        var companyTask = QueryCompanyAsync(context, new CompanyQuery(query.CompanyFullName), cancellationToken).AsTask();
        await Task.WhenAll(personTask, companyTask);
        var person = await personTask;
        var company = await companyTask;
        if (person.Data is null || company.Data is null)
        {
            var errors = person.SafeErrors.Concat(company.SafeErrors).ToArray();
            return Envelope<RelationshipEvidence>(
                context,
                EvidenceRules.AggregateStatus([person.Status, company.Status]),
                null,
                person.Facts.Concat(company.Facts).ToArray(),
                new DeterministicConclusion(ConclusionStatus.Unknown, "RELATIONSHIP_EVIDENCE_INSUFFICIENT", "Person or company evidence is missing."),
                person.MissingEvidence.Concat(company.MissingEvidence).ToArray(),
                errors,
                Endpoints(person, company));
        }

        var relationship = new RelationshipEvidence(
            person.Data,
            company.Data,
            person.Data.ClaimedNameMatches,
            null);
        return Envelope(
            context,
            EvidenceRules.AggregateStatus([person.Status, company.Status]),
            relationship,
            person.Facts.Concat(company.Facts).ToArray(),
            EvidenceRules.EvaluateRelationship(relationship),
            person.MissingEvidence.Concat(company.MissingEvidence).ToArray(),
            person.SafeErrors.Concat(company.SafeErrors).ToArray(),
            Endpoints(person, company));
    }

    public async ValueTask<EvidenceEnvelope<SealsEvidence>> QuerySealsAsync(
        DomainQueryContext context,
        SealsQuery query,
        CancellationToken cancellationToken)
    {
        var companyTask = QueryCompanyAsync(context, new CompanyQuery(query.CompanyFullName), cancellationToken).AsTask();
        var personTask = query.Mobile is null
            ? null
            : QueryPersonAsync(context, new PersonQuery(query.Mobile.Value, null), cancellationToken).AsTask();
        var company = await companyTask;
        var person = personTask is null ? null : await personTask;
        if (company.Data?.CompanyId is null)
        {
            return Envelope<SealsEvidence>(
                context,
                company.Status,
                null,
                company.Facts,
                new DeterministicConclusion(ConclusionStatus.Unknown, "SEALS_COMPANY_REQUIRED", "Reliable company evidence is required."),
                ["company.companyId"],
                company.SafeErrors,
                company.Metadata.SourceEndpointKeys.Select(ParseEndpoint));
        }

        var sealsResult = FadadaResponseMapper.Seals(
            await client.GetSealsAsync(context, company.Data.CompanyId, cancellationToken));
        if (!sealsResult.IsSuccess || sealsResult.Value is null)
        {
            return Terminal<SealsEvidence>(context, sealsResult, FadadaEndpointKey.GetSeals);
        }

        var details = await Task.WhenAll(sealsResult.Value.Select(async seal =>
            FadadaResponseMapper.SealInfo(
                await client.GetSealInfoAsync(context, seal.SealId, cancellationToken),
                seal)));
        var personAccountId = person?.Data?.AccountId;
        var seals = details.Where(result => result.Value is not null).Select(result =>
        {
            var detail = result.Value!;
            bool? authorization = query.Mobile is null
                ? null
                : EvidenceRules.EvaluateSealAuthorization(personAccountId, detail.PermissionAccountIds).Status == ConclusionStatus.Confirmed;
            // Cap the model-facing list explicitly so downstream sanitization cannot silently change its completeness.
            var authorizedUsersTruncated = detail.AuthorizedUsers.Count > SealEvidence.MaximumAuthorizedUsers;
            var authorizedUsers = detail.AuthorizedUsers.Take(SealEvidence.MaximumAuthorizedUsers).Select(user => new SealAuthorizedUserEvidence(
                user.UserName,
                user.AreaCode,
                user.Mobile,
                user.Email,
                user.AuthorizedAt,
                user.ValidFrom,
                user.ValidUntil,
                user.UseTimes)).ToArray();
            return new SealEvidence(
                detail.SealId,
                detail.Name,
                detail.Type,
                detail.Status,
                authorization,
                authorizedUsers,
                detail.AuthorizedUsersComplete ? detail.AuthorizedUsers.Count : null,
                detail.AuthorizedUsersComplete && !authorizedUsersTruncated,
                authorizedUsersTruncated);
        }).ToArray();
        var hasIncompleteAuthorizedUsers = details.Any(result =>
            result.Value is { } detail &&
            (!detail.AuthorizedUsersComplete || detail.AuthorizedUsers.Count > SealEvidence.MaximumAuthorizedUsers));
        var statuses = new[] { company.Status, sealsResult.Status }
            .Concat(details.Select(detail => detail.Status))
            .Concat(person is null ? [] : [person.Status])
            .Concat(hasIncompleteAuthorizedUsers ? [EvidenceStatus.Partial] : []);
        var status = EvidenceRules.AggregateStatus(statuses);
        var evidence = new SealsEvidence(company.Data, seals, personAccountId);
        var missingEvidence = new List<string>();
        if (details.Any(detail => detail.Value is null))
        {
            missingEvidence.Add("seal.details");
        }

        if (details.Any(detail => detail.Value is { AuthorizedUsersComplete: false }))
        {
            missingEvidence.Add("seal.authorizedUsers");
        }

        if (details.Any(detail => detail.Value is { AuthorizedUsers.Count: > SealEvidence.MaximumAuthorizedUsers }))
        {
            missingEvidence.Add("seal.authorizedUsers.truncated");
        }

        return Envelope(
            context,
            status,
            evidence,
            company.Facts.Concat(person?.Facts ?? []).ToArray(),
            new DeterministicConclusion(
                status == EvidenceStatus.Succeeded ? ConclusionStatus.Confirmed : ConclusionStatus.Partial,
                "SEALS_EVALUATED",
                "Seal and authorization evidence was evaluated deterministically."),
            missingEvidence,
            company.SafeErrors.Concat(person?.SafeErrors ?? []).Concat(details.SelectMany(Errors)).ToArray(),
            new[] { FadadaEndpointKey.GetCompany, FadadaEndpointKey.GetCompanyVerification, FadadaEndpointKey.GetSeals, FadadaEndpointKey.GetSealInfo }
                .Concat(query.Mobile is null ? [] : [FadadaEndpointKey.GetAccount, FadadaEndpointKey.GetPersonVerification]));
    }

    public void Dispose() => tokenProvider.Dispose();

    private static DeterministicConclusion PersonConclusion(PersonEvidence evidence, bool hasClaimedName)
    {
        if (evidence.VerificationStatus != BusinessStatus.Verified)
        {
            return new DeterministicConclusion(ConclusionStatus.NotVerified, "PERSON_NOT_VERIFIED", "Person verification is not confirmed.");
        }

        return hasClaimedName && evidence.ClaimedNameMatches == false
            ? new DeterministicConclusion(ConclusionStatus.Mismatch, "PERSON_NAME_MISMATCH", "The verified name does not match the claimed name.")
            : new DeterministicConclusion(ConclusionStatus.Confirmed, "PERSON_VERIFIED", "Person verification is confirmed.");
    }

    private static EvidenceEnvelope<T> Terminal<T>(
        DomainQueryContext context,
        object result,
        FadadaEndpointKey endpoint)
    {
        var (status, error) = result switch
        {
            FadadaResult<AccountRecord> value => (value.Status, value.Error),
            FadadaResult<CompanyRecord> value => (value.Status, value.Error),
            FadadaResult<IReadOnlyList<SealRecord>> value => (value.Status, value.Error),
            _ => (EvidenceStatus.Failed, new SafeEvidenceError("FDD_REQUEST_FAILED", endpoint.ToString(), false))
        };
        var conclusionStatus = status == EvidenceStatus.NotFound ? ConclusionStatus.NotFound : ConclusionStatus.Failed;
        return Envelope<T>(
            context,
            status,
            default,
            [],
            new DeterministicConclusion(conclusionStatus, status == EvidenceStatus.NotFound ? "EVIDENCE_NOT_FOUND" : "EXTERNAL_QUERY_FAILED", "Required external evidence is unavailable."),
            [endpoint.ToString()],
            error is null ? [] : [error],
            [endpoint]);
    }

    private static IReadOnlyList<SafeEvidenceError> Errors<T>(FadadaResult<T> result) =>
        result.Error is null ? [] : [result.Error];

    private static EvidenceEnvelope<T> Envelope<T>(
        DomainQueryContext context,
        EvidenceStatus status,
        T? data,
        IReadOnlyList<EvidenceFact> facts,
        DeterministicConclusion conclusion,
        IReadOnlyList<string> missing,
        IReadOnlyList<SafeEvidenceError> errors,
        IEnumerable<FadadaEndpointKey> endpoints) => new(
            status,
            data,
            facts,
            conclusion,
            missing,
            errors,
            new EvidenceMetadata(
                DateTimeOffset.UtcNow,
                endpoints.Select(endpoint => endpoint.ToString()).Distinct(StringComparer.Ordinal).ToArray(),
                context.TraceId));

    private static IEnumerable<FadadaEndpointKey> Endpoints<TLeft, TRight>(
        EvidenceEnvelope<TLeft> left,
        EvidenceEnvelope<TRight> right) =>
        left.Metadata.SourceEndpointKeys.Concat(right.Metadata.SourceEndpointKeys).Select(ParseEndpoint);

    private static FadadaEndpointKey ParseEndpoint(string value) => Enum.Parse<FadadaEndpointKey>(value);
}
