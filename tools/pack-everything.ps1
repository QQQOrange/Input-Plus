<#
.SYNOPSIS
    把内置 Everything 的三份二进制压成 .gz，供程序集内嵌。

.DESCRIPTION
    为什么要压：这三份东西是**真正的 PE 可执行体**（Everything.exe / Everything64.dll /
    Everything32.dll），直接以 EmbeddedResource 塞进 InputPlus.dll 时，程序集里就躺着
    三个完整的 PE 头。火绒（以及多数国产引擎的"通用 .NET"规则）对这种"托管程序里夹带
    可执行体"的形状极其敏感 —— 2026-11-23 就被判成 Trojan/Bladabindi.c 直接删文件。

    压成 gz 后程序集里不再有 MZ / PE\0\0 这类可识别头，运行时由
    Core/Qbar/Search/EverythingBundle.cs 解压释放，功能一模一样。

.PARAMETER Source
    原始二进制所在目录，默认 src\InputPlus\Assets\Everything。

.PARAMETER KeepOriginal
    保留原始二进制（默认会删掉：嵌进程序集的是 .gz，原件留在仓库里只是多占 2.4 MB，
    想再打一次包把 Everything 官方安装包里的文件丢进来即可）。

.EXAMPLE
    pwsh tools/pack-everything.ps1
#>
[CmdletBinding()]
param(
    [string]$Source,
    [switch]$KeepOriginal
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $Source) { $Source = Join-Path $PSScriptRoot '..\src\InputPlus\Assets\Everything' }
$Source = (Resolve-Path $Source).Path

$names = @('Everything.exe', 'Everything64.dll', 'Everything32.dll')

"源目录：$Source"
$missing = @($names | Where-Object { -not (Test-Path (Join-Path $Source $_)) })
if ($missing.Count -gt 0) {
    throw "缺少：$($missing -join ', ')。从 Everything 官方安装包里把这三份拷进 $Source 再跑一次。"
}

foreach ($name in $names) {
    $raw = Join-Path $Source $name
    $gz = $raw + '.gz'
    if (Test-Path $gz) { Remove-Item $gz -Force }

    $in = [System.IO.File]::OpenRead($raw)
    try {
        $out = [System.IO.File]::Create($gz)
        try {
            # Optimal：这三份只在启动时解一次。用 Optimal 而不是 SmallestSize ——
            # 后者是 .NET Core 才有的枚举值，而本脚本要能在 Windows PowerShell 5.1 下跑。
            # GZipStream 头部不写 mtime，同样的输入两次压缩字节完全一致（CI 与本地产物能对得上）。
            $gzip = New-Object System.IO.Compression.GZipStream($out, [System.IO.Compression.CompressionLevel]::Optimal, $true)
            try { $in.CopyTo($gzip) } finally { $gzip.Dispose() }
        }
        finally { $out.Dispose() }
    }
    finally { $in.Dispose() }

    $before = (Get-Item $raw).Length
    $after = (Get-Item $gz).Length
    $ver = (Get-Item $raw).VersionInfo.FileVersion
    "{0,-22} {1,10:N0} → {2,10:N0} 字节 ({3:P0})  版本={4}" -f $name, $before, $after, ($after / $before), $ver

    if (-not $KeepOriginal) { Remove-Item $raw -Force }
}

"完成。现在 csproj 里嵌的是 *.gz；运行时由 EverythingBundle 解压释放。"
if (-not $KeepOriginal) { "原始二进制已删除（要再打一次包：把官方文件拷回来再跑本脚本）。" }
