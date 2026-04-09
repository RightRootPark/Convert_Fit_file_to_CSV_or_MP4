# ZEEPLOG - FIT Data Processing & Visualization

FIT(Binary) 운동 기록 파일을 분석하여 CSV 파일로 변환하고, 이동 경로를 영상으로 시각화하는 도구 모음임.

## 1. 주요 기능
- **CSV 변환**: 바이너리 형태의 `.fit` 파일을 읽어 시간, 위치(Do), 속도, 심박수 등을 포함한 `.csv` 파일로 변환함.
- **경로 시각화 영상 생성**: FIT, GPX, TCX 파일을 지원하며, GPS 좌표를 기반으로 전체 이동 경로와 실시간 위치를 보여주는 400x400 비율의 애니메이션 영상(`.mp4`)을 생성함.
- **데이터 보정**: 
  - 센서 정밀도를 고려한 데이터 반올림(소수점 첫째 자리).
  - 속도 튐 현상 방지를 위한 이동 평균 필터(Moving Average Filter) 적용.

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
운동 기록 파일(FIT, GPX, TCX)을 애니메이션 영상으로 제작함. 확장자에 따라 자동으로 파서를 선택함.
```powershell
# 파일명을 인자로 전달하여 실행
python generate_video.py Zepp20260407070319.gpx
```
- **지원 확장자**: `.fit`, `.gpx`, `.tcx`
- **출력**: `[파일명]route.mp4`
- **시각화 규칙**:
  - 회색 선: 전체 경로 (배경)
  - 오렌지색 선: 현재까지 이동한 경로
  - 빨간 점: 현재 위치
  - 상단 바: `Timestamp | Speed (km/h)` 정보 표시

## 4. 폴더 구조
- `AnGLog/`: 작업 이력 및 로그 파일 저장소.
- `*.fit`: 원본 바이너리 데이터.
- `*.csv`: 텍스트 형태의 정제된 데이터.
- `*route.mp4`: 시각화 결과 영상.

## 5. 참고 사항
- 로봇 제어 루프의 필터링 방식과 유사하게 데이터 노이즈를 억제하도록 설계됨.
- 새로운 FIT 파일 적용 시, 스크립트 내의 `FIT_FILE` 변수 경로를 수정하여 사용 가능함.
