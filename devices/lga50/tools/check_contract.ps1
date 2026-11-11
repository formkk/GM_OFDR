# 契约文档机械检查：引用完整性 / 编号连续 / 计数一致 / 已删概念残留 / 枚举封闭 / 字段一致
# 用法：powershell -File check_contract.ps1 [-Path <契约文档>]
# 默认检查 docs/contracts 下最新版本的《统一数据契约_v*.md》；任一检查 FAIL 时退出码为 1。

param([string]$Path)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

if ([string]::IsNullOrEmpty($Path)) {
    $docs = Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $here))) 'docs\contracts'
    $candidate = Get-ChildItem -Path $docs -Filter '统一数据契约_v*.md' |
        Sort-Object Name -Descending | Select-Object -First 1
    if ($null -eq $candidate) { Write-Host 'FAIL 未找到契约文档'; exit 2 }
    $Path = $candidate.FullName
}
if (-not (Test-Path -LiteralPath $Path)) { Write-Host ('FAIL 文件不存在: ' + $Path); exit 2 }

$lines = Get-Content -LiteralPath $Path -Encoding UTF8
$text = ($lines -join "`n")
$fails = New-Object 'System.Collections.Generic.List[string]'
$passCount = 0

function Fail([string]$message) {
    $script:fails.Add($message)
    Write-Host ('    - ' + $message)
}

function Check([string]$name, [scriptblock]$body) {
    $before = $script:fails.Count
    & $body
    if ($script:fails.Count -eq $before) {
        Write-Host ('[PASS] ' + $name)
        $script:passCount++
    } else {
        Write-Host ('[FAIL] ' + $name)
    }
}

Write-Host ('契约检查: ' + $Path)
Write-Host ''

# ---------- 预解析：标题、所属章节、条款定义 ----------
$headings = New-Object 'System.Collections.Generic.List[string]'
$headingLines = New-Object 'System.Collections.Generic.List[string]'
$sectionOf = New-Object 'int[]' $lines.Count
$current = 0

for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]
    if ($line -match '^##\s+(\d+)\.') { $current = [int]$Matches[1] }
    $sectionOf[$i] = $current
    if ($line -match '^#{2,4}\s+(\d+(?:\.\d+)?)') { $headings.Add($Matches[1]) }
    if ($line -match '^#{2,4}\s') { $headingLines.Add($line) }
}

$defs = New-Object 'System.Collections.Generic.List[object]'
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^\|\s*\*\*([GLCRDE])-(\d{2})\*\*') {
        $defs.Add([pscustomobject]@{ Id = ($Matches[1] + '-' + $Matches[2]); Group = $Matches[1]; Num = [int]$Matches[2] })
    }
}

# ---------- 1. 引用完整性 ----------
Check '1 引用完整性（§x.y 目标存在）' {
    $refs = [regex]::Matches($text, '§(\d+(?:\.\d+)?)') |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    foreach ($r in $refs) {
        if (-not $headings.Contains($r)) { Fail ('引用了不存在的章节: §' + $r) }
    }
}

# ---------- 2. 条款编号连续且唯一 ----------
Check '2 条款编号连续且唯一' {
    $seen = @{}
    foreach ($d in $defs) {
        if ($seen.ContainsKey($d.Id)) { Fail ('条款重复定义: ' + $d.Id) }
        $seen[$d.Id] = $true
    }
    foreach ($g in @('G', 'L', 'C', 'R', 'D', 'E')) {
        $nums = @($defs | Where-Object { $_.Group -eq $g } | ForEach-Object { $_.Num } | Sort-Object)
        if ($nums.Count -eq 0) { Fail ('缺少 ' + $g + ' 组条款'); continue }
        for ($k = 0; $k -lt $nums.Count; $k++) {
            if ($nums[$k] -ne ($k + 1)) {
                $want = '{0:D2}' -f ($k + 1)
                Fail ('编号不连续: ' + $g + ' 组应为 ' + $g + '-' + $want)
                break
            }
        }
    }
}

