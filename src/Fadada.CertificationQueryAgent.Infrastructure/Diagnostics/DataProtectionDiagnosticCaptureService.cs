// Encrypts opt-in diagnostic payloads before persistence and applies a bounded retention policy.
using Fadada.CertificationQueryAgent.Application.Common;
using Fadada.CertificationQueryAgent.Application.Persistence;
using Microsoft.AspNetCore.DataProtection;

namespace Fadada.CertificationQueryAgent.Infrastructure.Diagnostics;

/// <summary>
/// 使用 Data Protection 加密获准的诊断载荷，并按配置限制大小和保留范围。
/// </summary>
public sealed class DataProtectionDiagnosticCaptureService : IDiagnosticCaptureService
{
    private const string ProtectorPurpose = "Fadada.CertificationQueryAgent.DiagnosticPayload.v1";
    private readonly IDiagnosticPayloadStore store;
    private readonly IDataProtector protector;
    private readonly DiagnosticCaptureOptions options;
    private readonly TimeProvider timeProvider;

    public DataProtectionDiagnosticCaptureService(
        IDiagnosticPayloadStore store,
        IDataProtectionProvider dataProtectionProvider,
        DiagnosticCaptureOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        this.options = options ?? new DiagnosticCaptureOptions();
        this.options.Validate();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<Guid?> CaptureAsync(
        UserId userId,
        string ownerType,
        Guid ownerId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return null;
        }

        if (userId.Value == Guid.Empty || ownerId == Guid.Empty ||
            payload.Length is < 1 || payload.Length > options.MaximumPayloadBytes)
        {
            throw new ArgumentException("Diagnostic capture input is invalid.", nameof(payload));
        }

        var id = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();
        var protectedPayload = protector.Protect(payload.ToArray());
        await store.SaveAsync(
            new DiagnosticPayload(
                id,
                userId,
                ownerType,
                ownerId,
                protectedPayload,
                now.Add(options.EffectiveTimeToLive)),
            cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async ValueTask<byte[]?> ReadAsync(
        Guid payloadId,
        UserId userId,
        CancellationToken cancellationToken)
    {
        var stored = await store.GetAsync(payloadId, userId, cancellationToken).ConfigureAwait(false);
        return stored is null ? null : protector.Unprotect(stored.ProtectedPayload);
    }
}
