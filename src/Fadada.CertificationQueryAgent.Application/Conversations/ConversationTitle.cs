// Produces a stable, user-recognizable title from the first message without model involvement.
using System.Text.RegularExpressions;

namespace Fadada.CertificationQueryAgent.Application.Conversations;

/// <summary>
/// 从首条用户输入生成可展示的会话标题，并统一处理空白、控制字符和最大长度。
/// </summary>
public static partial class ConversationTitle
{
    public const int MaximumLength = 200;

    public static string FromFirstUserMessage(string message)
    {
        var normalized = ForDisplay(message);
        return normalized.Length <= MaximumLength ? normalized : normalized[..MaximumLength];
    }

    public static string ForDisplay(string message)
    {
        var normalized = Whitespace().Replace(message?.Trim() ?? string.Empty, " ");
        if (normalized.Length == 0)
        {
            throw new ArgumentException("The first user message cannot be empty.", nameof(message));
        }

        return normalized;
    }

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