# ---------- 3. 条款引用均有定义 ----------
Check '3 条款引用均有定义' {
    $defined = @{}
    foreach ($d in $defs) { $defined[$d.Id] = $true }
    $matches = [regex]::Matches($text, '\b([GLCRDE])-(\d{2})\b')
    foreach ($m in $matches) {
        $id = $m.Groups[1].Value + '-' + $m.Groups[2].Value
        if (-not $defined.ContainsKey($id)) { Fail ('引用了未定义的条款: ' + $id) }
    }
}

# ---------- 4. 标题中的编号范围与实际条数一致 ----------
Check '4 编号范围与实际条数一致' {
    foreach ($h in $headingLines) {
        if ($h -match '([GLCRDE])-(\d{2})~([GLCRDE])-(\d{2})') {
            $g1 = $Matches[1]; $n1 = [int]$Matches[2]
            $g2 = $Matches[3]; $n2 = [int]$Matches[4]
            if ($g1 -ne $g2) { Fail ('编号范围跨组: ' + $h); continue }
            $actual = @($defs | Where-Object { $_.Group -eq $g1 }).Count
            $declared = $n2 - $n1 + 1
            if ($actual -ne $declared) {
                Fail (($g1 + ' 组标题声明 ' + $declared + ' 条，实际 ' + $actual + ' 条'))
            }
        }
    }
}

# ---------- 5. 已删概念无残留 ----------
Check '5 已删概念无残留（§6/§8/§9 除外）' {
    $banned = @('Replay', 'ChannelIn', 'ChannelOut', 'Epoch', 'Sequence', 'Timestamp',
        'ValueUnit', 'AxisKind', 'ScanQuality', 'GroupId')
    $skip = @(6, 8, 9)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($skip -contains $sectionOf[$i]) { continue }
        foreach ($b in $banned) {
            if ($lines[$i] -match [regex]::Escape($b)) {
                Fail ('§' + $sectionOf[$i] + ' 第 ' + ($i + 1) + ' 行出现已删概念: ' + $b)
            }
        }
    }
}

# ---------- 6. 枚举封闭 ----------
Check '6 枚举封闭（SourceType 只允许两个取值）' {
    $tree = @($lines | Where-Object { $_ -match 'SourceType\s*:\s*(.+)$' })
    if ($tree.Count -eq 0) { Fail '未找到 SourceType 定义行' }
    else {
        foreach ($t in $tree) {
            if ($t -notmatch 'Lianxun\s*\|\s*HengLga50') {
                Fail ('SourceType 取值不合法: ' + $t.Trim())
            }
        }
    }
}

# ---------- 7. 字段在树/表/对照表中一致 ----------
Check '7 字段三处一致' {
    $expect = @(
        @{ Field = 'Axis.Start'; MinSection2 = 1; InSection4 = $true },
        @{ Field = 'Axis.Step'; MinSection2 = 1; InSection4 = $true },
        @{ Field = 'Axis.Count'; MinSection2 = 1; InSection4 = $true },
        @{ Field = 'Values'; MinSection2 = 1; InSection4 = $true },
        @{ Field = 'ScanId'; MinSection2 = 2; InSection4 = $false },
        @{ Field = 'SourceType'; MinSection2 = 2; InSection4 = $false },
        @{ Field = 'DeviceId'; MinSection2 = 2; InSection4 = $false }
    )
    foreach ($e in $expect) {
        $in2 = 0; $in4 = 0
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i].Contains($e.Field)) {
                if ($sectionOf[$i] -eq 2) { $in2++ }
                if ($sectionOf[$i] -eq 4) { $in4++ }
            }
        }
        if ($in2 -lt $e.MinSection2) { Fail ($e.Field + ' 在 §2 中出现不足（结构与字段表）') }
        if ($e.InSection4 -and $in4 -lt 1) { Fail ($e.Field + ' 未出现在 §4 对照表') }
    }
}

# ---------- 汇总 ----------
Write-Host ''
$total = 7
if ($fails.Count -eq 0) {
    Write-Host ('汇总：PASS ' + $total + '，FAIL 0')
    Write-Host '结论：机械检查全部通过'
    exit 0
}
Write-Host ('汇总：PASS ' + $passCount + '，FAIL ' + ($total - $passCount))
Write-Host '结论：存在不通过项，先修文档再提交评审'
exit 1
