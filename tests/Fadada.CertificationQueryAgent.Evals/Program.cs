// CLI entry point writes current-Agent artifacts and fails when any absolute gate regresses.
namespace Fadada.CertificationQueryAgent.Evals;

/// <summary>
/// 作为应用或评测入口集中完成依赖装配和启动，避免初始化顺序散落到业务代码。
/// </summary>
internal static class Program
{
    public static async Task<int> Main()
    {
        try
        {
            var root = RepositoryPaths.FindRoot();
            var golden = DatasetLoader.LoadDataset(Path.Combine(root, "evals", "golden", "agent-golden.v1.json"));
            var redTeam = DatasetLoader.LoadDataset(Path.Combine(root, "evals", "red-team", "security.v1.json"));
            var fixtures = DatasetLoader.LoadFixtures(Path.Combine(root, "evals", "fixtures", "fadada-readonly.v1.json"));
            var dataset = DatasetLoader.Merge(golden, redTeam);
            var engine = new EvaluationEngine();

            var agentTarget = new OfflineAgentTarget(new DeterministicFadadaFixture(fixtures));
            var agent = await engine.RunAsync(dataset, agentTarget);
            var reference = await engine.RunAsync(dataset, new ExpectedReferenceTarget());
            var seededRegression = await engine.RunAsync(dataset, new SeededToolRegressionTarget(new ExpectedReferenceTarget()));
            var seededSafetyRegression = await engine.RunAsync(
                dataset,
                new SeededSafetyRegressionTarget(agentTarget, "security-011", "delete_company"));
            var outputDirectory = Path.Combine(root, "artifacts", "evals");
            ReportWriter.Write(agent, outputDirectory);
            ReportWriter.Write(reference, outputDirectory);
            ReportWriter.Write(seededRegression, outputDirectory);
            ReportWriter.Write(seededSafetyRegression, outputDirectory);

            var agentGatePassed = EvaluationEngine.PassesReleaseGate(agent, 1m);
            var seedDetected = EvaluationEngine.PassesReleaseGate(reference, 1m) &&
                !EvaluationEngine.PassesReleaseGate(seededRegression, 1m) &&
                !EvaluationEngine.PassesReleaseGate(seededSafetyRegression, 1m);
            Console.WriteLine(
                $"Agent cases={agent.Metrics.TotalCases}, passRate={agent.Metrics.PassRate:P2}, " +
                $"gate={agentGatePassed}, safetyViolations={agent.Metrics.SafetyViolations}, " +
                $"invalidToolCalls={agent.Metrics.InvalidToolCalls}, seededRegressionDetected={seedDetected}, " +
                $"modelQualityClaim={agent.SupportsModelQualityClaims}");
            return seedDetected && agentGatePassed ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Eval runner failed: {exception.GetType().Name}");
            return 1;
        }
    }
}
