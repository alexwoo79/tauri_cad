param(
    [ValidateSet('help', 'doctor', 'install-nuget', 'install-prereq', 'check-msbuild', 'check-nuget', 'check-net48-targeting-pack', 'check-acad-managed', 'resolve-nuget-cmd', 'restore', 'build', 'build-debug', 'build-release', 'clean', 'rebuild', 'copy-deploy', 'deploy-only', 'deploy', 'package-bundle', 'install-bundle', 'dev', 'launch')]
    [string]$Mode = 'dev',
    [string]$Configuration = 'Debug',
    [string]$Platform = 'AnyCPU',
    [string]$PackagesDir = 'packages',
    [string]$DeployDir = '',
    [string]$AutoCADManagedDir = '',
    [string]$AutoCADExePath = '',
    [string]$ProjectFile = 'SunlightPlugin.csproj',
    [string]$PackagesConfig = 'packages.config',
    [string]$AssemblyName = 'SunlightPlugin'
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectDir = Split-Path -Parent $scriptDir
$projectFilePath = Join-Path $projectDir $ProjectFile
$packagesConfigPath = Join-Path $projectDir $PackagesConfig
$packagesDirPath = if ([System.IO.Path]::IsPathRooted($PackagesDir)) { $PackagesDir } else { Join-Path $projectDir $PackagesDir }

if ([string]::IsNullOrWhiteSpace($DeployDir)) { $DeployDir = $env:DEPLOY_DIR }
if ([string]::IsNullOrWhiteSpace($AutoCADManagedDir)) { $AutoCADManagedDir = $env:ACAD_MANAGED_DIR }
if ([string]::IsNullOrWhiteSpace($AutoCADExePath)) { $AutoCADExePath = $env:ACAD_EXE_PATH }

Set-Location $projectDir

function Write-Help {
    Write-Host 'SunlightPlugin Windows tasks'
    Write-Host ''
    Write-Host 'Usage:'
    Write-Host '  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/sunlight_dev.ps1 <mode>'
    Write-Host ''
    Write-Host 'Modes:'
    Write-Host '  help'
    Write-Host '  doctor'
    Write-Host '  install-nuget'
    Write-Host '  install-prereq'
    Write-Host '  check-msbuild'
    Write-Host '  check-nuget'
    Write-Host '  check-net48-targeting-pack'
    Write-Host '  check-acad-managed'
    Write-Host '  resolve-nuget-cmd'
    Write-Host '  restore'
    Write-Host '  build'
    Write-Host '  build-debug'
    Write-Host '  build-release'
    Write-Host '  clean'
    Write-Host '  rebuild'
    Write-Host '  copy-deploy'
    Write-Host '  deploy-only'
    Write-Host '  deploy'
    Write-Host '  package-bundle'
    Write-Host '  install-bundle'
    Write-Host '  dev'
    Write-Host '  launch'
    Write-Host ''
    Write-Host 'Options:'
    Write-Host '  -Configuration Debug|Release'
    Write-Host '  -Platform AnyCPU'
    Write-Host '  -PackagesDir packages'
    Write-Host '  -DeployDir path'
    Write-Host '  -AutoCADManagedDir path'
    Write-Host '  -AutoCADExePath path'
}

function Get-OutputDirPath {
    return Join-Path $projectDir (Join-Path 'bin' $Configuration)
}

function Get-ProgramFilesX86 {
    [Environment]::GetFolderPath('ProgramFilesX86')
}

function Get-ShortcutTargetPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ShortcutPath
    )

    if (-not (Test-Path $ShortcutPath)) {
        return $null
    }

    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        if ($shortcut.TargetPath) {
            return $shortcut.TargetPath
        }
    }
    catch {
        return $null
    }

    return $null
}

function Get-AutoCADExeFromStartMenu {
    $startMenuRoots = @(
        (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'),
        (Join-Path $env:AppData 'Microsoft\Windows\Start Menu\Programs')
    )

    foreach ($root in $startMenuRoots) {
        if ([string]::IsNullOrWhiteSpace($root) -or -not (Test-Path $root)) {
            continue
        }

        $shortcuts = Get-ChildItem -Path $root -Filter '*.lnk' -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match 'AutoCAD' }

        foreach ($shortcut in $shortcuts) {
            $targetPath = Get-ShortcutTargetPath -ShortcutPath $shortcut.FullName
            if (-not [string]::IsNullOrWhiteSpace($targetPath) -and (Test-Path $targetPath) -and ($targetPath -match '\\acad\.exe$')) {
                return $targetPath
            }
        }
    }

    return $null
}

