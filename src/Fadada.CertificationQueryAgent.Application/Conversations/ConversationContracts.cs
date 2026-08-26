// Conversation ports require the authenticated user ID on every read and mutation for ownership isolation.
using Fadada.CertificationQueryAgent.Application.Common;

namespace Fadada.CertificationQueryAgent.Application.Conversations;

/// <summary>
/// 定义 ConversationStatus 的受控状态集合，避免跨层使用未校验的自由文本。
/// </summary>
public enum ConversationStatus
{
    Active,
    Archived
}

/// <summary>
/// 定义 MessageRole 的受控状态集合，避免跨层使用未校验的自由文本。
/// </summary>
public enum MessageRole
{
    User,
    Assistant
}

/// <summary>
/// 以不可变数据契约表达 ConversationSummary，确保跨层传递时字段语义和边界保持稳定。
/// </summary>
public sealed record ConversationSummary(
    ConversationId Id,
    UserId UserId,
    string Title,
    ConversationStatus Status,
    DateTimeOffset UpdatedAtUtc,
    byte[]? RowVersion = null);

/// <summary>
/// 以不可变契约保存 ConversationMessage 的关键状态，支持审计、恢复和确定性处理。
/// </summary>
public sealed record ConversationMessage(
    MessageId Id,
    ConversationId ConversationId,
    TurnId? TurnId,
    MessageRole Role,
    string Content,
    long SequenceNumber,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// 以不可变契约保存 ConversationSnapshot 的关键状态，支持审计、恢复和确定性处理。
/// </summary>
public sealed record ConversationSnapshot(
    ConversationSummary Conversation,
    IReadOnlyList<ConversationMessage> Messages);

/// <summary>
/// 定义 IConversationStore 的稳定端口，使应用逻辑不依赖具体基础设施并便于替换测试实现。
/// </summary>
public interface IConversationStore
{
    ValueTask<ConversationSummary> CreateAsync(
        UserId userId,
        string title,
        CancellationToken cancellationToken);

    ValueTask<ConversationSnapshot?> GetAsync(
        ConversationId conversationId,
        UserId userId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ConversationSummary>> ListAsync(
        UserId userId,
        ConversationStatus status,
        CancellationToken cancellationToken);

    ValueTask<bool> ArchiveAsync(
        ConversationId conversationId,
        UserId userId,
        CancellationToken cancellationToken);

    ValueTask<bool> RestoreAsync(
        ConversationId conversationId,
        UserId userId,
        CancellationToken cancellationToken);
}
