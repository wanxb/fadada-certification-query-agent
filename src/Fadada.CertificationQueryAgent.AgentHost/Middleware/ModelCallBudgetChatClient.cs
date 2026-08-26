// Enforces the per-turn model-call ceiling before requests cross the provider boundary.
using System.Runtime.CompilerServices;
using Fadada.CertificationQueryAgent.AgentHost.Runtime;
using Microsoft.Extensions.AI;

namespace Fadada.CertificationQueryAgent.AgentHost.Middleware;

/// <summary>
/// 在每次模型调用前消耗轮次预算，达到上限时立即阻断后续请求。
/// </summary>
internal sealed class ModelCallBudgetChatClient(IChatClient innerClient)
    : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        AgentTurnContextAccessor.Current.BeginModelCall();
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        AgentTurnContextAccessor.Current.BeginModelCall();

        await foreach (var update in base
            .GetStreamingResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }
}
