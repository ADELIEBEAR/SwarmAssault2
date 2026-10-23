param([Parameter(Mandatory=$true)][string]$Launcher, [string]$Output = (Join-Path $env:TEMP ('sii-tests-' + [Guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
$testFolder = [IO.Path]::GetFullPath($Output)
[IO.Directory]::CreateDirectory($testFolder) | Out-Null
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$assembly = Join-Path $testFolder 'SwarmAssaultII.exe'
Copy-Item -LiteralPath $Launcher -Destination $assembly
$stub = Join-Path $testFolder 'OpenSA.exe'
& $csc /nologo /target:winexe /platform:x64 "/out:$stub" (Join-Path $PSScriptRoot 'StubGame.cs')
if ($LASTEXITCODE -ne 0) { throw 'stub compile failed' }
$testExe = Join-Path $testFolder 'Tests.exe'
& $csc /nologo /platform:x64 "/out:$testExe" "/reference:$assembly" /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.Net.Http.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll (Join-Path $PSScriptRoot 'Tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'test compile failed' }
& $testExe $testFolder $stub $assembly
if ($LASTEXITCODE -ne 0) { throw 'distribution tests failed' }
& {
    # A test double, not a change to Windows antivirus settings.
    function Get-MpComputerStatus { [pscustomobject]@{AntivirusEnabled=$true;RealTimeProtectionEnabled=$false} }
    $blocked = $false
    try { & (Join-Path $PSScriptRoot 'Test-ReleaseSecurity.ps1') -Installer $assembly }
    catch { if ($_.Exception.Message -like '*실시간 보호*') { $blocked = $true } else { throw } }
    if (!$blocked) { throw 'security gate accepted disabled protection' }
    'PASS release gate rejects disabled protection without changing antivirus settings'
}
