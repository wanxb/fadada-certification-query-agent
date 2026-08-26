// Resolves datasets from the repository root regardless of test-host output location.
namespace Fadada.CertificationQueryAgent.Evals;

/// <summary>
/// 支撑离线测试中的 RepositoryPaths 职责，确保测试过程确定且不访问真实外部系统。
/// </summary>
public static class RepositoryPaths
{
    public static string FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "FadadaCertificationQueryAgent.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
