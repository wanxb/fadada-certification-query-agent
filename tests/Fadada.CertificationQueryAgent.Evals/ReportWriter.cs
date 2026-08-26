// Writes machine-readable and human-readable results without embedding prompts or credentials.
using System.Text.Json;
using System.Xml;

namespace Fadada.CertificationQueryAgent.Evals;

/// <summary>
/// 支撑离线测试中的 ReportWriter 职责，确保测试过程确定且不访问真实外部系统。
/// </summary>
public static class ReportWriter
{
    public static void Write(EvaluationReport report, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var safeTargetName = string.Concat(report.Target.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-'));
        var jsonPath = Path.Combine(outputDirectory, $"{safeTargetName}.json");
        var junitPath = Path.Combine(outputDirectory, $"{safeTargetName}.junit.xml");
        File.WriteAllText(
            jsonPath,
            JsonSerializer.Serialize(report, EvaluationJsonContext.Default.EvaluationReport));

        var settings = new XmlWriterSettings { Indent = true };
        using var writer = XmlWriter.Create(junitPath, settings);
        writer.WriteStartDocument();
        writer.WriteStartElement("testsuite");
        writer.WriteAttributeString("name", report.Target);
        writer.WriteAttributeString("tests", report.Metrics.TotalCases.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteAttributeString("failures", (report.Metrics.TotalCases - report.Metrics.PassedCases).ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var result in report.Cases)
        {
            writer.WriteStartElement("testcase");
            writer.WriteAttributeString("classname", result.Category);
            writer.WriteAttributeString("name", result.Id);
            if (!result.Passed)
            {
                writer.WriteStartElement("failure");
                writer.WriteAttributeString("message", string.Join(',', result.Failures));
                writer.WriteString("Deterministic evaluation mismatch; inspect the sanitized JSON report.");
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }
}
