// Verifies semantic Markdown output and the XSS boundary used by assistant messages.
using Fadada.CertificationQueryAgent.Web.Rendering;

namespace Fadada.CertificationQueryAgent.WebTests;

/// <summary>
/// 验证 AgentMarkdownRendererTests 所覆盖的行为、边界条件和安全回归约束。
/// </summary>
public sealed class AgentMarkdownRendererTests
{
    private readonly AgentMarkdownRenderer renderer = new();

    [Fact]
    public void Render_ProducesSemanticMarkupForAgentMarkdown()
    {
        var html = renderer.Render("""
            查询结果：

            1. **企业已认证**
            2. 管理员手机号：`13800000000`
            """).Value;

        Assert.Contains("<ol>", html, StringComparison.Ordinal);
        Assert.Contains("<strong>企业已认证</strong>", html, StringComparison.Ordinal);
        Assert.Contains("<code>13800000000</code>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_DisablesRawHtmlAndUnsafeLinkSchemes()
    {
        var html = renderer.Render("<script>alert('x')</script> [危险链接](javascript:alert('x'))").Value;

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
    }
}
