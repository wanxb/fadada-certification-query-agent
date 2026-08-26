// Provides deterministic in-memory adapters exclusively for the Development UiDemo profile.
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Fadada.CertificationQueryAgent.Application.AgentTurns;
using Fadada.CertificationQueryAgent.Application.Authentication;
using Fadada.CertificationQueryAgent.Application.Common;
using Fadada.CertificationQueryAgent.Application.Conversations;
using Fadada.CertificationQueryAgent.Application.Persistence;

namespace Fadada.CertificationQueryAgent.Web.Configuration;

/// <summary>
/// 注册仅限开发环境的内存演示依赖，使界面验证不访问真实外部系统。
/// </summary>
internal static class UiDemoServices
{
    public static IServiceCollection AddUiDemoServices(this IServiceCollection services)
    {
        services.AddSingleton<UiDemoState>();
        services.AddSingleton<IAuthenticationService>(provider => provider.GetRequiredService<UiDemoState>());
        services.AddSingleton<IConversationStore>(provider => provider.GetRequiredService<UiDemoState>());
        services.AddSingleton<IAgentTurnStore>(provider => provider.GetRequiredService<UiDemoState>());
        services.AddSingleton<IAgentRuntime, UiDemoAgentRuntime>();
        return services;
    }
}

/// <summary>
/// 为 UI 演示保存隔离的内存账号、会话和回合数据，不承诺生产持久化语义。
/// </summary>
internal sealed class UiDemoState : IAuthenticationService, IConversationStore, IAgentTurnStore
{
    private static readonly UserId DemoUserId = new(Guid.Parse("a1000000-0000-0000-0000-000000000001"));
    private readonly ConcurrentDictionary<ConversationId, DemoConversation> conversations = new();

    public UiDemoState()
    {
        var id = new ConversationId(Guid.Parse("b2000000-0000-0000-0000-000000000001"));
        var now = DateTimeOffset.UtcNow;
        conversations[id] = new DemoConversation(
            new ConversationSummary(id, DemoUserId, "企业签署能力核验", ConversationStatus.Active, now, RowVersion()),
            [
                new ConversationMessage(MessageId.New(), id, null, MessageRole.User, "查询示例科技有限公司的企业信息和印章状态", 1, now.AddMinutes(-3)),
                new ConversationMessage(MessageId.New(), id, null, MessageRole.Assistant,
                    "已取得企业与印章证据。当前演示数据表明：企业主体状态正常，存在一枚可用印章。正式环境中的结论将严格依据法大大只读查询结果生成。",
                    2, now.AddMinutes(-2))
            ]);
    }

    public ValueTask<AuthenticationResult> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var valid = string.Equals(request.UserName.Trim(), "admin", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(request.Password, "123qwe", StringComparison.Ordinal);
        return ValueTask.FromResult(valid
            ? new AuthenticationResult(true, DemoUserId, "ui-demo-security-stamp", null, null)
            : new AuthenticationResult(false, null, null, "AUTH_INVALID_CREDENTIALS", null));
    }

    public ValueTask<bool> ValidateSessionAsync(UserId userId, string securityStamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(userId == DemoUserId && securityStamp == "ui-demo-security-stamp");
    }

    public ValueTask<ConversationSummary> CreateAsync(UserId userId, string title, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = ConversationId.New();
        var summary = new ConversationSummary(id, userId, title, ConversationStatus.Active, DateTimeOffset.UtcNow, RowVersion());
        conversations[id] = new DemoConversation(summary, []);
        return ValueTask.FromResult(summary);
    }

    public ValueTask<ConversationSnapshot?> GetAsync(ConversationId conversationId, UserId userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!conversations.TryGetValue(conversationId, out var value) || value.Summary.UserId != userId)
        {
            return ValueTask.FromResult<ConversationSnapshot?>(null);
        }

