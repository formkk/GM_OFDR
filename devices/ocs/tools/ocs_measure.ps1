<#
.SYNOPSIS
  OCS 1x8 / 8x8 protocol measurement tool (TBD-O01 ~ O10).

.DESCRIPTION
  Runs a serial command matrix against the two OCS units and records raw
  TX/RX, latency distribution, error codes and state read-back.
  Zero third-party dependency: uses System.IO.Ports from .NET Framework.

  Safety:
    - Only <SETSW>/<QURSW>/<IDN?>/<SN?> are used.
    - <RST> is never sent unless -AllowReset is given.
    - The optical state captured at start is restored at the end and verified.

.PARAMETER ReadOnly
  Skip every command that can change the optical path (SETSW / matrix / soak).

.PARAMETER AllowReset
  Include <RST> in the error/reset probes. Off by default (NFR-008).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File devices/ocs/tools/ocs_measure.ps1 -Samples 100

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File devices/ocs/tools/ocs_measure.ps1 -ReadOnly -Samples 30
#>
[CmdletBinding()]
param(
    [string]$Port8x8 = 'COM3',
    [int]$Baud8x8 = 115200,
    [string]$Port1x8 = 'COM4',
    [int]$Baud1x8 = 9600,
    [int]$Samples = 100,
    [int]$SoakCycles = 0,
    [int]$TimeoutMs = 800,
    [string]$OutDir = '',
    [switch]$ReadOnly,
    [switch]$AllowReset
)

$ErrorActionPreference = 'Stop'

if ($Port8x8 -eq $Port1x8) { throw 'Port8x8 and Port1x8 must differ.' }

$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path (Split-Path -Parent $PSScriptRoot) ('capture\ocs-' + $stamp)
}
[void][System.IO.Directory]::CreateDirectory($OutDir)
$OutDir = (Resolve-Path $OutDir).Path
$script:LogPath = Join-Path $OutDir 'raw.jsonl'
$script:Utf8 = New-Object System.Text.UTF8Encoding($false)
$script:Seq = 0
$script:Records = New-Object System.Collections.ArrayList
[void][System.IO.File]::WriteAllText($script:LogPath, '', $script:Utf8)

function Write-Step([string]$Text) {
    Write-Host ('[ocs] ' + $Text)
}

# ---------------------------------------------------------------- serial I/O

function New-OcsPort {
    param(
        [string]$PortName,
        [int]$Baud,
        [string]$Parity = 'None',
        [int]$DataBits = 8,
        [string]$StopBits = 'One',
        [string]$Handshake = 'None'
    )
    $lastError = $null
    for ($attempt = 0; $attempt -lt 6; $attempt++) {
        $sp = New-Object System.IO.Ports.SerialPort
        $sp.PortName = $PortName
        $sp.BaudRate = $Baud
        $sp.Parity = $Parity
        $sp.DataBits = $DataBits
        $sp.StopBits = $StopBits
        $sp.Handshake = $Handshake
        $sp.ReadTimeout = 50
        $sp.WriteTimeout = 1000
        try {
            $sp.Open()
            if ($Handshake -eq 'None') {
                $sp.DtrEnable = $true
                $sp.RtsEnable = $true
            }
            Start-Sleep -Milliseconds 150
            $sp.DiscardInBuffer()
            return $sp
        } catch {
            $lastError = $_
            if ($sp.IsOpen) { $sp.Close() }
            $sp.Dispose()
            Start-Sleep -Milliseconds 400
        }
    }
    throw $lastError.Exception
}

function Read-Frame {
    param($Port, [int]$ReadTimeoutMs = 800)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $bytes = New-Object System.Collections.Generic.List[byte]
    $lastByteAt = 0
    while ($sw.ElapsedMilliseconds -lt $ReadTimeoutMs) {
        try {
            $b = $Port.ReadByte()
            if ($b -lt 0) { break }
            $bytes.Add([byte]$b)
            $lastByteAt = $sw.ElapsedMilliseconds
            if ($b -eq 0x3E) { break }   # '>'
        } catch [System.TimeoutException] {
            if ($bytes.Count -gt 0 -and ($sw.ElapsedMilliseconds - $lastByteAt) -gt 150) { break }
        }
    }
    $arr = $bytes.ToArray()
    $ascii = [System.Text.Encoding]::ASCII.GetString($arr)
    $hex = ($arr | ForEach-Object { $_.ToString('X2') }) -join ' '
    return [pscustomobject]@{
        Ascii     = $ascii
        Hex       = $hex
        Count     = $arr.Length
        ElapsedMs = [math]::Round($sw.ElapsedMilliseconds, 2)
        Bytes     = $arr
    }
}

function Add-Record {
    param($Device, [string]$Request, [string]$Term, $Response, [double]$LatencyMs, [string]$Kind, [string]$Note)
    $script:Seq++
    $rec = [pscustomobject]@{
        seq         = $script:Seq
        tsUtc       = (Get-Date).ToUniversalTime().ToString('o')
        device      = $Device
        request     = $Request
        term        = $Term
        response    = $Response.Ascii
        responseHex = $Response.Hex
        latencyMs   = $LatencyMs
        timedOut    = ($Response.Count -eq 0)
        kind        = $Kind
        note        = $Note
    }
    [void]$script:Records.Add($rec)
    [System.IO.File]::AppendAllText($script:LogPath, (($rec | ConvertTo-Json -Compress -Depth 5) + "`r`n"), $script:Utf8)
    return $rec
}

function Invoke-Cmd {
    param(
        $Ctx,
        [string]$Request,
        [string]$Term = "`r`n",
        [int]$ReadTimeoutMs = 0,
        [string]$Kind = 'cmd',
        [switch]$NoLog
    )
    if ($ReadTimeoutMs -le 0) { $ReadTimeoutMs = $TimeoutMs }
    $sp = $Ctx.Port
    if ($sp.BytesToRead -gt 0) { [void]$sp.ReadExisting() }
    $termName = 'none'
    if ($Term -eq "`r`n") { $termName = 'CRLF' }
    elseif ($Term -eq "`r") { $termName = 'CR' }
    elseif ($Term -eq "`n") { $termName = 'LF' }
    try {
        $sp.Write($Request + $Term)
    } catch {
        $empty = [pscustomobject]@{ Ascii = ''; Hex = ''; Count = 0; ElapsedMs = 0; Bytes = @() }
        if (-not $NoLog) { [void](Add-Record $Ctx.Name $Request $termName $empty 0 'write-error' $_.Exception.Message) }
        return $empty
    }
    $r = Read-Frame $sp $ReadTimeoutMs
    if (-not $NoLog) { [void](Add-Record $Ctx.Name $Request $termName $r $r.ElapsedMs $Kind '') }
    return $r
}

