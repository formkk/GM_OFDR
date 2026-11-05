# Build and test Gmix.Data (Schema v0.2) with .NET Framework csc.
# ASCII-only source. Outputs:
#   GM-IX\bin\Gmix.Data.dll
#   GM-IX\bin\Gmix.Data.Test.exe
#   GM-IX\tests\fixtures\schema_v02_sample\

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$src  = $PSScriptRoot
$bin  = Join-Path $root 'bin'
$csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sampleDir = Join-Path $root 'tests\fixtures\schema_v02_sample'
$libOut = Join-Path $bin 'Gmix.Data.dll'
$testOut = Join-Path $bin 'Gmix.Data.Test.exe'

New-Item -ItemType Directory -Force -Path $bin | Out-Null

$libSources = @(
    (Join-Path $src 'SchemaModels.cs'),
    (Join-Path $src 'AcquisitionDefaults.cs'),
    (Join-Path $src 'CalibrationModel.cs'),
    (Join-Path $src 'MetricsAggregator.cs'),
    (Join-Path $src 'JsonAndFileStore.cs'),
    (Join-Path $src 'SchemaValidator.cs')
)

& $csc /nologo /platform:x64 /optimize+ /target:library /r:System.Web.Extensions.dll `
    /out:$libOut $libSources
if ($LASTEXITCODE -ne 0) { throw "library build failed: $LASTEXITCODE" }

& $csc /nologo /platform:x64 /optimize+ /r:System.Web.Extensions.dll `
    /r:$libOut `
    /out:$testOut (Join-Path $src 'Program.cs')
if ($LASTEXITCODE -ne 0) { throw "test build failed: $LASTEXITCODE" }

& $testOut $sampleDir
if ($LASTEXITCODE -ne 0) { throw "schema tests failed: $LASTEXITCODE" }

Write-Host "DONE: Gmix.Data.dll + Gmix.Data.Test.exe"