function Get-AcadInstallDirFromExePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExePath
    )

    if ([string]::IsNullOrWhiteSpace($ExePath) -or -not (Test-Path $ExePath)) {
        return $null
    }

    return Split-Path -Parent $ExePath
}

function Test-PackageCache {
    $requiredPackages = @(
        'Costura.Fody.6.2.0',
        'Fody.6.9.3',
        'ILGPU.1.5.3',
        'System.Buffers.4.5.1',
        'System.Collections.Immutable.7.0.0',
        'System.Memory.4.5.5',
        'System.Numerics.Vectors.4.5.0',
        'System.Reflection.Metadata.7.0.2',
        'System.Runtime.CompilerServices.Unsafe.6.0.0'
    )

    foreach ($package in $requiredPackages) {
        if (-not (Test-Path (Join-Path $packagesDirPath $package))) {
            return $false
        }
    }

    return $true
}

function Get-NuGetCommand {
    $command = Get-Command nuget.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }

    foreach ($candidate in @('C:\ProgramData\chocolatey\bin\nuget.exe', 'C:\Windows\System32\nuget.exe')) {
        if (Test-Path $candidate) { return $candidate }
    }

    return $null
}

function Get-MSBuildCommand {
    $msbuild = Get-Command msbuild -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($msbuild) { return @($msbuild.Source) }

    $programFilesX86 = Get-ProgramFilesX86
    $vswhere = $null
    if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
        $vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
    }

    if (-not [string]::IsNullOrWhiteSpace($vswhere) -and (Test-Path $vswhere)) {
        $resolved = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null
        if ($resolved) {
            $first = ($resolved | Select-Object -First 1)
            if (-not [string]::IsNullOrWhiteSpace($first) -and (Test-Path $first)) {
                return @($first)
            }
        }
    }

    foreach ($candidate in @(
            'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe',
            'C:\Program Files (x86)\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe',
            'C:\Program Files (x86)\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe',
            'C:\Program Files (x86)\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe'
        )) {
        if (Test-Path $candidate) {
            return @($candidate)
        }
    }

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($dotnet) { return @($dotnet.Source, 'msbuild') }

    return $null
}

function Get-AcadManagedDir {
    if (-not [string]::IsNullOrWhiteSpace($AutoCADManagedDir) -and (Test-Path (Join-Path $AutoCADManagedDir 'acmgd.dll'))) {
        return $AutoCADManagedDir
    }

    $autoCadExe = Get-AcadExePath
    if (-not [string]::IsNullOrWhiteSpace($autoCadExe)) {
        $installDir = Get-AcadInstallDirFromExePath -ExePath $autoCadExe
        if (-not [string]::IsNullOrWhiteSpace($installDir) -and (Test-Path (Join-Path $installDir 'acmgd.dll'))) {
            return $installDir
        }
    }

    foreach ($version in @('2026', '2025', '2024', '2023', '2022', '2021', '2020')) {
        $candidate = Join-Path $env:ProgramFiles "Autodesk\AutoCAD $version"
        if (Test-Path (Join-Path $candidate 'acmgd.dll')) { return $candidate }
    }

    $programFilesX86 = Get-ProgramFilesX86
    if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
        foreach ($version in @('2026', '2025', '2024', '2023', '2022', '2021', '2020')) {
            $candidate = Join-Path $programFilesX86 "Autodesk\AutoCAD $version"
            if (Test-Path (Join-Path $candidate 'acmgd.dll')) { return $candidate }
        }
    }

    return $null
}

function Get-AcadExePath {
    if (-not [string]::IsNullOrWhiteSpace($AutoCADExePath) -and (Test-Path $AutoCADExePath)) {
        return $AutoCADExePath
    }

    $shortcutExe = Get-AutoCADExeFromStartMenu
    if (-not [string]::IsNullOrWhiteSpace($shortcutExe)) {
        return $shortcutExe
    }

    foreach ($version in @('2026', '2025', '2024', '2023', '2022', '2021', '2020')) {
        $candidate = Join-Path $env:ProgramFiles "Autodesk\AutoCAD $version\acad.exe"
        if (Test-Path $candidate) { return $candidate }
    }

    $programFilesX86 = Get-ProgramFilesX86
    if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
        foreach ($version in @('2026', '2025', '2024', '2023', '2022', '2021', '2020')) {
            $candidate = Join-Path $programFilesX86 "Autodesk\AutoCAD $version\acad.exe"
            if (Test-Path $candidate) { return $candidate }
        }
    }

    return $null
}

