// Typed query records constrain tools to the four approved read-only business capabilities.
namespace Fadada.CertificationQueryAgent.Domain.Queries;

/// <summary>
/// 承载 PersonQuery 的已验证输入，限制跨层调用只能使用明确的数据契约。
/// </summary>
public sealed record PersonQuery(MobileNumber Mobile, PersonName? ClaimedName);

/// <summary>
/// 承载 CompanyQuery 的已验证输入，限制跨层调用只能使用明确的数据契约。
/// </summary>
public sealed record CompanyQuery(CompanyFullName CompanyFullName);

/// <summary>
/// 承载 RelationshipQuery 的已验证输入，限制跨层调用只能使用明确的数据契约。
/// </summary>
public sealed record RelationshipQuery(
    MobileNumber Mobile,
    CompanyFullName CompanyFullName,
    PersonName? ClaimedName);

/// <summary>
/// 承载 SealsQuery 的已验证输入，限制跨层调用只能使用明确的数据契约。
/// </summary>
public sealed record SealsQuery(
    CompanyFullName CompanyFullName,
    MobileNumber? Mobile);
