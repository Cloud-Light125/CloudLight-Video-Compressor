[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$solutionPath = Join-Path $projectRoot 'CloudLight.VideoCompressor.sln'
$projectPath = Join-Path $projectRoot 'src\CloudLight.VideoCompressor\CloudLight.VideoCompressor.csproj'
$iconSourcePath = Join-Path $projectRoot 'icon.png'
$iconPath = Join-Path $projectRoot 'icon.ico'
$iconScriptPath = Join-Path $projectRoot 'tools\New-CloudLightIcon.ps1'
$ffmpegSourceDirectory = Join-Path $projectRoot 'third_party\ffmpeg'
$publishDirectory = Join-Path $projectRoot 'artifacts\publish\win-x64'
$installerOutputDirectory = Join-Path $projectRoot 'artifacts\installer'
$installerScriptPath = Join-Path $projectRoot 'installer\CloudLight.VideoCompressor.iss'

function Assert-WorkspaceChildPath {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootPrefix = $projectRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside the project workspace: $fullPath"
    }

    return $fullPath
}

function Invoke-NativeTool {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $($LASTEXITCODE): $FilePath $($Arguments -join ' ')"
    }
}

function Test-IconSizes {
    param([Parameter(Mandatory)][string]$Path)

    $fileStream = [System.IO.File]::OpenRead($Path)
    $reader = [System.IO.BinaryReader]::new($fileStream)
    try {
        if ($reader.ReadUInt16() -ne 0 -or $reader.ReadUInt16() -ne 1) {
            throw "Not a valid ICO file: $Path"
        }

        $count = $reader.ReadUInt16()
        $sizes = [System.Collections.Generic.HashSet[int]]::new()
        for ($index = 0; $index -lt $count; $index++) {
            $width = $reader.ReadByte()
            $height = $reader.ReadByte()
            $null = $reader.ReadByte()
            $null = $reader.ReadByte()
            $null = $reader.ReadUInt16()
            $null = $reader.ReadUInt16()
            $null = $reader.ReadUInt32()
            $null = $reader.ReadUInt32()
            $actualWidth = if ($width -eq 0) { 256 } else { [int]$width }
            $actualHeight = if ($height -eq 0) { 256 } else { [int]$height }
            if ($actualWidth -eq $actualHeight) {
                $null = $sizes.Add($actualWidth)
            }
        }
    }
    finally {
        $reader.Dispose()
        $fileStream.Dispose()
    }

    $requiredSizes = @(16, 24, 32, 48, 64, 128, 256)
    $missingSizes = @($requiredSizes | Where-Object { -not $sizes.Contains($_) })
    if ($missingSizes.Count -gt 0) {
        throw "ICO is missing required icon sizes: $($missingSizes -join ', ')"
    }
}

function Find-InnoSetupCompiler {
    $candidates = [System.Collections.Generic.List[string]]::new()
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($null -ne $command -and -not [string]::IsNullOrWhiteSpace($command.Source)) {
        $candidates.Add($command.Source)
    }

    $programFilesX86 = (Get-Item -Path 'Env:ProgramFiles(x86)' -ErrorAction SilentlyContinue).Value
    foreach ($baseDirectory in @($env:LOCALAPPDATA, $programFilesX86, $env:ProgramFiles)) {
        if (-not [string]::IsNullOrWhiteSpace($baseDirectory)) {
            $candidates.Add((Join-Path $baseDirectory 'Programs\Inno Setup 6\ISCC.exe'))
            $candidates.Add((Join-Path $baseDirectory 'Inno Setup 6\ISCC.exe'))
        }
    }

    $compiler = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($compiler)) {
        throw 'Inno Setup 6 was not found. Install Inno Setup 6, then run this script again.'
    }

    return $compiler
}

foreach ($requiredPath in @($solutionPath, $projectPath, $iconSourcePath, $iconScriptPath, $ffmpegSourceDirectory, $installerScriptPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required release input was not found: $requiredPath"
    }
}

