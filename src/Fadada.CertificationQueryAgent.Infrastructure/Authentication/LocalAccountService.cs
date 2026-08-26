// Implements local password authentication with lockout and optimistic-concurrency protection.
using System.Security.Cryptography;
using System.Text;
using Fadada.CertificationQueryAgent.Application.Authentication;
using Fadada.CertificationQueryAgent.Application.Common;
using Microsoft.AspNetCore.Identity;

namespace Fadada.CertificationQueryAgent.Infrastructure.Authentication;

/// <summary>
/// 实现本地账号认证和管理，统一密码策略、锁定、并发更新与安全戳失效。
/// </summary>
public sealed class LocalAccountService : IAuthenticationService, IAccountAdministrationService
{
    private const int MaximumConcurrencyAttempts = 3;
    private readonly IUserStore userStore;
    private readonly AuthenticationPolicy policy;
    private readonly TimeProvider timeProvider;
    private readonly PasswordHasher<UserAccount> passwordHasher = new();
    private readonly UserAccount dummyAccount;
    private readonly string dummyHash;

    public LocalAccountService(
        IUserStore userStore,
        AuthenticationPolicy? policy = null,
        TimeProvider? timeProvider = null)
    {
        this.userStore = userStore ?? throw new ArgumentNullException(nameof(userStore));
        this.policy = policy ?? new AuthenticationPolicy();
        this.policy.Validate();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        dummyAccount = NewAccount("timing-probe", "TIMING-PROBE", "Timing probe");
        dummyHash = passwordHasher.HashPassword(dummyAccount, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    }

    public async ValueTask<AuthenticationResult> AuthenticateAsync(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryNormalizeUserName(request.UserName, out var normalized) || string.IsNullOrEmpty(request.Password))
        {
            VerifyUnknownAccount(request.Password ?? string.Empty);
            return Failed("AUTH_INVALID_CREDENTIALS");
        }

        for (var attempt = 0; attempt < MaximumConcurrencyAttempts; attempt++)
        {
            var account = await userStore.GetByNormalizedNameAsync(normalized, cancellationToken).ConfigureAwait(false);
            if (account is null)
            {
                VerifyUnknownAccount(request.Password);
                return Failed("AUTH_INVALID_CREDENTIALS");
            }

            var now = timeProvider.GetUtcNow();
            if (!account.IsActive)
            {
                return Failed("AUTH_ACCOUNT_DISABLED");
            }

            if (account.LockoutEndUtc > now)
            {
                return Failed("AUTH_ACCOUNT_LOCKED", account.LockoutEndUtc);
            }

            var verification = passwordHasher.VerifyHashedPassword(account, account.PasswordHash, request.Password);
            if (verification == PasswordVerificationResult.Failed)
            {
                var priorFailures = account.LockoutEndUtc is not null && account.LockoutEndUtc <= now
                    ? 0
                    : account.AccessFailedCount;
                var failures = checked(priorFailures + 1);
                DateTimeOffset? lockedUntil = failures >= policy.MaximumFailedAttempts
                    ? now.Add(policy.EffectiveLockoutDuration)
                    : null;
                var updated = await userStore.UpdateAuthenticationStateAsync(
                    account.Id,
                    failures,
                    lockedUntil,
                    account.LastLoginAtUtc,
                    account.RowVersion,
                    Audit(lockedUntil is null ? "LoginFailed" : "AccountLocked", account.NormalizedUserName, now),
                    cancellationToken).ConfigureAwait(false);
                if (!updated)
                {
                    continue;
                }

                return lockedUntil is null
                    ? Failed("AUTH_INVALID_CREDENTIALS")
                    : Failed("AUTH_ACCOUNT_LOCKED", lockedUntil);
            }

            var succeeded = await userStore.UpdateAuthenticationStateAsync(
                account.Id,
                0,
                null,
                now,
                account.RowVersion,
                Audit("LoginSucceeded", account.NormalizedUserName, now),
                cancellationToken).ConfigureAwait(false);
            if (!succeeded)
            {
                continue;
            }

            return new AuthenticationResult(true, account.Id, account.SecurityStamp, null, null);
        }

        return Failed("AUTH_CONCURRENCY_CONFLICT");
    }

    public async ValueTask<bool> ValidateSessionAsync(
        UserId userId,
        string securityStamp,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(securityStamp))
        {
            return false;
        }