function Close-Ctx {
    param($Ctx)
    if ($null -ne $Ctx.Port) {
        try { if ($Ctx.Port.IsOpen) { $Ctx.Port.Close() } } catch { }
        try { $Ctx.Port.Dispose() } catch { }
        $Ctx.Port = $null
    }
}

function Reopen-Ctx {
    param($Ctx)
    Close-Ctx $Ctx
    $Ctx.Port = New-OcsPort -PortName $Ctx.PortName -Baud $Ctx.Baud
}

function Clear-Residual {
    param($Ctx, [int]$WaitMs = 300)
    $sp = $Ctx.Port
    if ($sp.BytesToRead -eq 0) { return '' }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $bytes = New-Object System.Collections.Generic.List[byte]
    while ($sw.ElapsedMilliseconds -lt $WaitMs) {
        try { $bytes.Add([byte]$sp.ReadByte()) } catch { break }
    }
    $ascii = [System.Text.Encoding]::ASCII.GetString($bytes.ToArray())
    if ($bytes.Count -gt 0) {
        $empty = [pscustomobject]@{ Ascii = $ascii; Hex = ''; Count = $bytes.Count; ElapsedMs = $WaitMs; Bytes = $bytes.ToArray() }
        [void](Add-Record $Ctx.Name '<residual>' 'none' $empty $WaitMs 'residual' '')
    }
    return $ascii
}

# ---------------------------------------------------------------- parsing

function Get-Identity {
    param($Ctx)
    $idn = Invoke-Cmd $Ctx '<IDN?>' -Kind 'identity'
    $sn = Invoke-Cmd $Ctx '<SN?>' -Kind 'identity'
    $model = $null; $date = $null; $ver = $null
    if ($idn.Ascii -match '^<GK,(GK-OSW-[^,]+),([^,]+),([^>]+)>$') {
        $model = $Matches[1]; $date = $Matches[2]; $ver = $Matches[3]
    }
    $serial = $null
    if ($sn.Ascii -match '^<(\d+)>$') { $serial = $Matches[1] }
    return [pscustomobject]@{
        IdnRaw = $idn.Ascii
        SnRaw  = $sn.Ascii
        Model  = $model
        Date   = $date
        Version= $ver
        Serial = $serial
        Baud   = $Ctx.Baud
        Port   = $Ctx.PortName
    }
}

function Get-State1x8 {
    param($Ctx)
    $r = Invoke-Cmd $Ctx '<QURSW?>' -Kind 'query'
    $ch = $null
    if ($r.Ascii -match '^<SW1-(\d{2})>$') { $ch = [int]$Matches[1] }
    return [pscustomobject]@{ Raw = $r.Ascii; Channel = $ch }
}

function Get-State8x8 {
    param($Ctx)
    $r = Invoke-Cmd $Ctx '<QURSW?>' -Kind 'query'
    $matrix = $null
    if ($r.Ascii -match '^<SW(\d{2}(?:_\d{2}){7})>$') {
        $parts = $Matches[1] -split '_'
        $matrix = @($parts | ForEach-Object { [int]$_ })
    }
    return [pscustomobject]@{ Raw = $r.Ascii; Matrix = $matrix }
}

function Get-QueryRoute8x8 {
    param($Ctx, [int]$InputIndex)
    $r = Invoke-Cmd $Ctx ('<QURSW{0}?>' -f $InputIndex) -Kind 'query'
    $out = $null
    if ($r.Ascii -match ('^<SW{0}-(\d{{2}})>$' -f $InputIndex)) { $out = [int]$Matches[1] }
    return [pscustomobject]@{ Raw = $r.Ascii; Output = $out }
}

function Format-Matrix([int[]]$Matrix) {
    if ($null -eq $Matrix) { return '' }
    return (($Matrix | ForEach-Object { $_.ToString() }) -join '')
}

# ---------------------------------------------------------------- statistics

function Get-Stats {
    param([double[]]$Values)
    if ($null -eq $Values -or $Values.Count -eq 0) {
        return [pscustomobject]@{ n = 0; min = $null; p50 = $null; p95 = $null; p99 = $null; max = $null; mean = $null }
    }
    $sorted = @($Values | Sort-Object)
    $n = $sorted.Count
    $pick = {
        param([double]$p)
        $idx = [math]::Ceiling($p / 100.0 * $n) - 1
        if ($idx -lt 0) { $idx = 0 }
        if ($idx -ge $n) { $idx = $n - 1 }
        return [math]::Round($sorted[$idx], 2)
    }
    return [pscustomobject]@{
        n    = $n
        min  = [math]::Round(($sorted | Measure-Object -Minimum).Minimum, 2)
        p50  = (& $pick 50)
        p95  = (& $pick 95)
        p99  = (& $pick 99)
        max  = [math]::Round(($sorted | Measure-Object -Maximum).Maximum, 2)
        mean = [math]::Round(($sorted | Measure-Object -Average).Average, 2)
    }
}

function Measure-Latency {
    param($Ctx, [string]$Label, [scriptblock]$Command, [int]$Count, [scriptblock]$Accept)
    $ok = New-Object System.Collections.Generic.List[double]
    $fail = 0
    for ($i = 1; $i -le $Count; $i++) {
        $r = & $Command $Ctx
        $good = $true
        if ($null -ne $Accept) { $good = [bool](& $Accept $r) }
        elseif ($r.Count -eq 0) { $good = $false }
        if ($good) { $ok.Add([double]$r.ElapsedMs) } else { $fail++ }
    }
    return [pscustomobject]@{
        label     = $Label
        requested = $Count
        ok        = $ok.Count
        failed    = $fail
        stats     = Get-Stats $ok.ToArray()
    }
}

# ---------------------------------------------------------------- tests

function Test-Terminators {
    param($Ctx)
    $rows = New-Object System.Collections.ArrayList
    $variants = @(
        @{ Name = 'none'; Value = '' },
        @{ Name = 'CR';   Value = "`r" },
        @{ Name = 'LF';   Value = "`n" },
        @{ Name = 'CRLF'; Value = "`r`n" }
    )
    foreach ($v in $variants) {
        $hits = 0
        $lat = New-Object System.Collections.Generic.List[double]
        for ($i = 0; $i -lt 3; $i++) {
            $r = Invoke-Cmd $Ctx '<IDN?>' -Term $v.Value -Kind ('o01-term-' + $v.Name)
            if ($r.Ascii -match '^<GK,') { $hits++; $lat.Add([double]$r.ElapsedMs) }
            Clear-Residual $Ctx 120 | Out-Null
        }
        [void]$rows.Add([pscustomobject]@{
            terminator = $v.Name
            hits       = $hits
            of         = 3
            latencyMs  = if ($lat.Count -gt 0) { [math]::Round((($lat | Measure-Object -Average).Average), 2) } else { $null }
        })
    }
    return $rows
}