function Invoke-ResolveNuGetCmd {
    $nuget = Get-NuGetCommand
    if (-not $nuget) { throw 'NuGet CLI not found. Install nuget.exe or keep the vendored packages directory intact.' }
    Write-Output $nuget
}

function Invoke-CheckMsBuild {
    if (-not (Get-MSBuildCommand)) { throw 'MSBuild or dotnet was not found.' }
    Write-Host 'info: MSBuild check passed.'
}

function Invoke-CheckNuGet {
    if (-not (Get-NuGetCommand)) { throw 'NuGet CLI not found.' }
    Write-Host 'info: NuGet check passed.'
}

function Invoke-CheckNet48TargetingPack {
    $targetingPack = Join-Path (Get-ProgramFilesX86) 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
    if (-not (Test-Path $targetingPack)) { throw '.NET Framework 4.8 Developer Pack / Targeting Pack not found.' }
    Write-Host "info: .NET Framework 4.8 Targeting Pack found: $targetingPack"
}

function Invoke-CheckAcadManaged {
    $managedDir = Get-AcadManagedDir
    if (-not $managedDir) { throw 'AutoCAD managed assemblies not found. Set ACAD_MANAGED_DIR or install AutoCAD 2020-2026.' }
    Write-Host "info: ACAD_MANAGED_DIR = $managedDir"
}

function Invoke-InstallNuGet {
    $nuget = Get-NuGetCommand
    if ($nuget) {
        Write-Host "info: nuget already installed: $nuget"
        return
    }

    $winget = Get-Command winget -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($winget) {
        & $winget.Source install --id NuGet.NuGet.CommandLine -e --accept-source-agreements --accept-package-agreements
        return
    }

    $choco = Get-Command choco -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($choco) {
        & $choco.Source install nuget.commandline -y
        return
    }

    throw 'No supported package manager found (winget/choco).'
}

function Invoke-InstallPrereq {
    Invoke-CheckMsBuild
    Invoke-InstallNuGet
    Invoke-CheckNet48TargetingPack
    Invoke-CheckAcadManaged
    Write-Host "info: prerequisite checks complete."
}

function Invoke-Doctor {
    $nuget = Get-NuGetCommand
    $msbuild = @(Get-MSBuildCommand)
    $managedDir = Get-AcadManagedDir
    $acadExe = Get-AcadExePath

    Write-Host "info: project = $ProjectFile"
    Write-Host "info: solution = SunlightPlugin.slnx"
    Write-Host "info: configuration = $Configuration"
    Write-Host "info: platform = $Platform"
    Write-Host "info: packages_dir = $packagesDirPath"
    Write-Host "info: msbuild = $([string]::Join(' ', $msbuild))"
    Write-Host "info: nuget = $nuget"
    Write-Host "info: ACAD_MANAGED_DIR = $managedDir"
    Write-Host "info: ACAD_EXE_PATH = $acadExe"
    Write-Host "info: deploy_dir = $DeployDir"
}

function Invoke-Restore {
    if (Test-PackageCache) {
        Write-Host "info: packages already present, skipping restore."
        return
    }

    if (-not (Test-Path $packagesConfigPath)) { throw "missing packages.config: $packagesConfigPath" }

    $nuget = Get-NuGetCommand
    if (-not $nuget) { throw 'NuGet CLI not found. Install nuget.exe or keep the vendored packages directory intact.' }

    & $nuget restore $packagesConfigPath -PackagesDirectory $packagesDirPath -SolutionDirectory $projectDir
}

function Write-AutoloadLsp {
    $outputDirPath = Get-OutputDirPath
    New-Item -ItemType Directory -Force -Path $outputDirPath | Out-Null
    $dllPath = Join-Path $outputDirPath "$AssemblyName.dll"
    $dllPathLsp = $dllPath.Replace('\', '/')
    $lspPath = Join-Path $outputDirPath 'AUTOLOADSUN.lsp'

    $content = @(
        '(setvar "FILEDIA" 0)',
        ('(command "_.NETLOAD" "{0}")' -f $dllPathLsp),
        '(setvar "FILEDIA" 1)',
        '(princ "\n[SUN] Plugin loaded. Type SUN to open panel.")',
        '(princ)'
    )

    Set-Content -Path $lspPath -Value $content -Encoding ASCII
    Write-Host "info: regenerated $lspPath"
}

function Test-FileLocked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path $Path)) { return $false }

    $stream = $null
    try {
        $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        return $false
    }
    catch {
        return $true
    }
    finally {
        if ($stream) { $stream.Dispose() }
    }
}

