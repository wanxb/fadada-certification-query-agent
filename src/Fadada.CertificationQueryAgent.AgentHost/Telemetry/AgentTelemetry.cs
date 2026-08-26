// Emits low-cardinality measurements and deliberately excludes prompts, arguments, and evidence.
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Fadada.CertificationQueryAgent.Application.AgentTurns;
using Fadada.CertificationQueryAgent.Application.Observability;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Fadada.CertificationQueryAgent.AgentHost.Telemetry;

/// <summary>
/// 统一记录 Agent 模型、工具和轮次指标，且只允许写入非敏感标签。
/// </summary>
internal static class AgentTelemetry
{
    public static readonly ActivitySource Activities = new(TelemetryNames.ActivitySource);
    private static readonly Meter Meter = new(TelemetryNames.Meter);
    private static readonly Histogram<double> TurnDuration = Meter.CreateHistogram<double>("agent.turn.duration", "s");
    private static readonly Counter<long> ModelCalls = Meter.CreateCounter<long>("agent.model.calls");
    private static readonly Counter<long> InputTokens = Meter.CreateCounter<long>("agent.model.tokens.input");
    private static readonly Counter<long> OutputTokens = Meter.CreateCounter<long>("agent.model.tokens.output");
    private static readonly Counter<double> EstimatedCost = Meter.CreateCounter<double>("agent.estimated.cost");
    private static readonly Counter<long> ToolCalls = Meter.CreateCounter<long>("agent.tool.calls");

    public static Activity? StartTurn(AgentTurnRequest request)
    {
        var activity = Activities.StartActivity("agent.turn", ActivityKind.Internal);
        activity?.SetTag("enduser.id", request.UserId.Value.ToString("N"));
        activity?.SetTag("agent.turn.id", request.TurnId.Value.ToString("N"));
        activity?.SetTag("trace.correlation_id", request.TraceId.ToString("N"));
        return activity;
    }

    public static Activity? StartTool(string toolName)
    {
        var activity = Activities.StartActivity("agent.tool", ActivityKind.Internal);
        activity?.SetTag("gen_ai.tool.name", toolName);
        return activity;
    }

    public static void RecordTurn(TimeSpan duration, string status) =>
        TurnDuration.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("status", status));

    public static void RecordModelCall(long inputTokens, long outputTokens, decimal estimatedCost, string status)
    {
        var tag = new KeyValuePair<string, object?>("status", status);
        ModelCalls.Add(1, tag);
        InputTokens.Add(inputTokens, tag);
        OutputTokens.Add(outputTokens, tag);
        EstimatedCost.Add(decimal.ToDouble(estimatedCost), tag);
    }

    public static void RecordTool(string toolName, string status) => ToolCalls.Add(
        1,
        new KeyValuePair<string, object?>("tool", toolName),
        new KeyValuePair<string, object?>("status", status));
}

/// <summary>
/// 从模型响应提取令牌用量等安全元数据，避免遥测采集消息正文。
/// </summary>
public static class ChatClientTelemetry
{
    public static IChatClient Wrap(IChatClient innerClient, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(innerClient);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        return new ChatClientBuilder(innerClient)
            .UseOpenTelemetry(
                loggerFactory,
                TelemetryNames.GenAiSource,
                instrumentation => instrumentation.EnableSensitiveData = false)
            .Build();
    }
}
