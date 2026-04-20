# ZEEPLOG - FIT Data Processing & Visualization

FIT(Binary) 운동 기록 파일을 분석하여 CSV 파일로 변환하고, 이동 경로를 영상으로 시각화하는 도구 모음임.
시연 영상 : https://youtu.be/ntO73FkwCGU

## 1. 주요 기능
- **CSV 변환**: 바이너리 형태의 `.fit` 파일을 읽어 시간, 위치(Do), 속도, 심박수 등을 포함한 `.csv` 파일로 변환함.
- **경로 시각화 영상 생성**: FIT, GPX, TCX 파일을 지원하며, GPS 좌표를 기반으로 전체 이동 경로와 실시간 위치를 시각화한 `.mp4` 영상을 생성함.
- **실시간 시간 동기화**: 기록된 데이터의 타임스탬프를 분석하여 1초당 1프레임(보간 처리)으로 생성하며, 실제 재생 배속(FPS)을 자유롭게 조절 가능함.
- **데이터 보정 및 필터링**: 
  - 속도 튐 현상 방지를 위한 이동 평균 필터(Moving Average Filter) 적용.
  - **Dropout 방지**: 센서 데이터가 일시적으로 누락되거나 0이 들어올 경우, 심박수(20초)와 속도(10초)를 이전 값으로 유지하다가 해당 시간이 지나면 0으로 업데이트함.
- **편의 기능**:
  - **파일 중복 방지**: 동일한 이름의 영상 파일이 존재할 경우 파일명 뒤에 `(1)`, `(2)` 등 숫자를 붙여 자동 생성함.
  - **다중 파일 처리**: 파일 선택 창에서 여러 개의 파일을 한 번에 선택하여 일괄 변환 가능함.

## 2. 사전 준비사항 (Requirements)
Python 환경에서 아래 라이브러리 설치가 필요함.
```powershell
pip install fitparse opencv-python numpy
```

## 3. 사용 방법

### (1) FIT to CSV 변환 (`fit_to_csv.py`)
FIT 파일을 CSV 형식으로 변환함. 위도/경도가 Degrees 단위로 자동 변환되며 데이터는 반올림 처리됨.
```powershell
python fit_to_csv.py
```
- **입력**: `Zepp20260407070319.fit`
- **출력**: `Zepp20260407070319.csv`

### (2) 경로 시각화 영상 생성 (`generate_video.py`)
운동 기록 파일(FIT, GPX, TCX)을 애니메이션 영상으로 제작함. 파이썬 파일을 직접 실행하면 파일 선택 대화상자가 표시됨.
```powershell
# 옵션 1: 실행 후 파일 선택 (GUI)
python generate_video.py

# 옵션 2: 파일명을 인자로 전달하여 실행 (CLI)
python generate_video.py Zepp_record.fit
```
- **지원 확장자**: `.fit`, `.gpx`, `.tcx`
- **출력**: `[파일명]route(n).mp4`
- **시각화 규칙**:
  - 회색 선: 전체 경로 (배경)
  - 오렌지색 선: 현재까지 이동한 경로
  - 빨간 점: 현재 위치
  - 상단 정보 바: `Elapsed Time | Speed (km/h) | Heart Rate (bpm)` 표시
  - **Elapsed Time**: UTC 기준 시간이 아닌, 출발 시점으로부터의 경과 시간(HH:MM:SS)을 표시함.

## 4. 폴더 구조
- `AnGLog/`: 작업 이력 및 로그 파일 저장소.
- `*.fit`: 원본 바이너리 데이터.
- `*.csv`: 텍스트 형태의 정제된 데이터.
- `*route.mp4`: 시각화 결과 영상.

## 5. 참고 사항
- 로봇 제어 루프의 필터링 방식과 유사하게 데이터 노이즈를 억제하도록 설계됨.
- 새로운 FIT 파일 적용 시, 스크립트 내의 `FIT_FILE` 변수 경로를 수정하여 사용 가능함.
