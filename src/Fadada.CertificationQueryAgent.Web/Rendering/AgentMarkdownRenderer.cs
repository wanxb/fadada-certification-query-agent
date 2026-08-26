// Renders model-authored Markdown through a restrictive allowlist before it reaches Blazor markup.
using Ganss.Xss;
using Markdig;
using Microsoft.AspNetCore.Components;

namespace Fadada.CertificationQueryAgent.Web.Rendering;

/// <summary>
/// 将 Agent Markdown 转换为经过白名单清洗的 HTML，阻断脚本和危险链接。
/// </summary>
public sealed class AgentMarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    private static readonly string[] AllowedTags =
    [
        "a", "blockquote", "br", "code", "del", "em", "h1", "h2", "h3", "h4", "h5", "h6", "hr",
        "li", "ol", "p", "pre", "strong", "table", "tbody", "td", "th", "thead", "tr", "ul"
    ];

    public MarkupString Render(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return default;
        }

        var sanitizer = CreateSanitizer();
        var html = Markdown.ToHtml(markdown, Pipeline);
        return new MarkupString(sanitizer.Sanitize(html));
    }

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Clear();
        sanitizer.AllowedAttributes.Clear();
        sanitizer.AllowedSchemes.Clear();

        foreach (var tag in AllowedTags)
        {
            sanitizer.AllowedTags.Add(tag);
        }

        sanitizer.AllowedAttributes.Add("href");
        sanitizer.AllowedAttributes.Add("title");
        sanitizer.AllowedSchemes.Add("http");
        sanitizer.AllowedSchemes.Add("https");
        return sanitizer;
    }
}
