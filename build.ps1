# =====================================================================
#  GWQuota 构建脚本
#  用 Win10 自带 .NET Framework 4.8 的 csc.exe 直接编译出单文件 exe，
#  不需要 dotnet SDK、不需要运行时、不需要安装程序。
#
#  用法（执行策略受限时用这一行）：
#     powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -SeedKey
#
#  说明：本目录名含中文，csc.exe 处理非 ASCII 路径会失败，
#        因此脚本先把源码暂存到 %TEMP% 的纯 ASCII 路径编译，再拷回 dist\。
# =====================================================================
param(
    [switch]$SeedKey,          # 首次部署：把只读 key 写进数据目录 key.txt
    [switch]$ForceIcon         # 强制重新生成图标
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }

$fw      = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$csc     = Join-Path $fw 'csc.exe'
$dist    = Join-Path $root 'dist'
$icoPath = Join-Path $root 'build\gwquota.ico'
$iconSrc = Join-Path $root 'build\MakeIcon.cs'

if (-not (Test-Path $csc)) { throw "找不到 csc.exe: $csc" }

$stage = Join-Path $env:TEMP 'gwquota-build'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
New-Item -ItemType Directory -Force -Path $dist  | Out-Null

$refs = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll') |
        ForEach-Object { '/reference:' + (Join-Path $fw $_) }

# ---------- 1) 图标 ----------
if ($ForceIcon -or -not (Test-Path $icoPath)) {
    if (Test-Path $iconSrc) {
        Copy-Item $iconSrc (Join-Path $stage 'MakeIcon.cs') -Force
        Push-Location $stage
        try {
            & $csc '/nologo' '/target:exe' '/codepage:65001' '/optimize+' '/out:mkicon.exe' `
                    '/reference:System.dll' '/reference:System.Drawing.dll' 'MakeIcon.cs' | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "图标生成器编译失败" }
            & (Join-Path $stage 'mkicon.exe') (Join-Path $root 'build') | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "图标生成失败" }
        } finally { Pop-Location }
        "图标: 已重新生成 build\gwquota.ico"
    } else {
        "图标: 缺少 build\MakeIcon.cs，跳过（exe 将使用默认图标）"
    }
}

# ---------- 2) 主程序 ----------
Copy-Item (Join-Path $root 'src\GWQuota.cs')      (Join-Path $stage 'GWQuota.cs')      -Force
Copy-Item (Join-Path $root 'src\AssemblyInfo.cs') (Join-Path $stage 'AssemblyInfo.cs') -Force
Copy-Item (Join-Path $root 'build\app.manifest')  (Join-Path $stage 'app.manifest')     -Force

$cscArgs = @(
    '/nologo',
    '/target:winexe',
    '/codepage:65001',
    '/optimize+',
    '/platform:anycpu',
    '/warn:4',
    '/win32manifest:app.manifest',
    '/out:GWQuota.exe'
) + $refs

if (Test-Path $icoPath) {
    Copy-Item $icoPath (Join-Path $stage 'gwquota.ico') -Force
    $cscArgs += '/win32icon:gwquota.ico'
}
$cscArgs += @('GWQuota.cs', 'AssemblyInfo.cs')

Push-Location $stage
try {
    & $csc @cscArgs
    $code = $LASTEXITCODE
} finally { Pop-Location }
if ($code -ne 0) { throw "编译失败，退出码 $code" }

Copy-Item (Join-Path $stage 'GWQuota.exe') (Join-Path $dist 'GWQuota.exe') -Force
$exe = Get-Item (Join-Path $dist 'GWQuota.exe')
$vi  = $exe.VersionInfo
"OK  {0}  {1:N0} 字节  {2:N1}KB" -f $exe.Name, $exe.Length, ($exe.Length / 1KB)
"    版本 {0}  产品 {1}  公司 {2}" -f $vi.FileVersion, $vi.ProductName, $vi.CompanyName
"    时间 {0}" -f $exe.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')

# ---------- 3) 首次部署写 key ----------
if ($SeedKey) {
    $dirs = @(
        (Join-Path $env:APPDATA 'GWQuota'),
        (Join-Path $env:LOCALAPPDATA 'GWQuota'),
        (Join-Path $dist 'data')
    )
    $target = $null
    foreach ($d in $dirs) {
        try {
            New-Item -ItemType Directory -Force -Path $d | Out-Null
            $probe = Join-Path $d '.probe'
            [System.IO.File]::WriteAllText($probe, '1')
            Remove-Item $probe -Force
            $target = $d
            break
        } catch { }
    }
    if (-not $target) { throw "找不到可写的数据目录" }
    $keyFile = Join-Path $target 'key.txt'
    if (Test-Path $keyFile) {
        "key.txt 已存在，未覆盖：$keyFile"
    } else {
        $key = $env:GWQUOTA_KEY
        if (-not $key) { $key = Read-Host -Prompt '粘贴只读 key（只写入本机 key.txt）' }
        if (-not $key) { throw "未提供只读 key" }
        [System.IO.File]::WriteAllText($keyFile, $key + "`r`n", (New-Object System.Text.UTF8Encoding($false)))
        "已写入只读 key：$keyFile"
    }
}
