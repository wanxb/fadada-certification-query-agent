// Loads versioned evaluation datasets strictly so malformed cases cannot silently weaken a gate.
using System.Text.Json;

namespace Fadada.CertificationQueryAgent.Evals;

/// <summary>
/// 支撑离线测试中的 DatasetLoader 职责，确保测试过程确定且不访问真实外部系统。
/// </summary>
public static class DatasetLoader
{
    public static EvaluationDataset LoadDataset(string path)
    {
        var dataset = JsonSerializer.Deserialize(
            File.ReadAllText(path),
            EvaluationJsonContext.Default.EvaluationDataset)
            ?? throw new InvalidDataException($"Dataset '{Path.GetFileName(path)}' was empty.");
        Validate(dataset);
        return dataset;
    }

    public static FixtureDataset LoadFixtures(string path)
    {
        var fixtures = JsonSerializer.Deserialize(
            File.ReadAllText(path),
            EvaluationJsonContext.Default.FixtureDataset)
            ?? throw new InvalidDataException($"Fixture '{Path.GetFileName(path)}' was empty.");
        if (fixtures.SchemaVersion != "1.0" || fixtures.Responses.Count == 0)
        {
            throw new InvalidDataException("Fixture schema version or responses are invalid.");
        }

        return fixtures;
    }

    public static EvaluationDataset Merge(params EvaluationDataset[] datasets)
    {
        if (datasets.Length == 0 || datasets.Any(dataset => dataset.SchemaVersion != "1.0"))
        {
            throw new InvalidDataException("At least one compatible evaluation dataset is required.");
        }

        var cases = datasets.SelectMany(dataset => dataset.Cases).ToArray();
        var merged = new EvaluationDataset(
            "1.0",
            string.Join('+', datasets.Select(dataset => dataset.DatasetVersion)),
            cases);
        Validate(merged);
        return merged;
    }

    private static void Validate(EvaluationDataset dataset)
    {
        if (dataset.SchemaVersion != "1.0" || string.IsNullOrWhiteSpace(dataset.DatasetVersion))
        {
            throw new InvalidDataException("Dataset schema or version is invalid.");
        }

        if (dataset.Cases.Count == 0 ||
            dataset.Cases.Select(scenario => scenario.Id).Distinct(StringComparer.Ordinal).Count() != dataset.Cases.Count)
        {
            throw new InvalidDataException("Dataset case IDs must be non-empty and unique.");
        }

        foreach (var scenario in dataset.Cases)
        {
            if (string.IsNullOrWhiteSpace(scenario.Id) ||
                string.IsNullOrWhiteSpace(scenario.Category) ||
                scenario.Tags.Count == 0 ||
                scenario.Turns.Count == 0 ||
                scenario.Repetitions < 1 ||
                scenario.Turns.Any(turn => turn.Role is not "user" || string.IsNullOrWhiteSpace(turn.MessageId)) ||
                scenario.ExpectedToolCalls.Intersect(scenario.ForbiddenToolCalls, StringComparer.Ordinal).Any())
            {
                throw new InvalidDataException($"Dataset case '{scenario.Id}' is invalid.");
            }
        }
    }
}
