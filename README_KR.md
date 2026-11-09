<p align="center">
  <img src="Assets/app.png" alt="TaskbarStats" width="128">
</p>

<h1 align="center">TaskbarStats</h1>

<p align="center">
  Windows 11 작업표시줄에 상주하는 가벼운 시스템 모니터.<br>
  CPU, GPU, 메모리, 디스크, 네트워크, 그리고 모든 센서의 사용량과 온도를 한눈에 보여 줍니다.
</p>

<p align="center">
  <a href="README.md">English</a> · <b>한국어</b>
</p>

<p align="center">
  <img src="docs/widget.png" alt="작업표시줄 위젯">
</p>

## 기능

- **작업표시줄 위젯**: 위/아래 두 줄 배치. 모듈마다 라벨, 막대, 얇은 막대, 히스토리 그래프, 원형 그래프, 코어 막대, 퍼센트, 온도, 속도, 원하는 센서 값 등을 골라 구성합니다.
- **모듈**: CPU, GPU, Memory, Disks, Network, Sensors, 그리고 간격을 조절하는 Space.
- **모듈별 클릭 팝업**: Acrylic 배경이며 위젯의 라이트/다크 테마를 따릅니다.
  - CPU: 코어별 막대, 히스토리, 주파수(전체 / 효율 / 성능 코어), 상위 프로세스, 업타임
  - GPU: 온도, VRAM, 클럭, 팬, 전력, 히스토리, GPU 사용 프로세스
  - Memory: 사용 / 여유 / 캐시 / 커밋 / 스왑, 히스토리, 메모리 많이 쓰는 프로세스
  - Disks: 드라이브별 용량, 읽기 / 쓰기 히스토리, 파일 I/O 상위 프로세스, 드라이브 온도
  - Network: 내부 IP, 다운 / 업 히스토리, 연결된 어댑터
  - Sensors: LibreHardwareMonitor가 읽을 수 있는 모든 센서를 하드웨어별로 실시간 표시
- **Sections 설정 페이지**: 팝업 섹션을 켜고 끄고, 프로세스 표시 개수를 정합니다.
- **어떤 센서든 작업표시줄에**: 온도, 팬, 부하, 클럭, 전력 센서를 골라 값, 막대, 그래프, 원형으로 표시합니다.
- **Fluent 설정 창**(Mica): 드래그 앤 드롭 레이아웃, 글꼴·크기·굵기, °C / °F, rpm 단위.
- **가벼움**: 화면에 쓰이는 센서만 읽고, 팝업은 열려 있는 동안에만 데이터를 수집하며, 대기 중 메모리 사용량이 작습니다.
- **부트캠프 맥**: 애플 SMC 드라이버로 온도와 팬을 읽습니다(읽기 전용). 드라이버가 처음 감지되면 자동으로 켜집니다.

## 설치

1. [Releases](../../releases)에서 `TaskbarStats-Setup-<버전>.exe`를 받습니다. 포터블 zip도 제공됩니다.
2. 설치 프로그램을 실행합니다. [PawnIO](https://pawnio.eu) 드라이버를 내려받아 설치하는 옵션(권장, 기본 선택)과 시작 작업 등록 옵션이 있습니다.

CPU와 메인보드 센서는 관리자 권한과 PawnIO 드라이버가 필요합니다. Windows 카운터(사용량, 메모리, 디스크, 네트워크)와 NVIDIA GPU 정보는 둘 다 없어도 동작합니다.

**Run as administrator**(설정 → General)는 기본으로 켜져 있습니다. 앱을 열면 관리자 권한을 요청하고, 시작 작업이 등록되어 있으면 UAC 프롬프트 없이 관리자로 시작합니다.

## 빌드

Windows와 .NET 10 SDK가 필요합니다.

```
dotnet build -c Release
```

설치 프로그램은 [Inno Setup 6](https://jrsoftware.org/isinfo.php)이 필요합니다.

```
dotnet publish TaskbarStats.csproj -c Release -r win-x64 --self-contained -o publish
iscc /DAppVersion=1.0.0 /DSourceDir=..\publish installer\TaskbarStats.iss
```

`v*` 태그를 푸시하면 GitHub Actions가 설치 프로그램과 포터블 zip을 만들어 릴리스에 첨부합니다.

## 라이선스

Apache License 2.0 — Copyright 2026 NSteven. [LICENSE](LICENSE)와 [NOTICE](NOTICE)를 참고하세요.

서드파티: [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)(MPL-2.0), HidSharp(Apache-2.0), System.Management(MIT), Xanmono 글꼴(SIL OFL 1.1). 선택 사항인 [PawnIO](https://pawnio.eu) 드라이버는 namazso의 별도 프로젝트이며 공식 배포처에서 내려받고, 이 저장소에 포함하지 않습니다. 라이선스 전문은 [`licenses/`](licenses/)에 있습니다.

Apple, NVIDIA, AMD, Intel, Windows는 각 소유자의 상표이며, 이 프로젝트는 그들과 관련이 없습니다.
