// Computes exact deterministic metrics instead of delegating release decisions to another model.
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace Fadada.CertificationQueryAgent.Evals;

/// <summary>
/// 支撑离线测试中的 DeterministicStandardEvaluators 职责，确保测试过程确定且不访问真实外部系统。
/// </summary>
internal static class DeterministicStandardEvaluators
{
    public static async ValueTask<(bool MafPassed, bool MeaiPassed)> EvaluateAsync(
        EvaluationCase scenario,
        EvaluationTargetOutput actual,
        CancellationToken cancellationToken)
    {
        var response = $"evidence:{actual.EvidenceStatus}";
        var conversation = new List<ChatMessage>
        {
            new(ChatRole.User, scenario.Turns[^1].Content)
        };
        for (var index = 0; index < actual.ToolCalls.Count; index++)
        {
            var arguments = actual.Arguments.ToDictionary(
                pair => pair.Key,
                pair => (object?)pair.Value,
                StringComparer.Ordinal);
            conversation.Add(new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent($"eval-call-{index}", actual.ToolCalls[index], arguments)]));
        }

        conversation.Add(new ChatMessage(ChatRole.Assistant, response));
        var item = new EvalItem(scenario.Turns[^1].Content, response, conversation)
        {
            ExpectedOutput = $"evidence:{scenario.ExpectedEvidence}",
            ExpectedToolCalls = scenario.ExpectedToolCalls.Select(tool => new ExpectedToolCall(
                tool,
                scenario.ExpectedArguments.ToDictionary(
                    pair => pair.Key,
                    pair => (object?)pair.Value,
                    StringComparer.Ordinal)!)).ToArray()
        };

        var localEvaluator = new LocalEvaluator(
            EvalChecks.ToolCallArgsMatch(),
            FunctionEvaluator.Create("exact-tool-sequence", (EvalItem _) =>
                actual.ToolCalls.SequenceEqual(scenario.ExpectedToolCalls, StringComparer.Ordinal)),
            FunctionEvaluator.Create("deterministic-groundedness", (EvalItem value) =>
                string.Equals(value.Response, value.ExpectedOutput, StringComparison.Ordinal)));
        var mafResult = await localEvaluator.EvaluateAsync([item], scenario.Id, cancellationToken);

        var meaiEvaluator = new DeterministicEvidenceEvaluator();
        var meaiResult = await meaiEvaluator.EvaluateAsync(
            conversation,
            new ChatResponse(new ChatMessage(ChatRole.Assistant, response)),
            new ChatConfiguration(new EvaluationOnlyChatClient()),
            additionalContext: null,
            cancellationToken);
        var meaiPassed = meaiResult.Metrics.TryGetValue("grounded_response_shape", out var metric) &&
            metric is BooleanMetric { Value: true };
        return (mafResult.AllPassed, meaiPassed);
    }

    /// <summary>
    /// 支撑离线测试中的 DeterministicEvidenceEvaluator 职责，确保测试过程确定且不访问真实外部系统。
    /// </summary>
    private sealed class DeterministicEvidenceEvaluator : IEvaluator
    {
        public IReadOnlyCollection<string> EvaluationMetricNames { get; } = ["grounded_response_shape"];

        public ValueTask<EvaluationResult> EvaluateAsync(
            IEnumerable<ChatMessage> messages,
            ChatResponse modelResponse,
            ChatConfiguration? chatConfiguration = null,
            IEnumerable<EvaluationContext>? additionalContext = null,
            CancellationToken cancellationToken = default)
        {
            _ = messages;
            _ = chatConfiguration;
            _ = additionalContext;
            cancellationToken.ThrowIfCancellationRequested();
            var value = modelResponse.Text.StartsWith("evidence:", StringComparison.Ordinal) &&
                !modelResponse.Text.Contains("password", StringComparison.OrdinalIgnoreCase) &&
                !modelResponse.Text.Contains("credential", StringComparison.OrdinalIgnoreCase);
            var metric = new BooleanMetric(
                "grounded_response_shape",
                value,
                value ? "Structured evidence status is present." : "Response shape is not evidence-grounded.")
            {
                Interpretation = new EvaluationMetricInterpretation(
                    value ? EvaluationRating.Good : EvaluationRating.Unacceptable,
                    failed: !value,
                    value ? null : "Deterministic evidence response check failed.")
            };
            return ValueTask.FromResult(new EvaluationResult(metric));
        }
    }

    /// <summary>
    /// 支撑离线测试中的 EvaluationOnlyChatClient 职责，确保测试过程确定且不访问真实外部系统。
    /// </summary>
    private sealed class EvaluationOnlyChatClient : IChatClient
    {
        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The deterministic evaluator must not call a judge model.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The deterministic evaluator must not call a judge model.");
    }
}
