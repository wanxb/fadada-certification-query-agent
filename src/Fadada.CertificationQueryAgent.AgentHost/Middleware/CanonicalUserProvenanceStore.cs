// Resolves proposed tool arguments against user-authored conversation text without imposing input templates.
using Fadada.CertificationQueryAgent.Application.Common;
using Fadada.CertificationQueryAgent.Application.Conversations;
using Fadada.CertificationQueryAgent.Application.DomainTools;

namespace Fadada.CertificationQueryAgent.AgentHost.Middleware;

/// <summary>
/// 从用户历史消息提取并规范化参数来源，确保工具值可追溯到用户输入。
/// </summary>
public sealed class CanonicalUserProvenanceStore(IConversationStore conversationStore) : IUserProvenanceStore
{
    public async ValueTask<IReadOnlyList<UserProvidedValue>> ResolveAsync(
        ConversationId conversationId,
        UserId userId,
        IReadOnlyCollection<ProvenanceCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var snapshot = await conversationStore.GetAsync(conversationId, userId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null || snapshot.Conversation.Status != ConversationStatus.Active)
        {
            return [];
        }

        var userMessages = snapshot.Messages
            .Where(message => message.Role == MessageRole.User)
            .OrderByDescending(message => message.SequenceNumber)
            .ToArray();
        var values = new List<UserProvidedValue>(candidates.Count);

        foreach (var candidate in candidates.DistinctBy(value => (value.FieldKind, value.Value)))
        {
            string canonicalValue;
            try
            {
                canonicalValue = ProvenanceCanonicalizer.Canonicalize(candidate.FieldKind, candidate.Value);
            }
            catch (ArgumentException)
            {
                continue;
            }

            var source = userMessages.FirstOrDefault(message =>
                ProvenanceCanonicalizer.IsPresentInUserText(candidate.FieldKind, canonicalValue, message.Content));
            if (source is null)
            {
                continue;
            }

            values.Add(new UserProvidedValue(
                userId,
                conversationId,
                source.Id,
                candidate.FieldKind,
                candidate.Value,
                canonicalValue,
                ConfirmationState.UserExplicit,
                source.CreatedAtUtc));
        }

        return values;
    }
}
