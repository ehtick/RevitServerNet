<#
.SYNOPSIS
Checks that a package consumer gets the bundled Revit Server client assemblies on build, publish and single-file publish.

.DESCRIPTION
Packs RevitServerNet into a temporary feed and builds a throwaway console app that references the package (net8.0 and net48).
Checks that the six required files are in RSAssemblies\ of:
  - the build output (net8.0, net48),
  - the publish output (net8.0, net48),
  - a self-contained single-file publish (net8.0, win-x64), next to the executable.
Every output is run: it calls ModelExporter.ExportAsync against a closed local port with RevitVersion 2031 (no installed Revit set can
exist for that year), and must fail with "no ModelService endpoint responded", not with "Revit Server client assemblies not found".
A second app declares an empty RevitServerNet_CopyRSAssembliesForConsumers target and must get no RSAssemblies folder.

The package is restored into a private package folder, so the just-packed version never lands in the global NuGet cache. Other packages
come from the global NuGet cache (used as a local feed) or nuget.org. Needs the .NET 8 SDK.

.PARAMETER WorkDir
Folder for the feed, the package folder and the apps (default: a new folder under %TEMP%). Deleted at the end unless -Keep is given.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File scripts\package-smoke.ps1
#>
param(
    [string]$WorkDir = (Join-Path ([System.IO.Path]::GetTempPath()) ("RsnSmoke_" + [guid]::NewGuid().ToString("N"))),
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$requiredFiles = @(
    'Autodesk.RevitServer.Social.dll',
    'RS.Enterprise.Common.ClientServer.DataContract.dll',
    'RS.Enterprise.Common.ClientServer.ServiceContract.Model.dll',
    'RS.Enterprise.Common.ClientServer.Helper.dll',
    'Castle.Core.dll',
    'Castle.Windsor.dll'
)
$failures = New-Object System.Collections.Generic.List[string]

function Invoke-Dotnet([string[]]$Arguments) {
    Write-Host ("> dotnet " + ($Arguments -join ' '))
    & dotnet @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE" }
}

function Test-Assemblies([string]$Label, [string]$Directory) {
    $dir = Join-Path $Directory 'RSAssemblies'
    $missing = @($requiredFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $dir $_)) })
    if ($missing.Count -gt 0) { $failures.Add("${Label}: missing in '$dir': $($missing -join ', ')") }
    else { Write-Host "OK   ${Label}: 6 files in '$dir'" }
}

function Test-Run([string]$Label, [string]$Exe) {
    $output = & $Exe | Out-String
    $code = $LASTEXITCODE
    if ($code -ne 0) { $failures.Add("${Label}: '$Exe' exited with $code`: $($output.Trim())") }
    else { Write-Host "OK   ${Label}: $($output.Trim())" }
}

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
try {
    $feed = Join-Path $WorkDir 'feed'
    Invoke-Dotnet @('pack', (Join-Path $repo 'RevitServerNet.csproj'), '-c', 'Release', '-p:GeneratePackageOnBuild=false', '-o', $feed)
    $package = Get-ChildItem -LiteralPath $feed -Filter 'RevitServerNet.*.nupkg' | Where-Object { $_.Name -notlike '*.snupkg' } | Select-Object -First 1
    if ($package -eq $null) { throw "No package was created in '$feed'." }
    $version = $package.BaseName.Substring('RevitServerNet.'.Length)
    Write-Host "Package: $($package.Name)"

    $globalPackages = ((& dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', '').Trim()
    $nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config>
    <add key="globalPackagesFolder" value="$(Join-Path $WorkDir 'packages')" />
  </config>
  <packageSources>
    <clear />
    <add key="smoke-feed" value="$feed" />
    <add key="global-cache" value="$globalPackages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@
    $program = @'
using System;
using System.IO;
using System.Threading.Tasks;
using RevitServerNet;

static class Program
{
    static async Task<int> Main()
    {
        var options = new ModelExporterOptions
        {
            ServerHost = "127.0.0.1:1",
            ModelPipePath = "|Smoke|Model.rvt",
            DestinationFile = Path.Combine(Path.GetTempPath(), "RsnSmoke_" + Guid.NewGuid().ToString("N") + ".rvt"),
            RevitVersion = "2031",
        };
        try
        {
            await ModelExporter.ExportAsync(options);
            Console.WriteLine("the export succeeded against a closed port");
            return 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.GetType().Name + ": " + ex.Message);
            return ex.Message.Contains("no ModelService endpoint responded") ? 0 : 1;
        }
    }
}
'@
    function New-App([string]$Name, [string]$TargetFrameworks, [string]$Extra) {
        $dir = Join-Path $WorkDir $Name
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Set-Content -LiteralPath (Join-Path $dir 'nuget.config') -Value $nugetConfig -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $dir 'Program.cs') -Value $program -Encoding UTF8
        $project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFrameworks>$TargetFrameworks</TargetFrameworks>
    <AssemblyName>Smoke</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="RevitServerNet" Version="$version" />
  </ItemGroup>
$Extra
</Project>
"@
        Set-Content -LiteralPath (Join-Path $dir "$Name.csproj") -Value $project -Encoding UTF8
        return $dir
    }

    # Consumer with the default copy
    $app = New-App 'App' 'net8.0;net48' ''
    Invoke-Dotnet @('build', $app, '-c', 'Release')
    foreach ($tf in 'net8.0', 'net48') {
        $out = Join-Path $app "bin\Release\$tf"
        Test-Assemblies "build $tf" $out
        Test-Run "run build $tf" (Join-Path $out 'Smoke.exe')
    }
    foreach ($tf in 'net8.0', 'net48') {
        $out = Join-Path $WorkDir "publish-$tf"
        Invoke-Dotnet @('publish', $app, '-c', 'Release', '-f', $tf, '-o', $out)
        Test-Assemblies "publish $tf" $out
        Test-Run "run publish $tf" (Join-Path $out 'Smoke.exe')
    }
    $single = Join-Path $WorkDir 'publish-single'
    Invoke-Dotnet @('publish', $app, '-c', 'Release', '-f', 'net8.0', '-r', 'win-x64', '--self-contained', '-p:PublishSingleFile=true', '-o', $single)
    Test-Assemblies 'single-file publish net8.0' $single
    Test-Run 'run single-file publish net8.0' (Join-Path $single 'Smoke.exe')

    # Consumer that turns the copy off
    $optOut = New-App 'OptOut' 'net8.0' '  <Target Name="RevitServerNet_CopyRSAssembliesForConsumers" />'
    Invoke-Dotnet @('build', $optOut, '-c', 'Release')
    $optOutPublish = Join-Path $WorkDir 'publish-optout'
    Invoke-Dotnet @('publish', $optOut, '-c', 'Release', '-f', 'net8.0', '-o', $optOutPublish)
    foreach ($dir in (Join-Path $optOut 'bin\Release\net8.0'), $optOutPublish) {
        if (Test-Path -LiteralPath (Join-Path $dir 'RSAssemblies')) { $failures.Add("opt-out: '$dir' has an RSAssemblies folder") }
        else { Write-Host "OK   opt-out: no RSAssemblies in '$dir'" }
    }
}
finally {
    if ($Keep) { Write-Host "Kept: $WorkDir" }
    else { Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction Continue }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL $_" }
    exit 1
}
Write-Host 'Package smoke test passed.'
