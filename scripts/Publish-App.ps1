# 以给定运行时标识发布浮层。
#
# 用法：
#   .\scripts\Publish-App.ps1 -RuntimeIdentifier win-x64 -Variant Both
#
# Variant：
#   Standalone  自包含单文件，体积大但无需安装运行时
#   Lite        依赖框架，体积小但需要 .NET 10 Desktop Runtime
#   Both        两者都产出

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier,

    [ValidateSet('Standalone', 'Lite', 'Both')]
    [string]$Variant = 'Both',

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src/CodexTokenMeter.App/CodexTokenMeter.App.csproj'

if (-not (Test-Path $projectPath)) {
    throw "未找到应用项目：$projectPath"
}

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts'
}

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null

$version = ([xml](Get-Content $projectPath)).Project.PropertyGroup.Version |
    Where-Object { $_ } |
    Select-Object -First 1

if (-not $version) {
    $version = '0.0.0'
}

Write-Host "项目：$projectPath"
Write-Host "运行时：$RuntimeIdentifier"
Write-Host "版本：$version"
Write-Host "输出：$OutputDirectory"
Write-Host ''

function Publish-Variant {
    param(
        [string]$Name,
        [bool]$SelfContained,
        [string]$CompressionSetting
    )

    $runId = [guid]::NewGuid().ToString('N')
    $publishDirectory = Join-Path $OutputDirectory "publish-$RuntimeIdentifier-$Name-$runId"
    $archivePath = Join-Path $OutputDirectory "CodexTokenMeter-$RuntimeIdentifier-$Name.zip"
    $temporaryArchive = Join-Path $OutputDirectory "CodexTokenMeter-$RuntimeIdentifier-$Name-$runId.zip"

    Write-Host "── 发布 $Name ──"

    $arguments = @(
        'publish', $projectPath,
        '--configuration', 'Release',
        '--runtime', $RuntimeIdentifier,
        '--self-contained', $SelfContained.ToString().ToLowerInvariant(),
        '--output', $publishDirectory,
        '-p:PublishSingleFile=true',
        "-p:EnableCompressionInSingleFile=$CompressionSetting",
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-p:ContinuousIntegrationBuild=true',
        '-p:TreatWarningsAsErrors=true'
    )

    try {
        & dotnet @arguments

        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish 失败（$Name）"
        }

        Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $temporaryArchive -CompressionLevel Optimal
        Move-Item -LiteralPath $temporaryArchive -Destination $archivePath -Force

        $hash = (Get-FileHash $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $(Split-Path $archivePath -Leaf)" | Set-Content "$archivePath.sha256" -NoNewline

        $size = [Math]::Round((Get-Item $archivePath).Length / 1MB, 2)
        Write-Host "  归档：$(Split-Path $archivePath -Leaf)  ${size} MB"
        Write-Host "  校验：$hash"
        Write-Host ''
    }
    finally {
        $expectedPrefix = $OutputDirectory.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        $absoluteStaging = [System.IO.Path]::GetFullPath($publishDirectory)

        if (-not $absoluteStaging.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "发布临时目录越界，拒绝清理：$absoluteStaging"
        }

        if (Test-Path -LiteralPath $publishDirectory) {
            Remove-Item -LiteralPath $publishDirectory -Recurse -Force
        }

        if (Test-Path -LiteralPath $temporaryArchive) {
            Remove-Item -LiteralPath $temporaryArchive -Force
        }
    }
}

if ($Variant -in @('Standalone', 'Both')) {
    # 自包含：内嵌运行时，用户无需安装任何东西。
    Publish-Variant -Name 'standalone' -SelfContained $true -CompressionSetting 'true'
}

if ($Variant -in @('Lite', 'Both')) {
    # 依赖框架：体积小，需要同架构的 .NET 10 Desktop Runtime。
    Publish-Variant -Name 'lite' -SelfContained $false -CompressionSetting 'false'
}

Write-Host '完成。'
Get-ChildItem $OutputDirectory -Filter '*.zip' | Select-Object Name, @{n = 'MB'; e = { [Math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize
