param([ValidatePattern('^NetCat-[a-zA-Z0-9][a-zA-Z0-9.-]*$')][string]$BuildName = 'NetCat-1.0.0',[switch]$Archive)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
Push-Location $taskRoot
try {
    if (-not (Test-Path -LiteralPath 'bin/modules.lock.json')) { throw 'Run scripts/Fetch-OfficialModules.ps1 first' }
    if (-not (Test-Path -LiteralPath 'bin/tg-runtime/NetCat.Telegram.exe')) { throw 'Run scripts/Fetch-TelegramHeadless.ps1 first' }
    if (-not (Test-Path -LiteralPath 'bin/geoip/geoip.dat') -or -not (Test-Path -LiteralPath 'bin/geosite/geosite.dat')) { throw 'Run scripts/Fetch-Geodata.ps1 first' }
    dotnet test NetCat.sln -c Release -warnaserror --logger "trx;LogFileName=$BuildName.trx" --results-directory "artifacts/$BuildName-test-results"
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    $taskDestination = Join-Path $taskRoot "artifacts/$BuildName"
    $taskOutput = Join-Path $taskRoot ("artifacts/.build-"+[guid]::NewGuid().ToString('N'))
    $candidateName = if($BuildName -match '(Candidate\d+)$') { $Matches[1] } else { 'Release' }
    dotnet publish src/NetCat.UI/NetCat.UI.csproj -c Release -r win-x64 --self-contained true -o $taskOutput -p:CandidateName=$candidateName -p:DebugType=None -p:DebugSymbols=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    New-Item -ItemType Directory -Force (Join-Path $taskOutput 'modules') | Out-Null
    Copy-Item -Path (Join-Path $taskRoot 'bin/*') -Destination (Join-Path $taskOutput 'modules') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination (Join-Path $taskOutput 'README.md') -Force
    Copy-Item -LiteralPath (Join-Path $taskRoot 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $taskOutput 'THIRD_PARTY_NOTICES.md') -Force
    if (Test-Path -LiteralPath (Join-Path $taskRoot 'licenses')) {
        Copy-Item -Path (Join-Path $taskRoot 'licenses') -Destination (Join-Path $taskOutput 'licenses') -Recurse -Force
    }
    $taskPropsXml = [xml](Get-Content -Raw -LiteralPath (Join-Path $taskRoot 'Directory.Build.props'))
    $taskVer = [string]$taskPropsXml.Project.PropertyGroup.Version
    $taskNote = Join-Path $taskRoot "docs/releases/v$taskVer.md"
    if (Test-Path -LiteralPath $taskNote) {
        Copy-Item -LiteralPath $taskNote -Destination (Join-Path $taskOutput 'RELEASE_NOTES.md') -Force
    }
    & (Join-Path $PSScriptRoot 'Write-PackageManifest.ps1') -Folder $taskOutput
    & (Join-Path $PSScriptRoot 'Assert-ImmutableRelease.ps1') -Candidate $taskOutput -Destination $taskDestination -ReleasesRoot (Join-Path $taskRoot 'artifacts')
    if(Test-Path -LiteralPath $taskDestination) {
        $taskResolved=[IO.Path]::GetFullPath($taskOutput)
        $taskArtifacts=[IO.Path]::GetFullPath((Join-Path $taskRoot 'artifacts'))+[IO.Path]::DirectorySeparatorChar
        if(-not $taskResolved.StartsWith($taskArtifacts,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($taskResolved) -notmatch '^\.build-[a-f0-9]{32}$') { throw 'Unsafe temporary build path.' }
        Remove-Item -LiteralPath $taskResolved -Recurse -Force
    } else { Move-Item -LiteralPath $taskOutput -Destination $taskDestination }
    $taskOutput=$taskDestination
    if ($Archive) {
        $taskVersion=(Get-Content -Raw -LiteralPath (Join-Path $taskOutput 'metadata/release-manifest.json') | ConvertFrom-Json).Version
        $taskArchiveName=if($candidateName -ne 'Release'){"$BuildName.zip"}else{"NetCat-v$taskVersion.zip"}
        & (Join-Path $PSScriptRoot 'Pack-Portable.ps1') -Folder $taskOutput -Archive (Join-Path $taskRoot "artifacts/$taskArchiveName") -TestBuild
    }
    Write-Host "Candidate EXE: $taskOutput\NetCat.exe"
} finally { Pop-Location }
