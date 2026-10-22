param([Parameter(Mandatory=$true)][string]$Folder, [Parameter(Mandatory=$true)][string]$Version)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d{4}\.\d{1,2}\.\d{1,2}\.\d{1,6}$') { throw '버전은 연도.월.일.순번 형식입니다.' }
$releaseFolder = [IO.Path]::GetFullPath($Folder)
$setup = Join-Path $releaseFolder "SwarmAssaultII-Setup-v$Version.exe"
$update = Join-Path $releaseFolder 'swarm-update.json'
$sums = Join-Path $releaseFolder 'SHA256SUMS.txt'
$notes = Join-Path $releaseFolder 'RELEASE-NOTES.md'
foreach ($file in @($setup,$update,$sums,$notes)) { if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "파일 없음: $file" } }
$envelope = Get-Content -LiteralPath $update -Raw | ConvertFrom-Json
$payload = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($envelope.Payload)) | ConvertFrom-Json
if ($payload.Version -ne $Version -or $payload.Repository -ne 'ADELIEBEAR/SwarmAssault2') { throw '업데이트 정보와 배포 버전이 다릅니다.' }
if ((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant() -ne $payload.Sha256) { throw '설치파일 해시가 다릅니다.' }
& gh release create "v$Version" $setup $update $sums --repo 'ADELIEBEAR/SwarmAssault2' --draft --title "스웜 어썰트 II $Version" --notes-file $notes
if ($LASTEXITCODE -ne 0) { throw 'GitHub 초안 생성 실패. gh auth login으로 로그인 상태를 확인하세요.' }
