# GM-IX hardware subproject: build and run the verified test cases.
# Prerequisites: x64 Windows; runtime\ contains SDK DLLs, msvcr100.dll and config\;
# device 192.168.5.20 reachable from host 192.168.5.55/24.
# NOTE: multi-segment acquisition (segs>=2) and the stop-interrupt test hang by design.
# They are intentionally excluded from this script.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$rt   = Join-Path $root 'runtime'
$bin  = Join-Path $root 'bin'
$res  = Join-Path $root 'results'
$src  = Join-Path $root 'src\Qt2144Probe.cs'
$exe  = Join-Path $bin 'Qt2144Probe.exe'
$csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

New-Item -ItemType Directory -Force -Path $bin, $res, (Join-Path $rt 'log') | Out-Null

if (-not (Test-Path (Join-Path $rt 'msvcr100.dll'))) {
    Write-Warning 'runtime\msvcr100.dll missing: pthreadVC2.dll needs VC++2010 runtime; qt_acq.dll will fail to load.'
}

& $csc /nologo /platform:x64 /optimize+ /out:$exe $src
if ($LASTEXITCODE -ne 0) { throw "csc failed: $LASTEXITCODE" }

function Invoke-Stage([string]$name, [string[]]$argv, [int]$timeoutSec) {
    $outLog = Join-Path $res "$name.console.log"
    $errLog = Join-Path $res "$name.err.log"
    $p = Start-Process -FilePath $exe -ArgumentList $argv -WorkingDirectory $rt -PassThru -NoNewWindow `
         -RedirectStandardOutput $outLog -RedirectStandardError $errLog
    if (-not $p.WaitForExit($timeoutSec * 1000)) {
        Stop-Process -Id $p.Id -Force
        Write-Warning "$name TIMEOUT after $timeoutSec s (process killed)"
    } else {
        Write-Host "$name exit=$($p.ExitCode)"
    }
}

Invoke-Stage 'stage0' @($rt, 'stage0') 60

$cases = @(
    @('stage1_10mhz',  @($rt,'stage1','10','1','1','0','0', (Join-Path $res 'raw-10mhz-1ms-1seg.bin'),  (Join-Path $res 'stage1_10mhz.json'))),
    @('stage1_50mhz',  @($rt,'stage1','50','8','1','0','1', (Join-Path $res 'raw-50mhz-8ms-1seg.bin'),  (Join-Path $res 'stage1_50mhz.json'))),
    @('stage1_125mhz', @($rt,'stage1','125','10','1','0','0',(Join-Path $res 'raw-125mhz-10ms-1seg.bin'),(Join-Path $res 'stage1_125mhz.json'))),
    @('stage1_250mhz', @($rt,'stage1','250','10','1','0','0',(Join-Path $res 'raw-250mhz-10ms-1seg.bin'),(Join-Path $res 'stage1_250mhz.json')))
)
foreach ($c in $cases) { Invoke-Stage $c[0] $c[1] 150 }

Invoke-Stage 'stage2_50mhz' @(
    $rt,'stage2', (Join-Path $res 'raw-50mhz-8ms-1seg.bin'), '50',
    (Join-Path $res 'stage2_50mhz.json'),
    (Join-Path $res 'peaks_50mhz.csv'),
    (Join-Path $res 'events_50mhz.csv')) 120

Write-Host 'Done: stage0 / single-segment capture / ofdr+detect. Multi-segment and stop-interrupt tests must be run manually with an external timeout.'
