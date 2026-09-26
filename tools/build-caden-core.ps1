param([Parameter(Mandatory=$true)][string]$UnityEditorData)
$ErrorActionPreference = 'Stop'
# Stored in tools/. Uses Unity's existing compiler/reference pack and Newtonsoft package.
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'cad-vision-game'
$destination = Join-Path $project 'Assets/Plugins/CADEN'
$compiler = Get-ChildItem (Join-Path $UnityEditorData 'DotNetSdk/sdk') -Filter csc.dll -Recurse | Select-Object -First 1
$json = Get-ChildItem (Join-Path $project 'Library/PackageCache') -Filter Newtonsoft.Json.dll -Recurse | Select-Object -First 1
if (!$compiler -or !$json) { throw 'Open the Unity project first to resolve its packages.' }
$scratch = Join-Path $project 'Temp/CADENCoreBuild'
New-Item -ItemType Directory -Path $scratch, $destination -Force | Out-Null
$response = Join-Path $scratch 'core.rsp'
$output = Join-Path $scratch 'CADEN.Core.dll'
$options = @('/nologo', '/target:library', '/langversion:9', '/nullable:enable', '/nostdlib+', '/deterministic+', ('/out:"' + $output + '"'))
$options += Get-ChildItem (Join-Path $UnityEditorData 'NetStandard/ref/2.1.0') -Filter '*.dll' | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$options += '/reference:"' + $json.FullName + '"'
# Isolate the Unity copy from Oculus.Platform.Core; upstream CADEN stays unchanged.
$sourceRoot = Join-Path $repo 'CADEN/Core'
$sources = Get-ChildItem $sourceRoot -Filter '*.cs' -Recurse | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
foreach ($source in $sources) {
    $target = Join-Path $scratch ('Sources/' + $source.FullName.Substring($sourceRoot.Length + 1))
    New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
    $text = [IO.File]::ReadAllText($source.FullName)
    $text = [regex]::Replace($text, '\bCore\.', 'CADEN.Core.')
    $text = [regex]::Replace($text, '\b(namespace|using) Core\b', '$1 CADEN.Core')
    [IO.File]::WriteAllText($target, $text)
    $options += '"' + $target + '"'
}
$options | Set-Content $response
& (Join-Path $UnityEditorData 'NetCoreRuntime/dotnet.exe') $compiler.FullName "@$response"
if ($LASTEXITCODE -ne 0) { throw 'CADEN Core compilation failed; existing assembly was preserved.' }
Copy-Item -LiteralPath $output -Destination (Join-Path $destination 'CADEN.Core.dll')
# Keep the local config reader Editor-only; never import Desktop/Program.cs or its .NET 10 host.
$configReader = '#nullable enable' + [Environment]::NewLine + [IO.File]::ReadAllText((Join-Path $repo 'CADEN/Desktop/LocalConfiguration.cs'))
[IO.File]::WriteAllText((Join-Path $project 'Assets/Editor/CadenLocalConfiguration.cs'), $configReader.Replace('using Core;', 'using CADEN.Core;'))
Write-Output 'CADEN.Core refreshed. Return to Unity and allow script compilation.'
