# Run from the existing RelayMcp project folder after copying the patch.
# Requires a .NET SDK and access to the configured NuGet package feed.
$ErrorActionPreference = 'Stop'
$projects = @(Get-ChildItem -Path . -Filter '*.csproj' -File)
if ($projects.Count -ne 1) {
    throw "Expected exactly one .csproj in current directory; found $($projects.Count). Run this from the project root."
}
$project = $projects[0].FullName
if (Select-String -Path $project -Pattern 'ModelContextProtocol.AspNetCore' -Quiet) {
    Write-Host 'MCP package reference is already present. Check that the version is 2.4.0.'
} else {
    dotnet add $project package ModelContextProtocol.AspNetCore --version 2.4.0
    if ($LASTEXITCODE -ne 0) { throw 'dotnet add package failed' }
}
dotnet restore $project
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed' }
Write-Host 'MCP package ready. Run: dotnet build'