function Test-EchoAndPipeline {
    param($Ctx)
    $echo = $false
    $r = Invoke-Cmd $Ctx '<IDN?>' -Kind 'o01-echo'
    if ($r.Ascii.Length -gt 5 -and $r.Ascii.StartsWith('<IDN?>')) { $echo = $true }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $ctx.Port.DiscardInBuffer()
    $ctx.Port.Write('<IDN?><SN?>' + "`r`n")
    $frames = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt 2; $i++) {
        $f = Read-Frame $ctx.Port 600
        [void]$frames.Add($f.Ascii)
        if ($f.Count -eq 0) { break }
    }
    $pipelineMs = [math]::Round($sw.ElapsedMilliseconds, 2)
    [void](Add-Record $Ctx.Name '<IDN?><SN?>' 'CRLF' ([pscustomobject]@{ Ascii = ($frames -join '|'); Hex = ''; Count = $frames.Count; ElapsedMs = $pipelineMs; Bytes = @() }) $pipelineMs 'o01-pipeline' '')

    $ctx.Port.DiscardInBuffer()
    $ctx.Port.Write('<IDN?>' + "`r`n")
    $ctx.Port.Write('<SN?>' + "`r`n")
    $backToBack = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt 2; $i++) {
        $f = Read-Frame $ctx.Port 600
        [void]$backToBack.Add($f.Ascii)
        if ($f.Count -eq 0) { break }
    }
    [void](Add-Record $Ctx.Name '<IDN?>,<SN?>' 'CRLF' ([pscustomobject]@{ Ascii = ($backToBack -join '|'); Hex = ''; Count = $backToBack.Count; ElapsedMs = 0; Bytes = @() }) 0 'o01-backtoback' '')

    return [pscustomobject]@{
        echo          = $echo
        pipelineFrames = $frames.Count
        pipelineRaw   = ($frames -join '|')
        backToBackFrames = $backToBack.Count
        backToBackRaw = ($backToBack -join '|')
    }
}

function Test-DtrRts {
    param($Ctx)
    $rows = New-Object System.Collections.ArrayList
    foreach ($mode in @('both-on', 'both-off')) {
        $sp = $null
        try {
            $sp = New-OcsPort -PortName $Ctx.PortName -Baud $Ctx.Baud
            if ($mode -eq 'both-off') { $sp.DtrEnable = $false; $sp.RtsEnable = $false }
            Start-Sleep -Milliseconds 120
            $sp.DiscardInBuffer()
            $sp.Write('<IDN?>' + "`r`n")
            $f = Read-Frame $sp 700
            [void]$rows.Add([pscustomobject]@{ mode = $mode; response = $f.Ascii })
            [void](Add-Record $Ctx.Name '<IDN?>' 'CRLF' $f $f.ElapsedMs ('o01-dtrrts-' + $mode) '')
        } catch {
            [void]$rows.Add([pscustomobject]@{ mode = $mode; response = ('ERR: ' + $_.Exception.Message) })
        } finally {
            if ($null -ne $sp) { try { if ($sp.IsOpen) { $sp.Close() } } catch { }; try { $sp.Dispose() } catch { }; Start-Sleep -Milliseconds 200 }
        }
    }
    return $rows
}

function Test-ParityAndFlow {
    param($Ctx)
    $rows = New-Object System.Collections.ArrayList
    foreach ($p in @('Even', 'Odd', 'Mark', 'Space')) {
        $sp = $null
        try {
            $sp = New-OcsPort -PortName $Ctx.PortName -Baud $Ctx.Baud -Parity $p
            $sp.DiscardInBuffer()
            $sp.Write('<IDN?>' + "`r`n")
            $f = Read-Frame $sp 600
            [void]$rows.Add([pscustomobject]@{ setting = ('8' + $p.Substring(0, 1) + '1'); response = $f.Ascii; valid = ($f.Ascii -match '^<GK,[^?]*>$') })
            [void](Add-Record $Ctx.Name '<IDN?>' 'CRLF' $f $f.ElapsedMs ('o09-parity-' + $p) '')
        } catch {
            [void]$rows.Add([pscustomobject]@{ setting = ('8' + $p.Substring(0, 1) + '1'); response = ('ERR: ' + $_.Exception.Message); valid = $false })
        } finally {
            if ($null -ne $sp) { try { if ($sp.IsOpen) { $sp.Close() } } catch { }; try { $sp.Dispose() } catch { }; Start-Sleep -Milliseconds 200 }
        }
    }
    foreach ($hs in @('RequestToSend', 'RequestToSendXOnXOff')) {
        $sp = $null
        try {
            $sp = New-OcsPort -PortName $Ctx.PortName -Baud $Ctx.Baud -Handshake $hs
            $sp.DiscardInBuffer()
            $sp.Write('<IDN?>' + "`r`n")
            $f = Read-Frame $sp 600
            [void]$rows.Add([pscustomobject]@{ setting = ('handshake=' + $hs); response = $f.Ascii; valid = ($f.Ascii -match '^<GK,[^?]*>$') })
            [void](Add-Record $Ctx.Name '<IDN?>' 'CRLF' $f $f.ElapsedMs ('o09-flow-' + $hs) '')
        } catch {
            [void]$rows.Add([pscustomobject]@{ setting = ('handshake=' + $hs); response = ('ERR: ' + $_.Exception.Message); valid = $false })
        } finally {
            if ($null -ne $sp) { try { if ($sp.IsOpen) { $sp.Close() } } catch { }; try { $sp.Dispose() } catch { }; Start-Sleep -Milliseconds 200 }
        }
    }
    foreach ($b in @(4800, 19200, 38400, 57600, 115200)) {
        if ($b -eq $Ctx.Baud) { continue }
        $sp = $null
        try {
            $sp = New-OcsPort -PortName $Ctx.PortName -Baud $b
            $sp.DiscardInBuffer()
            $sp.Write('<IDN?>' + "`r`n")
            $f = Read-Frame $sp 600
            [void]$rows.Add([pscustomobject]@{ setting = ('baud=' + $b); response = $f.Ascii; valid = ($f.Ascii -match '^<GK,[^?]*>$') })
            [void](Add-Record $Ctx.Name '<IDN?>' 'CRLF' $f $f.ElapsedMs ('o09-baud-' + $b) '')
        } catch {
            [void]$rows.Add([pscustomobject]@{ setting = ('baud=' + $b); response = ('ERR: ' + $_.Exception.Message); valid = $false })
        } finally {
            if ($null -ne $sp) { try { if ($sp.IsOpen) { $sp.Close() } } catch { }; try { $sp.Dispose() } catch { }; Start-Sleep -Milliseconds 200 }
        }
    }
    return $rows
}

