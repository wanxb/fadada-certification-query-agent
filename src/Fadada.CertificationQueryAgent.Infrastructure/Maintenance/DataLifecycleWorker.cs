using Fadada.CertificationQueryAgent.Application.Persistence;
using Microsoft.Extensions.Hosting;

namespace Fadada.CertificationQueryAgent.Infrastructure.Maintenance;

/// <summary>
/// 在后台按受控批次清理过期数据，避免维护任务阻塞请求或无界删除。
/// </summary>
public sealed class DataLifecycleWorker : BackgroundService
{
    private readonly IDataLifecycleStore store;
    private readonly DataLifecycleOptions options;
    private readonly TimeProvider timeProvider;

    public DataLifecycleWorker(
        IDataLifecycleStore store,
        DataLifecycleOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.options = options ?? new DataLifecycleOptions();
        this.options.Validate();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = timeProvider.GetUtcNow();
                await store.CleanupAsync(
                    new MaintenanceCleanupRequest(
                        Guid.NewGuid(),
                        now,
                        now.Subtract(options.EffectiveArchivedConversationRetention),
                        options.BatchSize,
                        now),
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // The store records a safe failed maintenance run when the database is reachable.
            }

            await Task.Delay(options.EffectiveRunInterval, timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }
}