        var account = await userStore.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        return account is { IsActive: true } && FixedTimeEquals(account.SecurityStamp, securityStamp);
    }

    public async ValueTask<AccountAdministrationResult> CreateAsync(
        string userName,
        string displayName,
        string password,
        string actor,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeUserName(userName, out var normalized) ||
            string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 128 ||
            !IsStrongPassword(password) || !IsValidActor(actor))
        {
            return AdminFailed("ACCOUNT_INPUT_INVALID");
        }

        if (await userStore.GetByNormalizedNameAsync(normalized, cancellationToken).ConfigureAwait(false) is not null)
        {
            return AdminFailed("ACCOUNT_ALREADY_EXISTS");
        }

        var now = timeProvider.GetUtcNow();
        var account = NewAccount(userName.Trim(), normalized, displayName.Trim());
        account = account with { PasswordHash = passwordHasher.HashPassword(account, password) };
        await userStore.CreateAsync(account, Audit("AccountCreated", actor, now), cancellationToken).ConfigureAwait(false);
        return new AccountAdministrationResult(true, account.Id, null);
    }

    public async ValueTask<AccountAdministrationResult> ResetPasswordAsync(
        string userName,
        string newPassword,
        string actor,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeUserName(userName, out var normalized) || !IsStrongPassword(newPassword) || !IsValidActor(actor))
        {
            return AdminFailed("ACCOUNT_INPUT_INVALID");
        }

        for (var attempt = 0; attempt < MaximumConcurrencyAttempts; attempt++)
        {
            var account = await userStore.GetByNormalizedNameAsync(normalized, cancellationToken).ConfigureAwait(false);
            if (account is null)
            {
                return AdminFailed("ACCOUNT_NOT_FOUND");
            }

            var hash = passwordHasher.HashPassword(account, newPassword);
            var updated = await userStore.UpdateCredentialsAsync(
                account.Id,
                hash,
                NewSecurityStamp(),
                account.RowVersion,
                Audit("PasswordReset", actor, timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
            if (updated)
            {
                return new AccountAdministrationResult(true, account.Id, null);
            }
        }

        return AdminFailed("ACCOUNT_CONCURRENCY_CONFLICT");
    }

    public async ValueTask<AccountAdministrationResult> SetActiveAsync(
        string userName,
        bool isActive,
        string actor,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeUserName(userName, out var normalized) || !IsValidActor(actor))
        {
            return AdminFailed("ACCOUNT_INPUT_INVALID");
        }

        for (var attempt = 0; attempt < MaximumConcurrencyAttempts; attempt++)
        {
            var account = await userStore.GetByNormalizedNameAsync(normalized, cancellationToken).ConfigureAwait(false);
            if (account is null)
            {
                return AdminFailed("ACCOUNT_NOT_FOUND");
            }

            if (account.IsActive == isActive)
            {
                return new AccountAdministrationResult(true, account.Id, null);
            }

            var updated = await userStore.SetActiveAsync(
                account.Id,
                isActive,
                NewSecurityStamp(),
                account.RowVersion,
                Audit(isActive ? "AccountEnabled" : "AccountDisabled", actor, timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
            if (updated)
            {
                return new AccountAdministrationResult(true, account.Id, null);
            }
        }

        return AdminFailed("ACCOUNT_CONCURRENCY_CONFLICT");
    }

    private void VerifyUnknownAccount(string password) =>
        _ = passwordHasher.VerifyHashedPassword(dummyAccount, dummyHash, password);

    private static UserAccount NewAccount(string userName, string normalizedUserName, string displayName) =>
        new(
            UserId.New(),
            userName,
            normalizedUserName,
            displayName,
            string.Empty,
            NewSecurityStamp(),
            true,
            0,
            null,
            null,
            []);

    private static AccountMutationAudit Audit(string eventType, string actor, DateTimeOffset occurredAtUtc) =>
        new(Guid.NewGuid(), eventType, actor, occurredAtUtc);

    private static AuthenticationResult Failed(string errorCode, DateTimeOffset? lockedUntilUtc = null) =>
        new(false, null, null, errorCode, lockedUntilUtc);

    private static AccountAdministrationResult AdminFailed(string errorCode) => new(false, null, errorCode);

    private static string NewSecurityStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static bool TryNormalizeUserName(string? value, out string normalized)
    {
        normalized = value?.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant() ?? string.Empty;
        return normalized.Length is >= 3 and <= 128 && normalized.All(character =>
            char.IsLetterOrDigit(character) || character is '.' or '-' or '_');
    }

    // This internal-only account boundary intentionally favors operability over internet-facing
    // complexity rules; lockout, rate limiting, salted hashing, and network isolation remain required.
    private static bool IsStrongPassword(string? value) =>
        value is { Length: >= 6 and <= 128 } &&
        value.Any(char.IsLetter) && value.Any(char.IsDigit);

    private static bool IsValidActor(string? actor) =>
        !string.IsNullOrWhiteSpace(actor) && actor.Trim().Length <= 128;

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