function Invoke-MissingClose {
    param($Ctx)
    $ctx.Port.DiscardInBuffer()
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $ctx.Port.Write('<IDN?')
    $first = Read-Frame $ctx.Port 400
    $second = $null
    if ($first.Count -eq 0) {
        $ctx.Port.Write('>')
        $second = Read-Frame $ctx.Port 600
    } else {
        $second = [pscustomobject]@{ Ascii = ''; Hex = ''; Count = 0; ElapsedMs = 0; Bytes = @() }
    }
    [void](Add-Record $Ctx.Name '<IDN?' 'none' $first $first.ElapsedMs 'o05-missing-close-part1' '')
    [void](Add-Record $Ctx.Name '>' 'CRLF' $second $second.ElapsedMs 'o05-missing-close-part2' '')
    return [pscustomobject]@{ beforeClose = $first.Ascii; afterClose = $second.Ascii; elapsedMs = [math]::Round($sw.ElapsedMilliseconds, 2) }
}

function Test-ErrorMatrix {
    param($Ctx, [bool]$Is8x8, [bool]$ReadOnlyMode)
    $cases = New-Object System.Collections.ArrayList
    if ($Is8x8) {
        [void]$cases.Add(@{ req = '<FOO?>';           expect = 'ERROR';           state = $false })
        [void]$cases.Add(@{ req = '<SETSW9-01>';      expect = 'ERROR';           state = $true })
        [void]$cases.Add(@{ req = '<SETSW1-9>';       expect = 'ERROR';           state = $true })
        [void]$cases.Add(@{ req = '<SETSW0-01>';      expect = 'ERROR';           state = $true })
        [void]$cases.Add(@{ req = '<SETSW1-01-02>';   expect = 'ERROR';           state = $true })
        [void]$cases.Add(@{ req = '<SETSW1-1>';       expect = 'ACCEPT or ERROR'; state = $true })
        [void]$cases.Add(@{ req = '<>';               expect = 'ERROR or SILENT'; state = $false })
        [void]$cases.Add(@{ req = '<IDN>';            expect = 'ERROR';           state = $false })
        [void]$cases.Add(@{ req = '<idn?>';           expect = 'ACCEPT or ERROR'; state = $false })
        [void]$cases.Add(@{ req = '<IDN ? >';         expect = 'ACCEPT or ERROR'; state = $false })
        [void]$cases.Add(@{ req = '<SETSW1-01 >';     expect = 'ACCEPT or ERROR'; state = $true })
        [void]$cases.Add(@{ req = '<SETSW123456789>'; expect = 'ERROR';           state = $true })
        [void]$cases.Add(@{ req = '<SETSW11234567>';  expect = 'ER_SetValue';     state = $true })
        [void]$cases.Add(@{ req = '<QURSW9?>';        expect = 'ERROR';           state = $false })
        [void]$cases.Add(@{ req = '<QURSW1>';         expect = 'ERROR or SILENT'; state = $false })
        [void]$cases.Add(@{ req = '<SETSW1-+1>';      expect = 'ERROR';           state = $true })
        [void]$cases.Add(@{ req = '<SETSW1- 1>';      expect = 'ERROR';           state = $true })
    } else {
        [void]$cases.Add(@{ req = '<SETSW2-01>';      expect = 'ERROR';           state = $true })
        [void]$cases.Add(@{ req = '<SETSW1-9>';       expect = 'ERROR';           state = $true })
        [void]$cases.Add(@{ req = '<SETSW1--1>';      expect = 'ERROR';           state = $true })
        [void]$cases.Add(@{ req = '<QURSW1?>';        expect = 'ERROR or SILENT'; state = $false })
        [void]$cases.Add(@{ req = '<SETSW11234567>';  expect = 'ERROR';           state = $true })
        [void]$cases.Add(@{ req = '<FOO?>';           expect = 'ERROR';           state = $false })
        [void]$cases.Add(@{ req = '<>';               expect = 'ERROR or SILENT'; state = $false })
        [void]$cases.Add(@{ req = '<idn?>';           expect = 'ACCEPT or ERROR'; state = $false })
        [void]$cases.Add(@{ req = '<SETSW1-01 >';     expect = 'ACCEPT or ERROR'; state = $true })
    }

    $rows = New-Object System.Collections.ArrayList
    foreach ($c in $cases) {
        if ($ReadOnlyMode -and $c.state) { continue }
        $before = ''
        if ($Is8x8) { $before = (Get-State8x8 $Ctx).Raw } else { $before = (Get-State1x8 $Ctx).Raw }
        $r = Invoke-Cmd $Ctx $c.req -Kind 'o05-error'
        Start-Sleep -Milliseconds 80
        $after = ''
        if ($Is8x8) { $after = (Get-State8x8 $Ctx).Raw } else { $after = (Get-State1x8 $Ctx).Raw }
        $class = 'OTHER'
        if ($r.Count -eq 0) { $class = 'SILENT' }
        elseif ($r.Ascii -eq '<ERROR>') { $class = 'ERROR' }
        elseif ($r.Ascii -eq '<ER_SetValue>') { $class = 'ER_SetValue' }
        elseif ($r.Ascii -match '^(<OK>|<SW\d|<\d+-\d+>)') { $class = 'ACCEPT' }
        $changed = ($before -ne $after)
        [void]$rows.Add([pscustomobject]@{
            device      = $Ctx.Name
            request     = $c.req
            expected    = $c.expect
            response    = $r.Ascii
            responseHex = $r.Hex
            class       = $class
            latencyMs   = $r.ElapsedMs
            stateBefore = $before
            stateAfter  = $after
            stateChanged= $changed
        })
        Clear-Residual $Ctx 100 | Out-Null
    }
    return $rows
}