        lock (value)
        {
            return ValueTask.FromResult<ConversationSnapshot?>(new ConversationSnapshot(value.Summary, [.. value.Messages]));
        }
    }

    public ValueTask<IReadOnlyList<ConversationSummary>> ListAsync(
        UserId userId,
        ConversationStatus status,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ConversationSummary> values = conversations.Values
            .Select(value => value.Summary)
            .Where(value => value.UserId == userId && value.Status == status)
            .OrderByDescending(value => value.UpdatedAtUtc)
            .ToArray();
        return ValueTask.FromResult(values);
    }

    public ValueTask<bool> ArchiveAsync(ConversationId conversationId, UserId userId, CancellationToken cancellationToken)
    {
        return ChangeStatus(conversationId, userId, ConversationStatus.Active, ConversationStatus.Archived, cancellationToken);
    }

    public ValueTask<bool> RestoreAsync(ConversationId conversationId, UserId userId, CancellationToken cancellationToken)
    {
        return ChangeStatus(conversationId, userId, ConversationStatus.Archived, ConversationStatus.Active, cancellationToken);
    }

    private ValueTask<bool> ChangeStatus(
        ConversationId conversationId,
        UserId userId,
        ConversationStatus expectedStatus,
        ConversationStatus targetStatus,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!conversations.TryGetValue(conversationId, out var value) ||
            value.Summary.UserId != userId ||
            value.Summary.Status != expectedStatus)
        {
            return ValueTask.FromResult(false);
        }

        lock (value)
        {
            value.Summary = value.Summary with
            {
                Status = targetStatus,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                RowVersion = RowVersion()
            };
        }
        return ValueTask.FromResult(true);
    }

    public ValueTask<byte[]> StartAsync(AgentTurnStart turn, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!conversations.TryGetValue(turn.ConversationId, out var value) || value.Summary.UserId != turn.UserId)
        {
            throw new InvalidOperationException("UI_DEMO_CONVERSATION_NOT_FOUND");
        }

        lock (value)
        {
            var title = value.Messages.Count == 0
                ? ConversationTitle.ForDisplay(turn.UserMessage.Content)
                : value.Summary.Title;
            value.Messages.Add(turn.UserMessage);
            value.Summary = value.Summary with
            {
                Title = title,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                RowVersion = RowVersion()
            };
            return ValueTask.FromResult(value.Summary.RowVersion!);
        }
    }

    public ValueTask<byte[]> CompleteAsync(AgentTurnCompletion turn, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!conversations.TryGetValue(turn.ConversationId, out var value) || value.Summary.UserId != turn.UserId)
        {
            throw new InvalidOperationException("UI_DEMO_CONVERSATION_NOT_FOUND");
        }

        lock (value)
        {
            if (turn.AssistantMessage is not null)
            {
                value.Messages.Add(turn.AssistantMessage);
            }
            value.Summary = value.Summary with { UpdatedAtUtc = DateTimeOffset.UtcNow, RowVersion = RowVersion() };
            return ValueTask.FromResult(value.Summary.RowVersion!);
        }
    }

    private static byte[] RowVersion() => BitConverter.GetBytes(DateTime.UtcNow.Ticks);

    /// <summary>
    /// 在开发演示 Profile 中聚合会话摘要与消息列表，不跨请求持久化到真实数据库。
    /// </summary>
    private sealed class DemoConversation(ConversationSummary summary, List<ConversationMessage> messages)
    {
        public ConversationSummary Summary { get; set; } = summary;
        public List<ConversationMessage> Messages { get; } = messages;
    }
}

/// <summary>
/// 生成确定性的演示回答和工具事件，避免 UI 测试调用模型或法大大。
/// </summary>
internal sealed class UiDemoAgentRuntime : IAgentRuntime
{
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new AgentEvent(AgentEventKind.TurnStarted, null, null, null);
        await Task.Delay(220, cancellationToken).ConfigureAwait(false);

        var tools = SelectTools(request.UserMessage);
        foreach (var tool in tools)
        {
            yield return new AgentEvent(AgentEventKind.ToolStarted, null, tool, null);
            await Task.Delay(420, cancellationToken).ConfigureAwait(false);
            yield return new AgentEvent(AgentEventKind.ToolCompleted, null, tool, null);
        }

        const string answer = "查询已完成。演示证据显示主体信息匹配，相关记录状态正常。正式环境会在此处给出基于只读领域工具证据的事实、缺失项和结论。";
        foreach (var chunk in answer.Chunk(12))
        {
            yield return new AgentEvent(AgentEventKind.TextDelta, new string(chunk), null, null);
            await Task.Delay(55, cancellationToken).ConfigureAwait(false);
        }
        yield return new AgentEvent(AgentEventKind.TurnCompleted, null, null, null, 1, tools.Length);
    }

    public async ValueTask<AgentTurnResult> RunOnceAsync(AgentTurnRequest request, CancellationToken cancellationToken)
    {
        var text = new System.Text.StringBuilder();
        var modelCalls = 0;
        var toolCalls = 0;
        await foreach (var value in RunAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (value.Kind == AgentEventKind.TextDelta)
            {
                text.Append(value.Text);
            }
            else if (value.Kind == AgentEventKind.TurnCompleted)
            {
                modelCalls = value.ModelCalls ?? 0;
                toolCalls = value.DomainToolCalls ?? 0;
            }
        }
        return new AgentTurnResult(text.ToString(), modelCalls, toolCalls, "ui-demo");
    }

    private static string[] SelectTools(string message)
    {
        var values = new List<string>();
        if (message.Contains("人员", StringComparison.Ordinal) || message.Contains("手机", StringComparison.Ordinal)) values.Add("query_person");
        if (message.Contains("企业", StringComparison.Ordinal) || message.Contains("公司", StringComparison.Ordinal)) values.Add("query_company");
        if (message.Contains("关系", StringComparison.Ordinal) || message.Contains("关联", StringComparison.Ordinal)) values.Add("query_relationship");
        if (message.Contains("印章", StringComparison.Ordinal)) values.Add("query_seals");
        return values.Count == 0 ? ["query_company"] : [.. values];
    }
}
