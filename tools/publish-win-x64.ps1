# Windows self-contained publish; keeps trimming OFF to preserve reflection-based tools and dependencies.
$ErrorActionPreference = 'Stop'
$projects = @(Get-ChildItem -Path . -Filter '*.csproj' -File)
if ($projects.Count -ne 1) { throw 'Run from project root with exactly one .csproj' }
$project = $projects[0].FullName
$out = Join-Path (Get-Location) 'publish-win-x64'
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:PublishTrimmed=false -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
# Ensure user-authored skill files travel with the executable.
$skills = Join-Path (Get-Location) 'skills'
if (Test-Path $skills) {
    $dest = Join-Path $out 'skills'
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    Copy-Item -Path (Join-Path $skills '*.json') -Destination $dest -Force
}
Write-Host "Published: $out"
