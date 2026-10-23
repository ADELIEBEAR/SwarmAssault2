param([Parameter(Mandatory=$true)][string]$Installer)
$ErrorActionPreference = 'Stop'
$setup = (Resolve-Path -LiteralPath $Installer).Path
$status = Get-MpComputerStatus
if (!$status.AntivirusEnabled -or !$status.RealTimeProtectionEnabled) { throw 'Defender 및 실시간 보호를 켠 상태에서 검사해야 합니다.' }
$scanner = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
if (!(Test-Path -LiteralPath $scanner -PathType Leaf)) { throw 'Defender 검사 도구를 찾을 수 없습니다.' }
# DisableRemediation only prevents this scan from deleting evidence; antivirus protection stays enabled.
& $scanner -Scan -ScanType 3 -File $setup -DisableRemediation
if ($LASTEXITCODE -ne 0) { throw 'Defender 검사 실패 또는 위협 탐지: 실행·게시하지 마세요.' }
$signature = Get-AuthenticodeSignature -LiteralPath $setup
if ($signature.Status -ne 'Valid') { Write-Warning '공인 프로그램 서명이 없어 SmartScreen 게시자/평판 경고가 남을 수 있습니다.' }
[pscustomobject]@{AntivirusScan='PASS';Definitions=$status.AntivirusSignatureVersion;Sha256=(Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash;Authenticode=$signature.Status.ToString()}
