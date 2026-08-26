# Starts one explicit runtime profile and restores the caller's process environment on exit.
[CmdletBinding()]
param(
    [switch]$UiDemo,
    [string]$Urls = "http://localhost:5256",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$project = Join-Path $repositoryRoot "src\Fadada.CertificationQueryAgent.Web\Fadada.CertificationQueryAgent.Web.csproj"
$localSettings = Join-Path $repositoryRoot "src\Fadada.CertificationQueryAgent.Web\appsettings.Local.json"
$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
$previousProfile = $env:Persistence__Profile

try
{
    if ($UiDemo)
    {
        $env:ASPNETCORE_ENVIRONMENT = "Development"
        $env:Persistence__Profile = "UiDemo"
    }
    elseif (Test-Path -LiteralPath $localSettings -PathType Leaf)
    {
        $env:ASPNETCORE_ENVIRONMENT = "Development"
    }
    else
    {
        $required = @(
            "ConnectionStrings__FddDomainAgent",
            "Persistence__Profile",
            "Model__BaseUrl",
            "Model__ApiKey",
            "Model__Name",
            "Fadada__BaseUrl",
            "Fadada__AppId",
            "Fadada__AppSecret"
        )
        $missing = @($required | Where-Object {
            [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_, "Process"))
        })
        if ($missing.Count -gt 0)
        {
            throw "Required process configuration is missing: $($missing -join ', ')."
        }
    }

    $arguments = @(
        "run",
        "--project", $project,
        "--configuration", $Configuration,
        "--no-launch-profile"
    )
    if ($NoBuild)
    {
        $arguments += "--no-build"
    }
    $arguments += @("--", "--urls", $Urls)

    & dotnet @arguments
    exit $LASTEXITCODE
}
finally
{
    $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
    $env:Persistence__Profile = $previousProfile
}
