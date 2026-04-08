[CmdletBinding()]
param(
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Require-Command {
    param([string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        if ($Name -eq "gh") {
            throw "未找到命令: gh。请先安装 GitHub CLI，并执行 gh auth login。"
        }

        throw "未找到命令: $Name"
    }
}

function Invoke-Checked {
    param(
        [string]$FilePath,
        [string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        $joined = $ArgumentList -join ' '
        throw "命令执行失败: $FilePath $joined"
    }
}

function Get-ProjectVersion {
    param([string]$ProjectPath)

    [xml]$xml = Get-Content $ProjectPath
    $value = $xml.Project.PropertyGroup.Version | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "未能从项目文件读取 Version: $ProjectPath"
    }

    return $value.Trim()
}

function Get-GitOutput {
    param([string[]]$ArgumentList)

    $output = & git @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        $joined = $ArgumentList -join ' '
        throw "git 命令执行失败: git $joined"
    }

    return ($output | Out-String).Trim()
}

$repoRoot = $PSScriptRoot
$desktopProject = Join-Path $repoRoot "MinoLink.Desktop\MinoLink.Desktop.csproj"
$installerProject = Join-Path $repoRoot "MinoLink.Installer\MinoLink.Installer.csproj"
$installerOutputDir = Join-Path $repoRoot "MinoLink.Installer\output"

if (-not (Test-Path $desktopProject)) {
    throw "未找到桌面项目: $desktopProject"
}

if (-not (Test-Path $installerProject)) {
    throw "未找到安装包项目: $installerProject"
}

Write-Step "启动发布流程"
Write-Host "仓库目录: $repoRoot" -ForegroundColor DarkGray

Write-Step "检查依赖命令"
Require-Command git
Require-Command dotnet
Require-Command gh

Push-Location $repoRoot
try {
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = Get-ProjectVersion -ProjectPath $desktopProject
    }

    $tag = "v$Version"
    Write-Host "目标版本: $Version" -ForegroundColor DarkGray
    Write-Host "目标标签: $tag" -ForegroundColor DarkGray

    Write-Step "校验工作区"
    $branch = Get-GitOutput -ArgumentList @("branch", "--show-current")
    if ($branch -ne "master") {
        throw "当前分支不是 master，拒绝发布。当前分支: $branch"
    }

    $status = Get-GitOutput -ArgumentList @("status", "--porcelain")
    if (-not [string]::IsNullOrWhiteSpace($status)) {
        throw "工作区不干净，请先提交或清理改动后再发布。"
    }

    $existingLocalTag = Get-GitOutput -ArgumentList @("tag", "--list", $tag)
    if (-not [string]::IsNullOrWhiteSpace($existingLocalTag)) {
        throw "本地 tag 已存在: $tag"
    }

    & gh release view $tag | Out-Null
    if ($LASTEXITCODE -eq 0) {
        throw "GitHub Release 已存在: $tag"
    }

    Write-Step "构建安装包"
    Invoke-Checked -FilePath "dotnet" -ArgumentList @("build", $installerProject, "-c", "Release", "-m:1")

    Write-Step "校验 MSI 输出"
    if (-not (Test-Path $installerOutputDir)) {
        throw "未找到安装包输出目录: $installerOutputDir"
    }

    $msi = Get-ChildItem (Join-Path $installerOutputDir "*.msi") |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($null -eq $msi) {
        throw "未找到 MSI 输出文件。"
    }

    Write-Host "MSI: $($msi.FullName)" -ForegroundColor Green

    Write-Step "推送 master"
    Invoke-Checked -FilePath "git" -ArgumentList @("push", "origin", "master")

    Write-Step "创建并推送 tag $tag"
    Invoke-Checked -FilePath "git" -ArgumentList @("tag", $tag)
    Invoke-Checked -FilePath "git" -ArgumentList @("push", "origin", $tag)

    Write-Step "创建 GitHub Release"
    Invoke-Checked -FilePath "gh" -ArgumentList @("release", "create", $tag, "--title", $tag, "--verify-tag", "--generate-notes")

    Write-Host ""
    Write-Host "发布已创建：$tag" -ForegroundColor Green
    Write-Host "GitHub Actions 将继续执行 release-installer.yml 并自动上传 MSI。" -ForegroundColor Green
}
catch {
    Write-Host ""
    Write-Host "发布失败：$($_.Exception.Message)" -ForegroundColor Red
    throw
}
finally {
    Pop-Location
}
