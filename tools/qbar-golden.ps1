# Qbar 文件搜索 golden 基线导出。
#
# 用法（环境变量传参，避免中文路径在命令行里被转码）：
#   $env:IP_EXE   = "<...>\InputPlus.exe"
#   $env:IP_OUT   = "<仓库>\docs\qbar\golden"
#   $env:IP_LABEL = "pre-tantivy"        # 批次名，写进文件名与清单
#   Invoke-Expression (Get-Content -Raw -Encoding UTF8 tools\qbar-golden.ps1)
#
# 为什么先等索引：查询在索引未就绪时会"成功"返回 0 条 —— 拿这种输出当基线等于把
# "还没有索引"写成"正确答案"，后面所有对照都失去意义。所以先跑 --index-status
# 等到退出码 0（它只有在索引可用且有文件时才返回 0）。
$ErrorActionPreference = 'Stop'
$uid = New-Object System.Text.UTF8Encoding($false)
$Exe = $env:IP_EXE
$OutDir = $env:IP_OUT
$Label = $env:IP_LABEL
if (-not $Label) { $Label = 'pre' }

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }

function Invoke-Ip {
    param([string[]]$ArgList)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = ($ArgList -join ' ')
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = [System.IO.Path]::GetDirectoryName($Exe)
    $p = [System.Diagnostics.Process]::Start($psi)
    $null = $p.WaitForExit(600000)
    if (-not $p.HasExited) { try { $p.Kill() } catch { } }
    return $p.ExitCode
}

function Read-TempReport {
    param([string]$Name)
    $path = Join-Path ([System.IO.Path]::GetTempPath()) $Name
    if (-not (Test-Path $path)) { return $null }
    return [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
}

# ── 1) 等索引就绪 ────────────────────────────────────────────────
$ready = $false
$deadline = (Get-Date).AddSeconds(300)
$polls = 0
while ((Get-Date) -lt $deadline) {
    $polls++
    $exit = Invoke-Ip @('--index-status')
    if ($exit -eq 0) { $ready = $true; break }
    Start-Sleep -Seconds 3
}
Write-Output ('INDEX_READY=' + $ready + ' polls=' + $polls)

# ── 2) 查询集：覆盖全部谓词 ──────────────────────────────────────
$queries = @(
    'report',
    'rprt',
    'yh',
    'yinhang',
    'doc:',
    'doc: 报表',
    'ext:pdf;docx size:>1mb',
    'date:week',
    '-ext:tmp',
    '"年度 总结"',
    '(',
    'C:\Windows',
    'pic:',
    'video:',
    'audio:',
    'folder:',
    'size:10mb..1gb',
    'wfn:*.pdf',
    'reporter',
    '说明',
    'path:Documents',
    '-path:AppData',
    'regex:^rep.*\.docx$',
    'file: 报表'
)

$rows = New-Object System.Collections.Generic.List[string]
$rows.Add("label`tindex`tquery`tbackend`thits`tpaths`tfile")

for ($i = 0; $i -lt $queries.Count; $i++) {
    $q = $queries[$i]
    $slug = ('{0:d2}' -f $i)
    $exit = Invoke-Ip @('--qbar-query', ('"' + $q + '"'))
    $text = Read-TempReport 'clp-qbar-query.txt'
    if ($null -eq $text) { $text = '' }

    $rawFile = Join-Path $OutDir ($Label + '-' + $slug + '.txt')
    [System.IO.File]::WriteAllText($rawFile, $text, $uid)

    $backend = ''
    if ($text -match '(?m)^后端:\s*(.+)$') { $backend = $Matches[1].Trim() }
    $hits = 0
    if ($text -match '(?m)^候选:\s*(\d+)\s*条') { $hits = [int]$Matches[1] }

    # 结果行形如：  [文件] C:\path   (分 123 / 档 1 / ...)  ← 加亮名
    $paths = New-Object System.Collections.Generic.List[string]
    foreach ($line in ($text -split "`r?`n")) {
        if ($line -match '^\s+\[(文件|目录)\]\s+(.+?)\s{3}\(分') {
            $paths.Add($Matches[2])
        }
    }
    $pathFile = Join-Path $OutDir ($Label + '-' + $slug + '.paths.txt')
    [System.IO.File]::WriteAllLines($pathFile, $paths, $uid)

    $rows.Add(($Label + "`t" + $slug + "`t" + $q + "`t" + $backend + "`t" + $hits + "`t" + $paths.Count + "`t" + $slug + '.paths.txt'))
    Write-Output ('  [' + $slug + '] exit=' + $exit + ' paths=' + $paths.Count + '  ' + $q)
}

$rows.Add(('#INDEX_READY=' + $ready))
$manifest = Join-Path $OutDir ($Label + '-manifest.tsv')
[System.IO.File]::WriteAllLines($manifest, $rows, $uid)
Write-Output ('MANIFEST=' + $manifest)
Write-Output ('TRUSTWORTHY=' + $ready)
