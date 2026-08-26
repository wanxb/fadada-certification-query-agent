// Central names keep traces and metrics queryable across hosts without duplicating string literals.
namespace Fadada.CertificationQueryAgent.Application.Observability;

/// <summary>
/// 集中定义跨模块共享的遥测名称，避免 Trace、Metric 因自由字符串产生不兼容维度。
/// </summary>
public static class TelemetryNames
{
    public const string ServiceName = "Fadada.CertificationQueryAgent";
    public const string ActivitySource = "Fadada.CertificationQueryAgent.Application";
    public const string Meter = "Fadada.CertificationQueryAgent.Application";
    public const string GenAiSource = "Fadada.CertificationQueryAgent.GenAI";
}
