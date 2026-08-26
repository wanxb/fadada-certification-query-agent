// Authentication contracts use stable user IDs; usernames and network addresses are never identity keys.
using Fadada.CertificationQueryAgent.Application.Common;

namespace Fadada.CertificationQueryAgent.Application.Authentication;

/// <summary>
/// 承载 LoginRequest 的已验证输入，限制跨层调用只能使用明确的数据契约。
/// </summary>
public sealed record LoginRequest(string UserName, string Password);

/// <summary>
/// 集中表达 AuthenticationPolicy 的配置和约束，使默认值、验证规则与运行行为保持一致。
/// </summary>
public sealed record AuthenticationPolicy(
    int MaximumFailedAttempts = 5,
    TimeSpan? LockoutDuration = null)
{
    public TimeSpan EffectiveLockoutDuration => LockoutDuration ?? TimeSpan.FromMinutes(15);

    public void Validate()
    {
        if (MaximumFailedAttempts is < 2 or > 20 ||
            EffectiveLockoutDuration < TimeSpan.FromMinutes(1) ||
            EffectiveLockoutDuration > TimeSpan.FromDays(1))
        {
            throw new InvalidOperationException("AUTH_POLICY_INVALID");
        }
    }
}

/// <summary>
/// 封装 AuthenticationResult 的确定性结果和安全状态，供上层在不解析自由文本的情况下处理。
/// </summary>
public sealed record AuthenticationResult(
    bool Succeeded,
    UserId? UserId,
    string? SecurityStamp,
    string? ErrorCode,
    DateTimeOffset? LockedUntilUtc);

/// <summary>
/// 以不可变数据契约表达 UserAccount，确保跨层传递时字段语义和边界保持稳定。
/// </summary>
public sealed record UserAccount(
    UserId Id,
    string UserName,
    string NormalizedUserName,
    string DisplayName,
    string PasswordHash,
    string SecurityStamp,
    bool IsActive,
    int AccessFailedCount,
    DateTimeOffset? LockoutEndUtc,
    DateTimeOffset? LastLoginAtUtc,
    byte[] RowVersion);

/// <summary>
/// 以不可变契约保存 AccountMutationAudit 的关键状态，支持审计、恢复和确定性处理。
/// </summary>
public sealed record AccountMutationAudit(
    Guid Id,
    string EventType,
    string Actor,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// 封装 AccountAdministrationResult 的确定性结果和安全状态，供上层在不解析自由文本的情况下处理。
/// </summary>
public sealed record AccountAdministrationResult(
    bool Succeeded,
    UserId? UserId,
    string? ErrorCode);

/// <summary>
/// 定义 IUserStore 的稳定端口，使应用逻辑不依赖具体基础设施并便于替换测试实现。
/// </summary>
public interface IUserStore
{
    ValueTask<UserAccount?> GetByNormalizedNameAsync(
        string normalizedUserName,
        CancellationToken cancellationToken);

    ValueTask<UserAccount?> GetByIdAsync(UserId userId, CancellationToken cancellationToken);

    ValueTask CreateAsync(
        UserAccount account,
        AccountMutationAudit audit,
        CancellationToken cancellationToken);

    ValueTask<bool> UpdateAuthenticationStateAsync(
        UserId userId,
        int accessFailedCount,
        DateTimeOffset? lockoutEndUtc,
        DateTimeOffset? lastLoginAtUtc,
        byte[] expectedRowVersion,
        AccountMutationAudit audit,
        CancellationToken cancellationToken);

    ValueTask<bool> UpdateCredentialsAsync(
        UserId userId,
        string passwordHash,
        string securityStamp,
        byte[] expectedRowVersion,
        AccountMutationAudit audit,
        CancellationToken cancellationToken);

    ValueTask<bool> SetActiveAsync(
        UserId userId,
        bool isActive,
        string securityStamp,
        byte[] expectedRowVersion,
        AccountMutationAudit audit,
        CancellationToken cancellationToken);
}

/// <summary>
/// 定义 IAccountAdministrationService 的稳定端口，使应用逻辑不依赖具体基础设施并便于替换测试实现。
/// </summary>
public interface IAccountAdministrationService
{
    ValueTask<AccountAdministrationResult> CreateAsync(
        string userName,
        string displayName,
        string password,
        string actor,
        CancellationToken cancellationToken);

    ValueTask<AccountAdministrationResult> ResetPasswordAsync(
        string userName,
        string newPassword,
        string actor,
        CancellationToken cancellationToken);

    ValueTask<AccountAdministrationResult> SetActiveAsync(
        string userName,
        bool isActive,
        string actor,
        CancellationToken cancellationToken);
}

/// <summary>
/// 定义 IAuthenticationService 的稳定端口，使应用逻辑不依赖具体基础设施并便于替换测试实现。
/// </summary>
public interface IAuthenticationService
{
    ValueTask<AuthenticationResult> AuthenticateAsync(
        LoginRequest request,
        CancellationToken cancellationToken);

    ValueTask<bool> ValidateSessionAsync(
        UserId userId,
        string securityStamp,
        CancellationToken cancellationToken);
}
