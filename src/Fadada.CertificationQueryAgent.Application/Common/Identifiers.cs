// Strong identifier wrappers prevent accidental substitution across user, conversation, turn, and tool scopes.
namespace Fadada.CertificationQueryAgent.Application.Common;

/// <summary>
/// 以强类型值表示 UserId，防止不同业务标识在编译期被误用或混传。
/// </summary>
public readonly record struct UserId(Guid Value)
{
    public static UserId New() => new(Guid.NewGuid());
}

/// <summary>
/// 以强类型值表示 ConversationId，防止不同业务标识在编译期被误用或混传。
/// </summary>
public readonly record struct ConversationId(Guid Value)
{
    public static ConversationId New() => new(Guid.NewGuid());
}

/// <summary>
/// 以强类型值表示 TurnId，防止不同业务标识在编译期被误用或混传。
/// </summary>
public readonly record struct TurnId(Guid Value)
{
    public static TurnId New() => new(Guid.NewGuid());
}

/// <summary>
/// 以强类型值表示 MessageId，防止不同业务标识在编译期被误用或混传。
/// </summary>
public readonly record struct MessageId(Guid Value)
{
    public static MessageId New() => new(Guid.NewGuid());
}

/// <summary>
/// 以强类型值表示 ToolCallId，防止不同业务标识在编译期被误用或混传。
/// </summary>
public readonly record struct ToolCallId(Guid Value)
{
    public static ToolCallId New() => new(Guid.NewGuid());
}
