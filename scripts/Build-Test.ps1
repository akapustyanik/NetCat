param([ValidatePattern('^[a-zA-Z0-9-]+$')][string]$BuildName = 'NetCat-Test-14',[switch]$Archive)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
Push-Location $taskRoot
try {
    if (-not (Test-Path -LiteralPath 'bin/modules.lock.json')) { throw 'Сначала выполните scripts/Fetch-OfficialModules.ps1' }
    if (-not (Test-Path -LiteralPath 'bin/tg-runtime/NetCat.Telegram.exe')) { throw 'Сначала выполните scripts/Fetch-TelegramHeadless.ps1' }
    if (-not (Test-Path -LiteralPath 'bin/geoip/geoip.dat') -or -not (Test-Path -LiteralPath 'bin/geosite/geosite.dat')) { throw 'Сначала выполните scripts/Fetch-Geodata.ps1' }
    dotnet test NetCat.sln
    if ($LASTEXITCODE -ne 0) { throw 'Тесты не прошли.' }
    $taskDestination = Join-Path $taskRoot "artifacts/$BuildName"
    $taskOutput = Join-Path $taskRoot ("artifacts/.build-"+[guid]::NewGuid().ToString('N'))
    dotnet publish src/NetCat.UI/NetCat.UI.csproj -c Release -r win-x64 --self-contained true -o $taskOutput -p:DebugType=None -p:DebugSymbols=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
    if ($LASTEXITCODE -ne 0) { throw 'Сборка не выполнена.' }
    New-Item -ItemType Directory -Force (Join-Path $taskOutput 'modules') | Out-Null
    Copy-Item -Path (Join-Path $taskRoot 'bin/*') -Destination (Join-Path $taskOutput 'modules') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination (Join-Path $taskOutput 'README.md') -Force
    New-Item -ItemType Directory -Force (Join-Path $taskOutput 'docs') | Out-Null
    Copy-Item -Path (Join-Path $taskRoot 'docs/*.md') -Destination (Join-Path $taskOutput 'docs') -Force
    & (Join-Path $PSScriptRoot 'Write-PackageManifest.ps1') -Folder $taskOutput
    & (Join-Path $PSScriptRoot 'Assert-ImmutableRelease.ps1') -Candidate $taskOutput -Destination $taskDestination -ReleasesRoot (Join-Path $taskRoot 'artifacts')
    if(Test-Path -LiteralPath $taskDestination) {
        $taskResolved=[IO.Path]::GetFullPath($taskOutput)
        $taskArtifacts=[IO.Path]::GetFullPath((Join-Path $taskRoot 'artifacts'))+[IO.Path]::DirectorySeparatorChar
        if(-not $taskResolved.StartsWith($taskArtifacts,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($taskResolved) -notmatch '^\.build-[a-f0-9]{32}$') { throw 'Unsafe temporary build path.' }
        Remove-Item -LiteralPath $taskResolved -Recurse -Force
    } else { Move-Item -LiteralPath $taskOutput -Destination $taskDestination }
    $taskOutput=$taskDestination
    if ($Archive) { $taskVersion=(Get-Content -Raw -LiteralPath (Join-Path $taskOutput 'metadata/release-manifest.json') | ConvertFrom-Json).Version; & (Join-Path $PSScriptRoot 'Pack-Portable.ps1') -Folder $taskOutput -Archive (Join-Path $taskRoot ("artifacts/NetCat-v$taskVersion.zip")) -TestBuild }
    Write-Host "Тестовый EXE: $taskOutput\NetCat.exe"
} finally { Pop-Location }