function Assert-BuildOutputFresh {
    $outputDirPath = Get-OutputDirPath
    $binDll = Join-Path $outputDirPath "$AssemblyName.dll"
    $objDll = Join-Path (Join-Path $projectDir 'obj') (Join-Path $Configuration "$AssemblyName.dll")

    if (-not (Test-Path $binDll)) {
        throw "Build output missing: $binDll"
    }

    if (Test-Path $objDll) {
        $binTime = (Get-Item $binDll).LastWriteTime
        $objTime = (Get-Item $objDll).LastWriteTime
        if ($binTime -lt $objTime) {
            throw "Stale output detected: $binDll ($binTime) is older than $objDll ($objTime). Close AutoCAD and rebuild."
        }
    }
}

function Invoke-Build {
    $msbuild = @(Get-MSBuildCommand)
    if (-not $msbuild) { throw 'MSBuild or dotnet was not found.' }

    $managedDir = Get-AcadManagedDir
    if (-not $managedDir) { throw 'AutoCAD managed assemblies not found. Set ACAD_MANAGED_DIR or install AutoCAD 2020-2026.' }

    $outputDirPath = Get-OutputDirPath
    $binDll = Join-Path $outputDirPath "$AssemblyName.dll"
    if (Test-FileLocked -Path $binDll) {
        throw "Output DLL is locked: $binDll . Close AutoCAD (or unload plugin) before build to avoid stale binaries."
    }

    $buildArgs = @($projectFilePath, '/t:Build', "/p:Configuration=$Configuration", "/p:Platform=$Platform", "/p:AutoCADManagedDir=$managedDir")
    if ($msbuild.Count -eq 1) {
        & $msbuild[0] @buildArgs
    }
    else {
        & $msbuild[0] $msbuild[1] @buildArgs
    }

    Assert-BuildOutputFresh
    Write-AutoloadLsp
}

