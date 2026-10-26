$ErrorActionPreference = 'Stop'
$prepareOnly = ($args -contains '--prepare')
$skipBuild = ($args -contains '--no-build')
$hostArguments = @($args | Where-Object { $_ -ne '--prepare' -and $_ -ne '--no-build' })
# Windows PowerShell 5.1 downloads far slower while drawing the progress bar.
$ProgressPreference = 'SilentlyContinue'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location $projectRoot

[xml]$project = Get-Content ShiroBot.AiChatPlugin.csproj -Raw
$sdkVersion = ($project.Project.ItemGroup.PackageReference |
    Where-Object { $_.Include -eq 'ShiroBot.SDK' } | Select-Object -First 1).Version
if (-not $sdkVersion) { throw 'Cannot read ShiroBot.SDK version from ShiroBot.AiChatPlugin.csproj.' }

$architecture = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
# Windows PowerShell 5.1 has no $IsWindows/$IsMacOS/$IsLinux and only runs on Windows.
$onWindows = $PSVersionTable.PSEdition -eq 'Desktop' -or $IsWindows
$platform = if ($onWindows) { 'win' }
    elseif ($IsMacOS) { 'osx' }
    elseif ($IsLinux) { if (Test-Path '/etc/alpine-release') { 'linux-musl' } else { 'linux' } }
    else { throw 'Unsupported operating system.' }
$rid = "$platform-$architecture"
# The host directory is not versioned: config.toml, plugins/ and adapters/ survive SDK upgrades.
$cacheDir = Join-Path $projectRoot ".shirobot-dev/host/$rid"
$hostExe = Join-Path $cacheDir $(if ($onWindows) { 'ShiroBot.exe' } else { 'ShiroBot' })
$versionFile = Join-Path $cacheDir '.host-version'
$archiveName = "shirobot-host-$rid-framework-dependent.zip"
$archive = Join-Path $cacheDir $archiveName

if (-not $skipBuild) {
    dotnet build ShiroBot.AiChatPlugin.csproj -c Debug -p:ShiroBotPluginPackagingEnabled=false -p:ShiroBotDevelopmentHostEnabled=false
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$installedVersion = if (Test-Path $versionFile) { "$(Get-Content $versionFile -Raw)".Trim() } else { '' }
if (-not (Test-Path $hostExe) -or $installedVersion -ne $sdkVersion) {
    New-Item -ItemType Directory -Force $cacheDir | Out-Null
    $url = "https://github.com/ShirokaProject/ShiroBot/releases/download/v$sdkVersion/$archiveName"
    Write-Host "Downloading ShiroBot v$sdkVersion for $rid..."
    Invoke-WebRequest -Uri $url -OutFile $archive
    # The archive only contains the host executable, so the existing local data is kept.
    Expand-Archive -Path $archive -DestinationPath $cacheDir -Force
    Remove-Item $archive
    # Expand-Archive does not keep the Unix executable bit.
    if (-not $onWindows) { chmod +x $hostExe }
    Set-Content -Path $versionFile -Value $sdkVersion -Encoding ASCII
}

$pluginDir = Join-Path $cacheDir 'plugins/AiChatPlugin'
$buildDir = (Resolve-Path 'bin/Debug/net10.0').Path
$manifest = Join-Path $pluginDir '.shirobot-dev-files'
New-Item -ItemType Directory -Force $pluginDir | Out-Null
# Remove files copied by the previous run that the build no longer produces. Files the
# plugin or host created there (config.toml, data, .shirobot/native, ...) are never touched.
if (Test-Path -LiteralPath $manifest) {
    foreach ($file in Get-Content -LiteralPath $manifest) {
        if (-not $file -or $file.Contains('..')) { continue }
        if (-not (Test-Path -LiteralPath (Join-Path $buildDir $file))) {
            $stale = Join-Path $pluginDir $file
            if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale -Force }
        }
    }
}
Copy-Item "$buildDir/*" $pluginDir -Recurse -Force
# config.toml is never listed, so a user-edited config is not deleted if the build stops emitting one.
Get-ChildItem -LiteralPath $buildDir -Recurse -File | Where-Object { $_.Name -ne 'config.toml' } |
    ForEach-Object { $_.FullName.Substring($buildDir.Length).TrimStart('\', '/') -replace '\\', '/' } |
    Set-Content -LiteralPath $manifest -Encoding UTF8

$configPath = Join-Path $cacheDir 'config.toml'
if (-not (Test-Path $configPath)) {
    Set-Content -Path $configPath -Value "protocols = []`n`n[api]`nenable = true`nlisten_urls = [`"http://127.0.0.1:7002`"]" -Encoding UTF8
}

if ($prepareOnly) {
    Write-Host "ShiroBot v$sdkVersion is ready. Use the Rider Run button to start it."
    exit 0
}

Write-Host "Starting ShiroBot v$sdkVersion with ShiroBot.AiChatPlugin..."
Push-Location $cacheDir
try {
    & $hostExe @hostArguments
    exit $LASTEXITCODE
} finally {
    Pop-Location
}
