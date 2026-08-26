// Verifies diagnostic content is opt-in, encrypted at rest, bounded, and lifecycle-managed.
using Fadada.CertificationQueryAgent.Application.Common;
using Fadada.CertificationQueryAgent.Application.Persistence;
using Fadada.CertificationQueryAgent.Infrastructure.Diagnostics;
using Microsoft.AspNetCore.DataProtection;

namespace Fadada.CertificationQueryAgent.IntegrationTests;

/// <summary>
/// 验证 DiagnosticCaptureTests 所覆盖的行为、边界条件和安全回归约束。
/// </summary>
public sealed class DiagnosticCaptureTests
{
    [Fact]
    public async Task Capture_is_disabled_by_default()
    {
        var store = new RecordingPayloadStore();
        var service = new DataProtectionDiagnosticCaptureService(
            store,
            new EphemeralDataProtectionProvider());

        var id = await service.CaptureAsync(
            UserId.New(), "Turn", Guid.NewGuid(), new byte[] { 1, 2, 3 }, CancellationToken.None);

        Assert.Null(id);
        Assert.Null(store.Value);
    }

    [Fact]
    public async Task Enabled_capture_protects_payload_and_round_trips_for_owner()
    {
        var store = new RecordingPayloadStore();
        var service = new DataProtectionDiagnosticCaptureService(
            store,
            new EphemeralDataProtectionProvider(),
            new DiagnosticCaptureOptions(true, TimeSpan.FromHours(1)));
        var userId = UserId.New();
        var raw = new byte[] { 1, 2, 3, 4 };

        var id = await service.CaptureAsync(userId, "Turn", Guid.NewGuid(), raw, CancellationToken.None);
        var restored = await service.ReadAsync(id!.Value, userId, CancellationToken.None);

        Assert.NotNull(store.Value);
        Assert.False(raw.SequenceEqual(store.Value!.ProtectedPayload));
        Assert.Equal(raw, restored);
        Assert.InRange(store.Value.ExpiresAtUtc, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(2));
        Assert.Null(await service.ReadAsync(id.Value, UserId.New(), CancellationToken.None));
    }

    [Fact]
    public void Diagnostic_ttl_cannot_exceed_seven_days()
    {
        var options = new DiagnosticCaptureOptions(true, TimeSpan.FromDays(8));

        Assert.Equal("DIAGNOSTIC_CAPTURE_OPTIONS_INVALID", Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }

    /// <summary>
    /// 为所属测试提供可观测且无外部副作用的 RecordingPayloadStore 测试替身。
    /// </summary>
    private sealed class RecordingPayloadStore : IDiagnosticPayloadStore
    {
        public DiagnosticPayload? Value { get; private set; }

        public ValueTask SaveAsync(DiagnosticPayload payload, CancellationToken cancellationToken)
        {
            Value = payload;
            return ValueTask.CompletedTask;
        }

        public ValueTask<DiagnosticPayload?> GetAsync(
            Guid payloadId,
            UserId userId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Value is { } value && value.Id == payloadId && value.UserId == userId ? value : null);

        public ValueTask<int> DeleteExpiredAsync(
            DateTimeOffset expiresBeforeUtc,
            int batchSize,
            CancellationToken cancellationToken) => ValueTask.FromResult(0);
    }
}
