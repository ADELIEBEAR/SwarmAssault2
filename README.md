# 스웜 어썰트 II

Windows용 플레이어 설치·자동 업데이트 도구입니다. 이 저장소는 배포 도구의 소스를 보관합니다. 원작 게임 그래픽, 개인 저장 데이터, 서명 비밀 키는 Git에 올리지 않습니다. 실제 게임 개발 프로젝트는 별도로 보관해야 합니다.

## 플레이어

**[Releases](https://github.com/ADELIEBEAR/SwarmAssault2/releases)**에서 `SwarmAssaultII-Setup-v버전.exe` 하나를 받아 실행합니다. Windows 10/11 64비트용이며 .NET Framework 4.8을 사용합니다. 게임에 필요한 .NET 6 런타임은 설치파일에 포함됩니다.

- 설치 기본 위치: `%LOCALAPPDATA%\Programs\SwarmAssaultII`
- 저장·설정·캠페인 진행·사용자 지도: `%LOCALAPPDATA%\SwarmAssaultII\Profile`
- 시작할 때 새 정식 버전을 확인하고 자동 설치합니다. 실패하면 기존 버전으로 실행합니다. 전투 중에는 업데이트하지 않습니다.
- 이전 개발판의 진행을 옮기려면 두 게임을 모두 종료한 뒤 기존 `Profile` 내용을 새 저장 폴더로 복사합니다.
- 제거는 Windows의 설치된 앱에서 합니다. 저장 데이터는 남깁니다.

첫 공개 배포 `2026.10.23.7`은 제공된 `20261023-r5` 게임을 그대로 포장합니다. 새 공개 배포 서명 키를 사용하므로 다른 키로 만든 개인 시험 설치본은 이 설치 EXE로 한 번 다시 설치해야 합니다. 기존 설치가 키 불일치로 거부하면 저장 폴더를 백업한 뒤 기존 설치를 제거하고 다시 설치하세요. ZIP 개발판의 `OpenSA.exe` 직접 실행은 자동 업데이트 대상이 아닙니다.

## 새 버전 만들기

게임 개발 프로젝트의 `active-version.json`이 가리키는 **패치 완료 실행본**을 포장합니다. 오래된 엔진 전체 빌드로 교체하지 않습니다. Python 3.9 이상, Windows .NET Framework 컴파일러를 사용하며 외부 설치 도구가 필요 없습니다.

```powershell
python build_release.py --project 'D:\SwarmAssaultII-Project' --output 'D:\release\2026.10.24.1' --version '2026.10.24.1' --private-key 'D:\private\signing-private.xml'
```

첫 빌드 때 RSA 서명 키 쌍을 만듭니다. **`signing-private.xml`과 `signing-public.xml`을 안전하게 백업하고, 다음 PC에서도 같은 키를 사용하세요.** 비밀 키는 게임 프로젝트·출력·이 저장소 바깥에 두어야 합니다. 비밀 키를 잃으면 기존 설치의 자동 업데이트를 서명할 수 없습니다. 키를 공개하거나 Releases에 첨부하지 마세요.

생성물 중 플레이어에게 필요한 것은 설치 EXE 하나입니다. 자동 업데이트를 위해 같은 Release에 `swarm-update.json`도 첨부합니다. `SHA256SUMS.txt`는 수동 검증용입니다. `launcher-build.exe`와 `package-inventory.json`은 개발 검사 자료이며 배포하지 않습니다.

```powershell
./Publish-Release.ps1 -Folder 'D:\release\2026.10.24.1' -Version '2026.10.24.1'
```

위 명령은 로그인된 GitHub CLI로 **초안 Release**를 만듭니다. GitHub Releases에서 검토 후 정식으로 게시하고 최신 버전으로 지정하면, 설치된 게임이 다음 실행 때 자동 갱신됩니다. 시험판(prerelease)과 초안은 자동 업데이트 대상이 아닙니다. 버전은 `연도.월.일.순번` 네 숫자로 올리며 이전 버전으로 자동 내려가지 않습니다. GitHub 계정 토큰은 플레이어 프로그램에 포함되지 않습니다.

## 검증

`Test-Distribution.ps1`은 별도 임시 설치와 가짜 게임 프로세스로 서명·손상 다운로드·설치·업데이트·저장 보존·삭제를 확인합니다. 실제 게임을 조작하거나 사용자 프로필에 접근하지 않습니다. 실전 게임 및 다른 PC의 GPU/드라이버 검사는 별도로 필요합니다.

## 배포 권리

이 저장소의 배포 도구는 MIT입니다. OpenRA/OpenSA 엔진은 자체 GPL 조건, 폰트와 원작·외부 모델은 각각의 조건을 따릅니다. MIT가 게임 자산에 적용되는 것은 아닙니다. 패키지에는 기존 출처 기록과 라이선스를 보존합니다. 공개 배포에 필요한 자산 권한은 게시자가 확인하며, 설치파일에서 개발 소스를 제외하는 것과 별도로 엔진 라이선스가 요구하는 대응 소스 제공 의무가 남습니다. 이 저장소의 배포 도구 소스가 게임 엔진의 전체 대응 소스를 대신하지는 않습니다.
