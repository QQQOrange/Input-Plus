# Qbar golden 对照：把当前实现的查询结果与基线逐条比。
#
# 用法：
#   $env:IP_EXE    = "<...>\InputPlus.exe"
#   $env:IP_GOLDEN = "<仓库>\docs\qbar\golden"
#   $env:IP_LABEL  = "pre-tantivy"     # 基线批次名
#   Invoke-Expression (Get-Content -Raw -Encoding UTF8 tools\qbar-golden-compare.ps1)
#
# 判定口径（与 TANTIVY-MIGRATION.md 的"行为差异"表配套）：
#   · **缺失** = 基线里有的路径，新实现里没有 → 这是召回退步，必须为 0；
#   · **新增** = 新实现多出来的路径 → 允许（正则/拼音/目录等新能力会带来增量），
#     但要逐条列出来，人工确认是"新能力"而不是"复核漏了"。
# 退出码：缺失 > 0 → 1。
$ErrorActionPreference = 'Stop'
$Exe = $env:IP_EXE
$GoldenDir = $env:IP_GOLDEN
$Label = $env:IP_LABEL
if (-not $Label) { $Label = 'pre-tantivy' }

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
    if (-not (Test-Path $path)) { return '' }
    return [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
}

$manifest = Join-Path $GoldenDir ($Label + '-manifest.tsv')
if (-not (Test-Path $manifest)) { Write-Output ('缺少基线清单：' + $manifest); exit 2 }

$rows = Get-Content $manifest -Encoding UTF8
$totalMissing = 0
$totalAdded = 0
$queries = 0

Write-Output 'slug' + "`t" + 'query' + "`t" + '基线' + "`t" + '现在' + "`t" + '缺失' + "`t" + '新增'
foreach ($row in $rows) {
    if ($row.StartsWith('label') -or $row.StartsWith('#')) { continue }
    $cols = $row -split "`t"
    if ($cols.Count -lt 7) { continue }
    $slug = $cols[1]
    $query = $cols[2]
    $baselineFile = Join-Path $GoldenDir ($Label + '-' + $slug + '.paths.txt')
    if (-not (Test-Path $baselineFile)) { continue }
    $baseline = @(Get-Content $baselineFile -Encoding UTF8 | Where-Object { $_ -ne '' })

    $null = Invoke-Ip @('--qbar-query', ('"' + $query + '"'))
    $text = Read-TempReport 'clp-qbar-query.txt'
    $now = New-Object System.Collections.Generic.List[string]
    foreach ($line in ($text -split "`r?`n")) {
        if ($line -match '^\s+\[(文件|目录)\]\s+(.+?)\s{3}\(分') { $now.Add($Matches[2]) }
    }

    $nowSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($p in $now) { $null = $nowSet.Add($p) }
    $baseSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($p in $baseline) { $null = $baseSet.Add($p) }

    $missing = New-Object System.Collections.Generic.List[string]
    foreach ($p in $baseSet) { if (-not $nowSet.Contains($p)) { $missing.Add($p) } }
    $added = New-Object System.Collections.Generic.List[string]
    foreach ($p in $nowSet) { if (-not $baseSet.Contains($p)) { $added.Add($p) } }

    $totalMissing += $missing.Count
    $totalAdded += $added.Count
    $queries++

    Write-Output ($slug + "`t" + $query + "`t" + $baseline.Count + "`t" + $now.Count + "`t" + $missing.Count + "`t" + $added.Count)
    foreach ($p in $missing) { Write-Output ('    [缺失] ' + $p) }
    foreach ($p in $added)   { Write-Output ('    [新增] ' + $p) }
}

Write-Output ''
Write-Output ('查询数=' + $queries + ' 缺失合计=' + $totalMissing + ' 新增合计=' + $totalAdded)
if ($totalMissing -gt 0) {
    Write-Output '结论：召回有退步（缺失 > 0）'
    exit 1
}
Write-Output '结论：没有缺失（新增项属于新能力，逐条列在上面）'
exit 0