function Test-Uniqueness8x8 {
    param($Ctx, [int[]]$InitialMatrix)
    $rows = New-Object System.Collections.ArrayList
    $state = Get-State8x8 $Ctx
    $m = $state.Matrix
    if ($null -eq $m) { return $rows }

    $sourceInput = 0; $conflictOutput = 0
    for ($i = 1; $i -le 8; $i++) {
        if ($m[$i - 1] -ne 0) { $sourceInput = $i; $conflictOutput = $m[$i - 1]; break }
    }
    if ($sourceInput -eq 0) {
        [void](Invoke-Cmd $Ctx '<SETSW1-01>' -Kind 'o04-setup')
        $sourceInput = 1; $conflictOutput = 1
    }
    $otherInput = 2
    if ($sourceInput -eq 2) { $otherInput = 3 }
    $otherBefore = $m[$otherInput - 1]

    $r = Invoke-Cmd $Ctx ('<SETSW{0}-{1:D2}>' -f $otherInput, $conflictOutput) -Kind 'o04-single-conflict'
    Start-Sleep -Milliseconds 100
    $after = Get-State8x8 $Ctx
    $accepted = ($r.Ascii -eq ('<{0}-{1:D2}>' -f $otherInput, $conflictOutput))
    $readBack = $null
    if ($null -ne $after.Matrix) { $readBack = $after.Matrix[$otherInput - 1] }
    [void]$rows.Add([pscustomobject]@{
        test        = 'single-route conflict (two inputs -> same output)'
        request     = ('<SETSW{0}-{1:D2}>' -f $otherInput, $conflictOutput)
        response    = $r.Ascii
        readBack    = $after.Raw
        readBackOut = $readBack
        duplicateAllowed = ($accepted -and $readBack -eq $conflictOutput)
        note        = ('input {0} already pointed to OUT{1}' -f $sourceInput, $conflictOutput)
    })

    $matrixBefore = (Get-State8x8 $Ctx).Raw
    $dup = '<SETSW11234567>'
    $rd = Invoke-Cmd $Ctx $dup -Kind 'o04-matrix-duplicate'
    Start-Sleep -Milliseconds 100
    $matrixAfter = Get-State8x8 $Ctx
    [void]$rows.Add([pscustomobject]@{
        test        = 'matrix duplicate outputs'
        request     = $dup
        response    = $rd.Ascii
        readBack    = $matrixAfter.Raw
        readBackOut = $null
        duplicateAllowed = ($rd.Ascii -ne '<ER_SetValue>')
        note        = ('matrix before=' + $matrixBefore)
    })

    $zero = Invoke-Cmd $Ctx '<SETSW10000000>' -Kind 'o04-matrix-zero'
    [void]$rows.Add([pscustomobject]@{
        test        = 'matrix command with zeros (disconnect)'
        request     = '<SETSW10000000>'
        response    = $zero.Ascii
        readBack    = (Get-State8x8 $Ctx).Raw
        readBackOut = $null
        duplicateAllowed = $null
        note        = ''
    })

    $over = Invoke-Cmd $Ctx '<SETSW90000000>' -Kind 'o04-matrix-range'
    [void]$rows.Add([pscustomobject]@{
        test        = 'matrix command out of range (9)'
        request     = '<SETSW90000000>'
        response    = $over.Ascii
        readBack    = (Get-State8x8 $Ctx).Raw
        readBackOut = $null
        duplicateAllowed = $null
        note        = ''
    })

    return $rows
}

function Restore-1x8 {
    param($Ctx, [int]$Channel)
    $resp = Invoke-Cmd $Ctx ('<SETSW1-{0:D2}>' -f $Channel) -Kind 'restore'
    $state = Get-State1x8 $Ctx
    return [pscustomobject]@{ Sent = ('<SETSW1-{0:D2}>' -f $Channel); Response = $resp.Ascii; ReadBack = $state.Raw; Ok = ($state.Channel -eq $Channel) }
}

function Restore-8x8 {
    param($Ctx, [int[]]$Matrix)
    $cmd = '<SETSW' + (Format-Matrix $Matrix) + '>'
    $resp = Invoke-Cmd $Ctx $cmd -Kind 'restore'
    $state = Get-State8x8 $Ctx
    $ok = $false
    if ($null -ne $state.Matrix -and $state.Matrix.Count -eq 8) {
        $ok = $true
        for ($i = 0; $i -lt 8; $i++) { if ($state.Matrix[$i] -ne $Matrix[$i]) { $ok = $false } }
    }
    return [pscustomobject]@{ Sent = $cmd; Response = $resp.Ascii; ReadBack = $state.Raw; Ok = $ok }
}

# ---------------------------------------------------------------- report

