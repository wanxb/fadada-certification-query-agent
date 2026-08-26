// Covers password verification, lockout, security stamps, and concurrent account updates.
using Fadada.CertificationQueryAgent.Application.Authentication;
using Fadada.CertificationQueryAgent.Application.Common;
using Fadada.CertificationQueryAgent.Infrastructure.Authentication;

namespace Fadada.CertificationQueryAgent.IntegrationTests;

/// <summary>
/// 验证 LocalAccountServiceTests 所覆盖的行为、边界条件和安全回归约束。
/// </summary>
public sealed class LocalAccountServiceTests
{
    [Fact]
    public async Task Create_and_authenticate_use_a_versioned_password_hash()
    {
        var store = new InMemoryUserStore();
        var service = new LocalAccountService(store);

        var created = await service.CreateAsync(
            "query.user",
            "Query User",
            "Strong-Password-2026!",
            "local-admin:test",
            CancellationToken.None);
        var authenticated = await service.AuthenticateAsync(
            new LoginRequest("QUERY.USER", "Strong-Password-2026!"),
            CancellationToken.None);

        Assert.True(created.Succeeded);
        Assert.True(authenticated.Succeeded);
        Assert.NotEqual("Strong-Password-2026!", store.Single.PasswordHash);
        Assert.Equal("AccountCreated", store.Audits[0].EventType);
        Assert.Equal("LoginSucceeded", store.Audits[1].EventType);
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account()
    {
        var store = new InMemoryUserStore();
        var service = new LocalAccountService(store, new AuthenticationPolicy(3, TimeSpan.FromMinutes(10)));
        await service.CreateAsync("locked.user", "Locked User", "Strong-Password-2026!", "local-admin:test", CancellationToken.None);

        AuthenticationResult result = null!;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            result = await service.AuthenticateAsync(new LoginRequest("locked.user", "wrong"), CancellationToken.None);
        }

        Assert.False(result.Succeeded);
        Assert.Equal("AUTH_ACCOUNT_LOCKED", result.ErrorCode);
        Assert.NotNull(result.LockedUntilUtc);
        Assert.Equal("AccountLocked", store.Audits[^1].EventType);
    }

