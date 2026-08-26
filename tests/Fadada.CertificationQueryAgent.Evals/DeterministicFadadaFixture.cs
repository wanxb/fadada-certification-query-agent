// Supplies synthetic provider evidence so evaluation results remain offline and reproducible.
namespace Fadada.CertificationQueryAgent.Evals;

/// <summary>
/// 封装 DeterministicFadadaFixture 测试场景所需的固定输入和可验证状态，减少用例间重复装配。
/// </summary>
public sealed class DeterministicFadadaFixture(FixtureDataset dataset)
{
    private static readonly HashSet<string> ApprovedEndpointKeys = new(StringComparer.Ordinal)
    {
        "get_access_token",
        "get_account",
        "get_person_verification",
        "get_company",
        "get_company_verification",
        "get_seals",
        "get_seal_info"
    };

    public FixtureResponse Resolve(string fixtureKey)
    {
        if (!dataset.Responses.TryGetValue(fixtureKey, out var response))
        {
            throw new InvalidDataException($"Unknown fixture key '{fixtureKey}'.");
        }

        if (response.SourceEndpointKeys.Any(key => !ApprovedEndpointKeys.Contains(key)))
        {
            throw new InvalidDataException($"Fixture '{fixtureKey}' contains a non-read-only endpoint.");
        }

        return response;
    }
}