function New-Report {
    param($Result, [string]$Path)
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('# OCS protocol measurement report')
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine(('- Started (UTC): {0}' -f $Result.startedAtUtc))
    [void]$sb.AppendLine(('- Finished (UTC): {0}' -f $Result.finishedAtUtc))
    [void]$sb.AppendLine(('- Raw log: `{0}`' -f $Result.rawLog))
    [void]$sb.AppendLine(('- Raw log SHA256: `{0}`' -f $Result.rawLogSha256))
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('## 1. Devices')
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('| Unit | Port | Baud | IDN | SN |')
    [void]$sb.AppendLine('|---|---|---:|---|---|')
    foreach ($d in $Result.devices) {
        [void]$sb.AppendLine(('| {0} | {1} | {2} | {3} | {4} |' -f $d.Name, $d.Info.Port, $d.Info.Baud, $d.Info.IdnRaw, $d.Info.SnRaw))
    }
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('## 2. Terminator / echo / pipeline (TBD-O01)')
    [void]$sb.AppendLine('')
    foreach ($d in $Result.devices) {
        [void]$sb.AppendLine(('### {0}' -f $d.Name))
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine('| Terminator | Hits / 3 | Avg latency ms |')
        [void]$sb.AppendLine('|---|---:|---:|')
        foreach ($t in $d.terminators) {
            [void]$sb.AppendLine(('| {0} | {1} | {2} |' -f $t.terminator, $t.hits, $t.latencyMs))
        }
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine(('- Echo: {0}' -f $d.echoPipeline.echo))
        [void]$sb.AppendLine(('- Pipeline frames: {0} (raw `{1}`)' -f $d.echoPipeline.pipelineFrames, $d.echoPipeline.pipelineRaw))
        [void]$sb.AppendLine(('- Back-to-back frames: {0} (raw `{1}`)' -f $d.echoPipeline.backToBackFrames, $d.echoPipeline.backToBackRaw))
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine('| DTR/RTS | Response |')
        [void]$sb.AppendLine('|---|---|')
        foreach ($x in $d.dtrRts) { [void]$sb.AppendLine(('| {0} | `{1}` |' -f $x.mode, $x.response)) }
        [void]$sb.AppendLine('')
    }
    [void]$sb.AppendLine('## 3. Latency (TBD-O02)')
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('| Device | Command | n | ok | fail | min | p50 | p95 | p99 | max | mean |')
    [void]$sb.AppendLine('|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|')
    foreach ($m in $Result.latency) {
        $s = $m.stats
        [void]$sb.AppendLine(('| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} | {10} |' -f $m.device, $m.label, $s.n, $m.ok, $m.failed, $s.min, $s.p50, $s.p95, $s.p99, $s.max, $s.mean))
    }
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('### Recommended response timeout')
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('Rule: max(300 ms, ceil(p99 x 3 to 100 ms)). Retry at most 2 times with 100 ms / 300 ms backoff.')
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('| Device | Command | p99 | Recommended timeout |')
    [void]$sb.AppendLine('|---|---|---:|---:|')
    foreach ($m in $Result.latency) {
        if ($null -ne $m.stats -and $null -ne $m.stats.p99) {
            $rec = [math]::Max(300, [math]::Ceiling(($m.stats.p99 * 3) / 100.0) * 100)
            [void]$sb.AppendLine(('| {0} | {1} | {2} | {3} |' -f $m.device, $m.label, $m.stats.p99, $rec))
        }
    }
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('## 4. Error matrix (TBD-O05)')
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('| Device | Request | Expected | Response | Class | State changed |')
    [void]$sb.AppendLine('|---|---|---|---|---|---|')
    foreach ($e in $Result.errors) {
        [void]$sb.AppendLine(('| {0} | `{1}` | {2} | `{3}` | {4} | {5} |' -f $e.device, $e.request, $e.expected, $e.response, $e.class, $e.stateChanged))
    }
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('## 5. 8x8 uniqueness (TBD-O04)')
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('| Test | Request | Response | Read-back | Duplicate allowed | Note |')
    [void]$sb.AppendLine('|---|---|---|---|---|---|')
    foreach ($u in $Result.uniqueness) {
        [void]$sb.AppendLine(('| {0} | `{1}` | `{2}` | `{3}` | {4} | {5} |' -f $u.test, $u.request, $u.response, $u.readBack, $u.duplicateAllowed, $u.note))
    }
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('## 6. Parity / flow / baud tolerance (TBD-O09)')
    [void]$sb.AppendLine('')
    foreach ($d in $Result.devices) {
        [void]$sb.AppendLine(('### {0}' -f $d.Name))
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine('| Setting | Frame valid | Response |')
        [void]$sb.AppendLine('|---|---|---|')
        foreach ($p in $d.tolerance) { [void]$sb.AppendLine(('| {0} | {1} | `{2}` |' -f $p.setting, $p.valid, $p.response)) }
        [void]$sb.AppendLine('')
    }
    [void]$sb.AppendLine('## 7. Disconnect / idempotency / missing close')
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine(('```text' + "`r`n" + $Result.misc + "`r`n" + '```'))
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('## 8. State restore')
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine(('```text' + "`r`n" + $Result.restore + "`r`n" + '```'))
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('## 9. TBD-O01..O10 status')
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('| TBD | Status | Evidence |')
    [void]$sb.AppendLine('|---|---|---|')
    foreach ($t in $Result.tbd) { [void]$sb.AppendLine(('| {0} | {1} | {2} |' -f $t.id, $t.status, $t.evidence)) }
    [System.IO.File]::WriteAllText($Path, $sb.ToString(), $script:Utf8)
}

# ---------------------------------------------------------------- main

$startedUtc = (Get-Date).ToUniversalTime().ToString('o')
Write-Step ('output = ' + $OutDir)
Write-Step ('8x8 = {0} @ {1}, 1x8 = {2} @ {3}' -f $Port8x8, $Baud8x8, $Port1x8, $Baud1x8)

