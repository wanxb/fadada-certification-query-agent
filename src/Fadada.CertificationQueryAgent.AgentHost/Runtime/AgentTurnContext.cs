// Owns mutable counters and event delivery for exactly one authenticated agent turn.
using System.Threading.Channels;
using Fadada.CertificationQueryAgent.Application.AgentTurns;
using Fadada.CertificationQueryAgent.Application.Common;

namespace Fadada.CertificationQueryAgent.AgentHost.Runtime;

/// <summary>
/// 保存当前异步调用链的单轮状态，并以原子计数执行模型和工具预算。
/// </summary>
internal sealed class AgentTurnContext
{
    private int _modelCalls;
    private int _domainToolCalls;

    public AgentTurnContext(
        AgentTurnRequest request,
        AgentRuntimeOptions options,
        ChannelWriter<AgentEvent>? eventWriter)
    {
        Request = request;
        Options = options;
        EventWriter = eventWriter;
    }

    public AgentTurnRequest Request { get; }

    public AgentRuntimeOptions Options { get; }

    public ChannelWriter<AgentEvent>? EventWriter { get; }

    public ToolCallId CurrentToolCallId { get; set; }

    public int ModelCalls => Volatile.Read(ref _modelCalls);

    public int DomainToolCalls => Volatile.Read(ref _domainToolCalls);

    public void BeginModelCall()
    {
        if (Interlocked.Increment(ref _modelCalls) > Options.MaxModelCalls)
        {
            throw new AgentRuntimeException("AGENT_MODEL_CALL_BUDGET_EXCEEDED");
        }
    }

    public bool TryBeginDomainTool()
    {
        while (true)
        {
            var current = Volatile.Read(ref _domainToolCalls);
            if (current >= Options.MaxDomainToolCalls)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _domainToolCalls, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    public void Emit(AgentEvent value) => EventWriter?.TryWrite(value);
}

/// <summary>
/// 通过异步上下文传递当前轮次状态，避免并发请求之间共享预算或身份。
/// </summary>
internal static class AgentTurnContextAccessor
{
    private static readonly AsyncLocal<AgentTurnContext?> CurrentContext = new();

    public static AgentTurnContext Current =>
        CurrentContext.Value ?? throw new AgentRuntimeException("AGENT_RUN_CONTEXT_MISSING");

    public static AgentTurnContext? CurrentOrNull => CurrentContext.Value;

    public static IDisposable Push(AgentTurnContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var previous = CurrentContext.Value;
        CurrentContext.Value = context;
        return new Scope(previous);
    }

    /// <summary>
    /// 在释放时恢复进入当前轮次前的异步上下文，防止嵌套调用污染后续请求状态。
    /// </summary>
    private sealed class Scope(AgentTurnContext? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            CurrentContext.Value = previous;
            _disposed = true;
        }
    }
}
