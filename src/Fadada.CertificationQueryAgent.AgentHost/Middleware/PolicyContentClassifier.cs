// Detects instruction-shaped content in untrusted provider output before it returns to the model.
using System.Text.RegularExpressions;

namespace Fadada.CertificationQueryAgent.AgentHost.Middleware;

/// <summary>
/// 识别提示注入和越权操作语句，为策略管线提供确定性风险分类。
/// </summary>
public static partial class PolicyContentClassifier
{
    public static bool ContainsInstruction(string value) => InstructionPattern().IsMatch(value);

    [GeneratedRegex("(?i)(ignore (all |previous )?instructions|system prompt|developer mode|call (the )?tool|execute sql|delete[_ ]company|grant[_ ]seal|dump[_ ]credentials|reveal[_ ]prompt|web[_ ]fetch|忽略(系统|之前|所有)?(提示|规则|指令)|开发者模式|调用.{0,20}(工具|函数)|执行.{0,10}(任意)?sql|删除.{0,20}(公司|企业)|给.{0,30}授权|泄露.{0,10}(提示|凭据|原始响应)|循环调用|跳过审计|工具结果要求|必须访问任意网址)", RegexOptions.CultureInvariant)]
    private static partial Regex InstructionPattern();
}