& $iconScriptPath -SourcePath $iconSourcePath -OutputPath $iconPath
Test-IconSizes -Path $iconPath

foreach ($requiredFfmpegFile in @('ffmpeg.exe', 'ffprobe.exe', 'LICENSE.txt')) {
    $path = Join-Path $ffmpegSourceDirectory $requiredFfmpegFile
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Bundled FFmpeg runtime file was not found: $path"
    }
}

Invoke-NativeTool -FilePath (Join-Path $ffmpegSourceDirectory 'ffmpeg.exe') -Arguments @('-version')
Invoke-NativeTool -FilePath (Join-Path $ffmpegSourceDirectory 'ffprobe.exe') -Arguments @('-version')

$versionOutput = & dotnet msbuild $projectPath -nologo '-getProperty:Version'
if ($LASTEXITCODE -ne 0) {
    throw "Unable to read the application version from $projectPath"
}

$appVersion = ($versionOutput | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^\d+(\.\d+){1,3}([-.][0-9A-Za-z.-]+)?$' } | Select-Object -Last 1)
if ([string]::IsNullOrWhiteSpace($appVersion)) {
    throw "Unable to resolve a valid application version from MSBuild output: $($versionOutput -join [Environment]::NewLine)"
}

$publishDirectory = Assert-WorkspaceChildPath -Path $publishDirectory
$installerOutputDirectory = Assert-WorkspaceChildPath -Path $installerOutputDirectory
$expectedInstallerPath = Join-Path $installerOutputDirectory "CloudLight-Video-Compressor-Setup-x64-$appVersion.exe"

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $installerOutputDirectory -Force | Out-Null

if (Test-Path -LiteralPath $expectedInstallerPath -PathType Leaf) {
    Remove-Item -LiteralPath $expectedInstallerPath -Force
}

Invoke-NativeTool -FilePath 'dotnet' -Arguments @('restore', $solutionPath)
Invoke-NativeTool -FilePath 'dotnet' -Arguments @('build', $solutionPath, '-c', 'Release', '--no-restore')

$previousFfmpegTestDirectory = [Environment]::GetEnvironmentVariable('CLOUDLIGHT_FFMPEG_TEST_DIR', 'Process')
try {
    [Environment]::SetEnvironmentVariable('CLOUDLIGHT_FFMPEG_TEST_DIR', $ffmpegSourceDirectory, 'Process')
    Invoke-NativeTool -FilePath 'dotnet' -Arguments @('test', $solutionPath, '-c', 'Release', '--no-build', '--no-restore')
}
finally {
    [Environment]::SetEnvironmentVariable('CLOUDLIGHT_FFMPEG_TEST_DIR', $previousFfmpegTestDirectory, 'Process')
}

Invoke-NativeTool -FilePath 'dotnet' -Arguments @(
    'publish', $projectPath,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '--no-restore',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-o', $publishDirectory)

foreach ($requiredPublishedFile in @(
    'CloudLight.VideoCompressor.exe',
    'icon.ico',
    'ffmpeg\ffmpeg.exe',
    'ffmpeg\ffprobe.exe',
    'ffmpeg\LICENSE.txt')) {
    $path = Join-Path $publishDirectory $requiredPublishedFile
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Publish output is missing required file: $path"
    }
}

$isccPath = Find-InnoSetupCompiler
Invoke-NativeTool -FilePath $isccPath -Arguments @(
    "/DMyAppVersion=$appVersion",
    "/DMyPublishDir=$publishDirectory",
    $installerScriptPath)

if (-not (Test-Path -LiteralPath $expectedInstallerPath -PathType Leaf)) {
    throw "Inno Setup completed but did not produce the expected installer: $expectedInstallerPath"
}

$installer = Get-Item -LiteralPath $expectedInstallerPath
Write-Host ''
Write-Host "Installer generated: $($installer.FullName)"
Write-Host "Installer size: $([Math]::Round($installer.Length / 1MB, 2)) MB"