$dev8 = $null
$dev1 = $null
try {
    Write-Step 'open ports'
    $dev8 = [pscustomobject]@{ Name = '8x8'; PortName = $Port8x8; Baud = $Baud8x8; Port = (New-OcsPort -PortName $Port8x8 -Baud $Baud8x8) }
    $dev1 = [pscustomobject]@{ Name = '1x8'; PortName = $Port1x8; Baud = $Baud1x8; Port = (New-OcsPort -PortName $Port1x8 -Baud $Baud1x8) }

    Write-Step 'identity'
    $id8 = Get-Identity $dev8
    $id1 = Get-Identity $dev1
    if ($null -eq $id8.Model) { throw ('8x8 did not answer IDN? on {0} @ {1}. Got: {2}' -f $Port8x8, $Baud8x8, $id8.IdnRaw) }
    if ($null -eq $id1.Model) { throw ('1x8 did not answer IDN? on {0} @ {1}. Got: {2}' -f $Port1x8, $Baud1x8, $id1.IdnRaw) }
    Write-Step ('8x8: ' + $id8.IdnRaw)
    Write-Step ('1x8: ' + $id1.IdnRaw)

    Write-Step 'capture initial state'
    $state8Before = Get-State8x8 $dev8
    $state1Before = Get-State1x8 $dev1
    if ($null -eq $state8Before.Matrix -or $null -eq $state1Before.Channel) { throw 'Initial state query failed.' }
    Write-Step ('8x8 matrix = ' + $state8Before.Raw)
    Write-Step ('1x8 channel = ' + $state1Before.Raw)

    Write-Step 'TBD-O01 terminator / echo / pipeline'
    $term8 = Test-Terminators $dev8
    $term1 = Test-Terminators $dev1
    $echo8 = Test-EchoAndPipeline $dev8
    $echo1 = Test-EchoAndPipeline $dev1

    Write-Step 'TBD-O01 ancillary: DTR/RTS (port reopened)'
    Close-Ctx $dev8
    Close-Ctx $dev1
    Start-Sleep -Milliseconds 500
    $dtr8 = Test-DtrRts $dev8
    $dtr1 = Test-DtrRts $dev1

    Write-Step 'TBD-O09 parity / flow / baud (port reopened)'
    $tol8 = Test-ParityAndFlow $dev8
    $tol1 = Test-ParityAndFlow $dev1
    Reopen-Ctx $dev8
    Reopen-Ctx $dev1

    Write-Step 'TBD-O02 latency'
    $latency = New-Object System.Collections.ArrayList
    $n = [math]::Max(1, $Samples)
    foreach ($pair in @(@{ ctx = $dev8; name = '8x8' }, @{ ctx = $dev1; name = '1x8' })) {
        $c = $pair.ctx
        $m = Measure-Latency -Ctx $c -Label 'IDN?' -Command { param($x) Invoke-Cmd $x '<IDN?>' -Kind 'o02-latency' } -Count $n -Accept { param($r) $r.Ascii -match '^<GK,' }
        [void]$latency.Add([pscustomobject]@{ device = $pair.name; label = $m.label; requested = $m.requested; ok = $m.ok; failed = $m.failed; stats = $m.stats })
        $m = Measure-Latency -Ctx $c -Label 'SN?' -Command { param($x) Invoke-Cmd $x '<SN?>' -Kind 'o02-latency' } -Count $n -Accept { param($r) $r.Ascii -match '^<\d+>$' }
        [void]$latency.Add([pscustomobject]@{ device = $pair.name; label = $m.label; requested = $m.requested; ok = $m.ok; failed = $m.failed; stats = $m.stats })
        if ($pair.name -eq '1x8') {
            $m = Measure-Latency -Ctx $c -Label 'QURSW?' -Command { param($x) Invoke-Cmd $x '<QURSW?>' -Kind 'o02-latency' } -Count $n -Accept { param($r) $r.Ascii -match '^<SW1-\d{2}>$' }
        } else {
            $m = Measure-Latency -Ctx $c -Label 'QURSW?' -Command { param($x) Invoke-Cmd $x '<QURSW?>' -Kind 'o02-latency' } -Count $n -Accept { param($r) $r.Ascii -match '^<SW\d{2}(_\d{2}){7}>$' }
        }
        [void]$latency.Add([pscustomobject]@{ device = $pair.name; label = $m.label; requested = $m.requested; ok = $m.ok; failed = $m.failed; stats = $m.stats })
    }

    if (-not $ReadOnly) {
        Write-Step 'TBD-O02 switching latency'
        $ch0 = $state1Before.Channel
        $alt = if ($ch0 -eq 1) { 2 } else { 1 }
        $switchCount = [math]::Max(10, [math]::Min($n, 50))
        $m = Measure-Latency -Ctx $dev1 -Label 'SETSW (switch)' -Command { param($x) Invoke-Cmd $x ('<SETSW1-{0:D2}>' -f $alt) -Kind 'o02-latency' } -Count $switchCount -Accept { param($r) $r.Ascii -match '^<1-\d{2}>$' }
        [void]$latency.Add([pscustomobject]@{ device = '1x8'; label = $m.label; requested = $m.requested; ok = $m.ok; failed = $m.failed; stats = $m.stats })
        $null = Restore-1x8 $dev1 $ch0
        $in8 = $state8Before.Matrix[7]
        $m = Measure-Latency -Ctx $dev8 -Label ('SETSW (same value, IN8->OUT{0})' -f $in8) -Command { param($x) Invoke-Cmd $x ('<SETSW8-{0:D2}>' -f $in8) -Kind 'o02-latency' } -Count $switchCount -Accept { param($r) $r.Ascii -match '^<8-\d{2}>$' }
        [void]$latency.Add([pscustomobject]@{ device = '8x8'; label = $m.label; requested = $m.requested; ok = $m.ok; failed = $m.failed; stats = $m.stats })
        $matrixCmd = '<SETSW' + (Format-Matrix $state8Before.Matrix) + '>'
        $matrixCount = [math]::Max(10, [math]::Min($n, 30))
        $m = Measure-Latency -Ctx $dev8 -Label 'SETSW matrix (same state)' -Command { param($x) Invoke-Cmd $x $matrixCmd -Kind 'o02-latency' } -Count $matrixCount -Accept { param($r) $r.Ascii -eq '<OK>' }
        [void]$latency.Add([pscustomobject]@{ device = '8x8'; label = $m.label; requested = $m.requested; ok = $m.ok; failed = $m.failed; stats = $m.stats })
    }

    Write-Step 'TBD-O05 error matrix'
    $errFlat = New-Object System.Collections.ArrayList
    foreach ($e in (Test-ErrorMatrix $dev8 $true $ReadOnly.IsPresent)) { [void]$errFlat.Add($e) }
    foreach ($e in (Test-ErrorMatrix $dev1 $false $ReadOnly.IsPresent)) { [void]$errFlat.Add($e) }

    Write-Step 'TBD-O04 uniqueness'
    $uniq = New-Object System.Collections.ArrayList
    if (-not $ReadOnly) {
        foreach ($u in (Test-Uniqueness8x8 $dev8 $state8Before.Matrix)) { [void]$uniq.Add($u) }
    }

    Write-Step 'misc: disconnect / idempotency / missing close'
    $miscLines = New-Object System.Collections.ArrayList
    [void]$miscLines.Add('--- 8x8 ---')
    $mc8 = Invoke-MissingClose $dev8
    [void]$miscLines.Add(('missing close before=`{0}` after=`{1}`' -f $mc8.beforeClose, $mc8.afterClose))
    $sn8a = Invoke-Cmd $dev8 '<SN?>'
    $sn8b = Invoke-Cmd $dev8 '<SN?>'
    [void]$miscLines.Add(('idempotent SN?: `{0}` / `{1}`' -f $sn8a.Ascii, $sn8b.Ascii))
    [void]$miscLines.Add('--- 1x8 ---')
    $mc1 = Invoke-MissingClose $dev1
    [void]$miscLines.Add(('missing close before=`{0}` after=`{1}`' -f $mc1.beforeClose, $mc1.afterClose))
    $sn1a = Invoke-Cmd $dev1 '<SN?>'
    $sn1b = Invoke-Cmd $dev1 '<SN?>'
    [void]$miscLines.Add(('idempotent SN?: `{0}` / `{1}`' -f $sn1a.Ascii, $sn1b.Ascii))
    if (-not $ReadOnly) {
        $ch = $state1Before.Channel
        $disc1 = Invoke-Cmd $dev1 '<SETSW1-00>' -Kind 'o02-disconnect'
        Start-Sleep -Milliseconds 100
        $disc1State = Get-State1x8 $dev1
        [void]$miscLines.Add(('1x8 disconnect: response=`{0}` readback=`{1}`' -f $disc1.Ascii, $disc1State.Raw))
        $null = Restore-1x8 $dev1 $ch
        $in8 = $state8Before.Matrix[7]
        $disc8 = Invoke-Cmd $dev8 '<SETSW8-00>' -Kind 'o02-disconnect'
        Start-Sleep -Milliseconds 100
        $disc8State = Get-QueryRoute8x8 $dev8 8
        [void]$miscLines.Add(('8x8 disconnect IN8: response=`{0}` readback=`{1}`' -f $disc8.Ascii, $disc8State.Raw))
        if ($in8 -ne 0) { [void](Invoke-Cmd $dev8 ('<SETSW8-{0:D2}>' -f $in8) -Kind 'restore') }
    }

    if ($SoakCycles -gt 0 -and -not $ReadOnly) {
        Write-Step ('TBD-O10 soak: {0} cycles' -f $SoakCycles)
        $miss = 0; $timeouts = 0; $ch = $state1Before.Channel
        $alt = if ($ch -eq 1) { 2 } else { 1 }
        for ($i = 0; $i -lt $SoakCycles; $i++) {
            $t = if ($i % 2 -eq 0) { $alt } else { $ch }
            $r = Invoke-Cmd $dev1 ('<SETSW1-{0:D2}>' -f $t) -Kind 'o10-soak'
            if ($r.Count -eq 0) { $timeouts++ }
            elseif ($r.Ascii -ne ('<1-{0:D2}>' -f $t)) { $miss++ }
        }
        [void]$miscLines.Add(('1x8 soak: cycles={0} timeouts={1} mismatches={2}' -f $SoakCycles, $timeouts, $miss))
        $null = Restore-1x8 $dev1 $ch
    }

    Write-Step 'restore optical state'
    $restoreLines = New-Object System.Collections.ArrayList
    if (-not $ReadOnly) {
        $r1 = Restore-1x8 $dev1 $state1Before.Channel
        [void]$restoreLines.Add(('1x8 sent={0} response=`{1}` readback=`{2}` ok={3}' -f $r1.Sent, $r1.Response, $r1.ReadBack, $r1.Ok))
        $r8 = Restore-8x8 $dev8 $state8Before.Matrix
        [void]$restoreLines.Add(('8x8 sent={0} response=`{1}` readback=`{2}` ok={3}' -f $r8.Sent, $r8.Response, $r8.ReadBack, $r8.Ok))
    } else {
        [void]$restoreLines.Add('read-only mode: no state change, restore skipped')
    }

    # --- assemble result
    $rawHash = (Get-FileHash -Algorithm SHA256 -Path $script:LogPath).Hash
    $termWorks = @($term8 | Where-Object { $_.hits -gt 0 }).Count -gt 0 -and @($term1 | Where-Object { $_.hits -gt 0 }).Count -gt 0
    $hasLatency = @($latency | Where-Object { $null -ne $_.stats -and $_.stats.n -gt 0 }).Count -gt 0
    $hasErrors = $errFlat.Count -gt 0
    $hasUniq = $uniq.Count -gt 0
    $hasTol = (@($tol8).Count + @($tol1).Count) -gt 0
    $tbd = New-Object System.Collections.ArrayList
    [void]$tbd.Add([pscustomobject]@{ id = 'TBD-O01'; status = $(if ($termWorks) { 'closed' } else { 'failed' }); evidence = 'terminator matrix, echo, pipeline, DTR/RTS' })
    [void]$tbd.Add([pscustomobject]@{ id = 'TBD-O02'; status = $(if ($hasLatency) { 'closed' } else { 'failed' }); evidence = 'latency stats, disconnect format, idempotency, timeout recommendation' })
    [void]$tbd.Add([pscustomobject]@{ id = 'TBD-O03'; status = 'partial'; evidence = 'state snapshot only; settle time needs optical power meter' })
    [void]$tbd.Add([pscustomobject]@{ id = 'TBD-O04'; status = $(if ($hasUniq) { 'closed' } else { 'skipped/failed' }); evidence = 'single-route conflict + matrix duplicate' })
    [void]$tbd.Add([pscustomobject]@{ id = 'TBD-O05'; status = $(if ($hasErrors) { 'closed' } else { 'failed' }); evidence = 'error matrix on both units' })
    [void]$tbd.Add([pscustomobject]@{ id = 'TBD-O06'; status = 'not-testable'; evidence = 'needs multimeter / vendor pinout' })
    [void]$tbd.Add([pscustomobject]@{ id = 'TBD-O07'; status = 'not-testable'; evidence = 'needs calibrated power meter / ORL' })
    [void]$tbd.Add([pscustomobject]@{ id = 'TBD-O08'; status = 'partial'; evidence = 'protocol-side routing only; optical mapping needs OFDR/power meter' })
    [void]$tbd.Add([pscustomobject]@{ id = 'TBD-O09'; status = $(if ($hasTol) { 'closed' } else { 'failed' }); evidence = 'baud scan + parity + flow control' })
    [void]$tbd.Add([pscustomobject]@{ id = 'TBD-O10'; status = $(if ($SoakCycles -gt 0) { 'partial' } else { 'not-tested' }); evidence = $(if ($SoakCycles -gt 0) { ('soak {0} cycles' -f $SoakCycles) } else { 'run with -SoakCycles 1000' }) })

    $result = [pscustomobject]@{
        tool          = 'devices/ocs/tools/ocs_measure.ps1'
        startedAtUtc  = $startedUtc
        finishedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        readOnly      = [bool]$ReadOnly
        rawLog        = $script:LogPath
        rawLogSha256  = $rawHash
        devices       = @(
            [pscustomobject]@{ Name = '8x8'; Info = $id8; PortState = $state8Before.Raw; terminators = @($term8); echoPipeline = $echo8; dtrRts = @($dtr8); tolerance = @($tol8) },
            [pscustomobject]@{ Name = '1x8'; Info = $id1; PortState = $state1Before.Raw; terminators = @($term1); echoPipeline = $echo1; dtrRts = @($dtr1); tolerance = @($tol1) }
        )
        latency      = @($latency)
        errors       = @($errFlat)
        uniqueness   = @($uniq)
        misc         = ($miscLines -join "`r`n")
        restore      = ($restoreLines -join "`r`n")
        tbd          = @($tbd)
        records      = $script:Records.Count
    }
    $resultPath = Join-Path $OutDir 'result.json'
    [System.IO.File]::WriteAllText($resultPath, ($result | ConvertTo-Json -Depth 8), $script:Utf8)
    New-Report $result (Join-Path $OutDir 'report.md')

    Write-Step ('records = ' + $script:Records.Count)
    Write-Step ('result  = ' + $resultPath)
    Write-Step ('raw sha = ' + $rawHash)
    Write-Output ''
    Write-Output ($restoreLines -join "`n")
}
catch {
    Write-Step ('ERROR: ' + $_.Exception.Message)
    try {
        if (-not $ReadOnly -and $null -ne $state8Before) {
            Write-Step 'emergency restore'
            $null = Restore-1x8 $dev1 $state1Before.Channel
            $null = Restore-8x8 $dev8 $state8Before.Matrix
        }
    } catch { Write-Step ('emergency restore failed: ' + $_.Exception.Message) }
    throw
}
finally {
    if ($null -ne $dev8 -and $null -ne $dev8.Port -and $dev8.Port.IsOpen) { $dev8.Port.Close() }
    if ($null -ne $dev1 -and $null -ne $dev1.Port -and $dev1.Port.IsOpen) { $dev1.Port.Close() }
}