    [Fact]
    public async Task Password_reset_and_disable_invalidate_existing_sessions()
    {
        var store = new InMemoryUserStore();
        var service = new LocalAccountService(store);
        await service.CreateAsync("stamp.user", "Stamp User", "Strong-Password-2026!", "local-admin:test", CancellationToken.None);
        var login = await service.AuthenticateAsync(
            new LoginRequest("stamp.user", "Strong-Password-2026!"),
            CancellationToken.None);

        Assert.True(await service.ValidateSessionAsync(login.UserId!.Value, login.SecurityStamp!, CancellationToken.None));
        Assert.True((await service.ResetPasswordAsync(
            "stamp.user", "Another-Password-2026!", "local-admin:test", CancellationToken.None)).Succeeded);
        Assert.False(await service.ValidateSessionAsync(login.UserId.Value, login.SecurityStamp!, CancellationToken.None));
        Assert.True((await service.SetActiveAsync("stamp.user", false, "local-admin:test", CancellationToken.None)).Succeeded);
        Assert.False((await service.AuthenticateAsync(
            new LoginRequest("stamp.user", "Another-Password-2026!"), CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task Internal_password_policy_accepts_six_character_alphanumeric_passwords()
    {
        // The supported internal credential format must work for both provisioning and rotation.
        var store = new InMemoryUserStore();
        var service = new LocalAccountService(store);

        var created = await service.CreateAsync(
            "simple.user", "Simple User", "123qwe", "local-admin:test", CancellationToken.None);
        var reset = await service.ResetPasswordAsync(
            "simple.user", "qwe123", "local-admin:test", CancellationToken.None);
        var authenticated = await service.AuthenticateAsync(
            new LoginRequest("simple.user", "qwe123"), CancellationToken.None);

        Assert.True(created.Succeeded);
        Assert.True(reset.Succeeded);
        Assert.True(authenticated.Succeeded);
    }

    [Theory]
    [InlineData("1qwe")]
    [InlineData("123456")]
    [InlineData("letters")]
    public async Task Internal_password_policy_rejects_short_or_single_category_passwords(string password)
    {
        // Minimum entropy and mixed character categories remain enforced despite relaxed complexity.
        var service = new LocalAccountService(new InMemoryUserStore());

        var result = await service.CreateAsync(
            "invalid.user", "Invalid User", password, "local-admin:test", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("ACCOUNT_INPUT_INVALID", result.ErrorCode);
    }

    /// <summary>
    /// 为所属测试提供可观测且无外部副作用的 InMemoryUserStore 测试替身。
    /// </summary>
    private sealed class InMemoryUserStore : IUserStore
    {
        private readonly Dictionary<UserId, UserAccount> accounts = [];

        public List<AccountMutationAudit> Audits { get; } = [];

        public UserAccount Single => Assert.Single(accounts.Values);

        public ValueTask<UserAccount?> GetByNormalizedNameAsync(string normalizedUserName, CancellationToken cancellationToken) =>
            ValueTask.FromResult(accounts.Values.SingleOrDefault(account => account.NormalizedUserName == normalizedUserName));

        public ValueTask<UserAccount?> GetByIdAsync(UserId userId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(accounts.GetValueOrDefault(userId));

        public ValueTask CreateAsync(UserAccount account, AccountMutationAudit audit, CancellationToken cancellationToken)
        {
            accounts.Add(account.Id, account with { RowVersion = NextVersion([]) });
            Audits.Add(audit);
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> UpdateAuthenticationStateAsync(
            UserId userId,
            int accessFailedCount,
            DateTimeOffset? lockoutEndUtc,
            DateTimeOffset? lastLoginAtUtc,
            byte[] expectedRowVersion,
            AccountMutationAudit audit,
            CancellationToken cancellationToken) =>
            Update(userId, expectedRowVersion, account => account with
            {
                AccessFailedCount = accessFailedCount,
                LockoutEndUtc = lockoutEndUtc,
                LastLoginAtUtc = lastLoginAtUtc
            }, audit);

        public ValueTask<bool> UpdateCredentialsAsync(
            UserId userId,
            string passwordHash,
            string securityStamp,
            byte[] expectedRowVersion,
            AccountMutationAudit audit,
            CancellationToken cancellationToken) =>
            Update(userId, expectedRowVersion, account => account with
            {
                PasswordHash = passwordHash,
                SecurityStamp = securityStamp,
                AccessFailedCount = 0,
                LockoutEndUtc = null
            }, audit);

        public ValueTask<bool> SetActiveAsync(
            UserId userId,
            bool isActive,
            string securityStamp,
            byte[] expectedRowVersion,
            AccountMutationAudit audit,
            CancellationToken cancellationToken) =>
            Update(userId, expectedRowVersion, account => account with
            {
                IsActive = isActive,
                SecurityStamp = securityStamp,
                AccessFailedCount = 0,
                LockoutEndUtc = null
            }, audit);

        private ValueTask<bool> Update(
            UserId userId,
            byte[] expectedRowVersion,
            Func<UserAccount, UserAccount> mutation,
            AccountMutationAudit audit)
        {
            if (!accounts.TryGetValue(userId, out var account) || !account.RowVersion.SequenceEqual(expectedRowVersion))
            {
                return ValueTask.FromResult(false);
            }

            accounts[userId] = mutation(account) with { RowVersion = NextVersion(account.RowVersion) };
            Audits.Add(audit);
            return ValueTask.FromResult(true);
        }

        private static byte[] NextVersion(byte[] current)
        {
            var value = current.Length == sizeof(long) ? BitConverter.ToInt64(current) : 0;
            return BitConverter.GetBytes(value + 1);
        }
    }
}