function Invoke-Clean {
    $msbuild = @(Get-MSBuildCommand)
    if ($msbuild) {
        $cleanArgs = @($projectFilePath, '/t:Clean', "/p:Configuration=$Configuration", "/p:Platform=$Platform")
        if ($msbuild.Count -eq 1) {
            & $msbuild[0] @cleanArgs
        }
        else {
            & $msbuild[0] $msbuild[1] @cleanArgs
        }
    }

    foreach ($path in @((Join-Path $projectDir 'bin'), (Join-Path $projectDir 'obj'))) {
        if (Test-Path $path) { Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

function Invoke-Rebuild {
    Invoke-Clean
    Invoke-Restore
    Invoke-Build
}

function Invoke-CopyDeploy {
    if ([string]::IsNullOrWhiteSpace($DeployDir)) { throw 'DEPLOY_DIR not set.' }

    Invoke-Build

    $outputDirPath = Get-OutputDirPath

    $copyItems = @(
        "$AssemblyName.dll",
        "$AssemblyName.dll.config",
        "$AssemblyName.pdb",
        'AUTOLOADSUN.lsp'
    )

    New-Item -ItemType Directory -Force -Path $DeployDir | Out-Null
    foreach ($item in $copyItems) {
        $source = Join-Path $outputDirPath $item
        if (Test-Path $source) { Copy-Item $source $DeployDir -Force }
    }

    Write-Host "[info] 已部署到: $DeployDir"
    Write-Host "info: deployed to $DeployDir"
}

function Invoke-DeployOnly {
    if ([string]::IsNullOrWhiteSpace($DeployDir)) { throw 'DEPLOY_DIR not set.' }

    $outputDirPath = Get-OutputDirPath

    $copyItems = @(
        "$AssemblyName.dll",
        "$AssemblyName.dll.config",
        "$AssemblyName.pdb",
        'AUTOLOADSUN.lsp'
    )

    New-Item -ItemType Directory -Force -Path $DeployDir | Out-Null
    foreach ($item in $copyItems) {
        $source = Join-Path $outputDirPath $item
        if (Test-Path $source) { Copy-Item $source $DeployDir -Force }
    }

    Write-Host "[info] 已从现有产物部署到: $DeployDir"
    Write-Host "info: deployed from existing artifacts to $DeployDir"
}

function Get-BundleRootPath {
    return Join-Path (Join-Path $projectDir 'dist') "$AssemblyName.bundle"
}

function Write-PackageContentsXml {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BundleRoot
    )

    $xmlPath = Join-Path $BundleRoot 'PackageContents.xml'
    $xml = @(
        '<?xml version="1.0" encoding="utf-8"?>',
        '<ApplicationPackage SchemaVersion="1.0" AutodeskProduct="AutoCAD" Name="SunlightPlugin" Description="SunlightPlugin for AutoCAD" AppVersion="1.0.0" ProductCode="{D0E5F7A5-9B4B-4D9B-9A52-7B5B9A3E9C11}">',
        '  <CompanyDetails Name="SunlightPlugin" />',
        '  <Components>',
        '    <ComponentEntry AppName="SunlightPlugin" Version="1.0.0" ModuleName="./Contents/Win64/SunlightPlugin.dll" AppDescription="SunlightPlugin" LoadOnAutoCADStartup="True">',
        '      <Commands GroupName="SUNLIGHT_CMDS">',
        '        <Command Global="SUN" Local="SUN" />',
        '      </Commands>',
        '    </ComponentEntry>',
        '  </Components>',
        '</ApplicationPackage>'
    )

    Set-Content -Path $xmlPath -Value $xml -Encoding UTF8
}

function Invoke-PackageBundle {
    Invoke-Build

    $outputDirPath = Get-OutputDirPath
    $bundleRoot = Get-BundleRootPath
    $bundleWin64 = Join-Path $bundleRoot 'Contents\Win64'

    if (Test-Path $bundleRoot) {
        Remove-Item -Path $bundleRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $bundleWin64 | Out-Null

    $copyItems = @(
        "$AssemblyName.dll",
        "$AssemblyName.dll.config"
    )

    foreach ($item in $copyItems) {
        $source = Join-Path $outputDirPath $item
        if (Test-Path $source) {
            Copy-Item -Path $source -Destination $bundleWin64 -Force
        }
    }

    Write-PackageContentsXml -BundleRoot $bundleRoot

    $zipPath = Join-Path (Join-Path $projectDir 'dist') ("{0}-{1}.zip" -f $AssemblyName, $Configuration.ToLowerInvariant())
    if (Test-Path $zipPath) {
        Remove-Item -Path $zipPath -Force
    }
    Compress-Archive -Path $bundleRoot -DestinationPath $zipPath -Force

    Write-Host "[info] Bundle目录: $bundleRoot"
    Write-Host "[info] Bundle压缩包: $zipPath"
}

function Invoke-InstallBundle {
    Invoke-PackageBundle

    $bundleRoot = Get-BundleRootPath
    $pluginsDir = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins'
    $targetBundle = Join-Path $pluginsDir "$AssemblyName.bundle"

    New-Item -ItemType Directory -Force -Path $pluginsDir | Out-Null
    if (Test-Path $targetBundle) {
        Remove-Item -Path $targetBundle -Recurse -Force
    }

    Copy-Item -Path $bundleRoot -Destination $targetBundle -Recurse -Force
    Write-Host "[info] 已安装Bundle: $targetBundle"
    Write-Host "[info] 请重启 AutoCAD 使自动加载生效。"
}

function Invoke-Launch {
    $acadExe = Get-AcadExePath
    if (-not $acadExe) { throw 'AutoCAD exe not found. Set ACAD_EXE_PATH or install AutoCAD 2020-2026.' }

    Start-Process $acadExe
}

function Invoke-Dev {
    Invoke-Restore
    Invoke-Build
    Invoke-Launch
}

switch ($Mode) {
    "help" { Write-Help }
    "doctor" { Invoke-Doctor }
    "install-nuget" { Invoke-InstallNuGet }
    "install-prereq" { Invoke-InstallPrereq }
    "check-msbuild" { Invoke-CheckMsBuild }
    "check-nuget" { Invoke-CheckNuGet }
    "check-net48-targeting-pack" { Invoke-CheckNet48TargetingPack }
    "check-acad-managed" { Invoke-CheckAcadManaged }
    "resolve-nuget-cmd" { Invoke-ResolveNuGetCmd }
    "restore" { Invoke-Restore }
    "build" { Invoke-Build }
    "build-debug" { $Configuration = 'Debug'; Invoke-Build }
    "build-release" { $Configuration = 'Release'; Invoke-Build }
    "clean" { Invoke-Clean }
    "rebuild" { Invoke-Rebuild }
    "copy-deploy" { Invoke-CopyDeploy }
    "deploy-only" { Invoke-DeployOnly }
    "deploy" { Invoke-CopyDeploy }
    "package-bundle" { Invoke-PackageBundle }
    "install-bundle" { Invoke-InstallBundle }
    "dev" { Invoke-Dev }
    "launch" { Invoke-Launch }
}