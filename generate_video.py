import cv2
import numpy as np
from fitparse import FitFile
import sys
import os

# 400x400 비율 설정
WIDTH, HEIGHT = 400, 400
FPS = 10  # 초당 프레임 수
FIT_FILE = r"C:\DTWorkSpace\AntiGravityProject\ZEEPLOG\Zepp20260405154310.fit"
# 출력 파일명을 원본파일명+route.mp4 로 설정
OUTPUT_FILE = os.path.join(os.path.dirname(FIT_FILE), os.path.splitext(os.path.basename(FIT_FILE))[0] + "route.mp4")

def create_video():
    print(f"FIT 파일 파싱 중: {FIT_FILE}")
    try:
        fitfile = FitFile(FIT_FILE)
    except Exception as e:
        print(f"FIT 파일 읽기 오류: {e}")
        return

    # 1. 데이터 추출
    raw_points = []
    for record in fitfile.get_messages('record'):
        lat = record.get_value('position_lat')
        lon = record.get_value('position_long')
        timestamp = record.get_value('timestamp')
        speed = record.get_value('speed') # 단위: m/s (FIT 표준)
        
        if lat is not None and lon is not None:
            raw_points.append({
                'coord': (lat * (180.0 / 2**31), lon * (180.0 / 2**31)),
                'timestamp': str(timestamp).split('+')[0] if timestamp else "N/A", # 시간 포맷 간소화
                'speed': float(speed) if speed is not None else 0.0
            })

    if not raw_points:
        print("유효한 GPS 데이터가 없습니다.")
        return

    # 2. 속도 필터링 (이동 평균 필터 - Moving Average Filter)
    # 속도가 0으로 튀는 노이즈를 잡기 위해 창 크기 5의 앞뒤 평균 적용
    window_size = 5
    points = []
    for i in range(len(raw_points)):
        start = max(0, i - window_size // 2)
        end = min(len(raw_points), i + window_size // 2 + 1)
        avg_speed = sum(p['speed'] for p in raw_points[start:end]) / (end - start)
        
        # 보정된 데이터를 새 리스트에 저장 (km/h 변환 포함)
        p = raw_points[i].copy()
        p['speed_kmh'] = round(avg_speed * 3.6, 1)
        points.append(p)

    # 좌표 최소/최대값 계산 (화면 줌 조정을 위함)
    lats, lons = zip(*[p['coord'] for p in points])
    min_lat, max_lat = min(lats), max(lats)
    min_lon, max_lon = min(lons), max(lons)
    
    # 여백 추가 (10%)
    lat_range = max_lat - min_lat if max_lat != min_lat else 0.001
    lon_range = max_lon - min_lon if max_lon != min_lon else 0.001
    padding = 0.1
    min_lat -= lat_range * padding
    max_lat += lat_range * padding
    min_lon -= lon_range * padding
    max_lon += lon_range * padding
    
    current_lat_range = max_lat - min_lat
    current_lon_range = max_lon - min_lon

    def to_pixel(lat, lon):
        x = int(((lon - min_lon) / current_lon_range) * WIDTH)
        y = int(HEIGHT - ((lat - min_lat) / current_lat_range) * HEIGHT)
        return x, y

    # 비디오 라이터 설정
    fourcc = cv2.VideoWriter_fourcc(*'mp4v')
    out = cv2.VideoWriter(OUTPUT_FILE, fourcc, FPS, (WIDTH, HEIGHT))

    print(f"영상 제작 시작: {OUTPUT_FILE} ({len(points)} 프레임)")

    for i in range(len(points)):
        frame = np.zeros((HEIGHT, WIDTH, 3), dtype=np.uint8)
        
        # 전체 경로 그리기 (배경 회색 선)
        for j in range(1, len(points)):
            p1 = to_pixel(*points[j-1]['coord'])
            p2 = to_pixel(*points[j]['coord'])
            cv2.line(frame, p1, p2, (60, 60, 60), 1)

        # 실시간 진행 경로 (오렌지색 선)
        for j in range(1, i + 1):
            p1 = to_pixel(*points[j-1]['coord'])
            p2 = to_pixel(*points[j]['coord'])
            cv2.line(frame, p1, p2, (0, 165, 255), 2)

        # 현재 위치 (빨간 점)
        curr_p = to_pixel(*points[i]['coord'])
        cv2.circle(frame, curr_p, 5, (0, 0, 255), -1)

        # 상단 정보 통합 표시 (Time & Speed 한 줄)
        info_text = f"{points[i]['timestamp']} | {points[i]['speed_kmh']} km/h"
        
        # 텍스트 배경 (가독성 향상)
        cv2.rectangle(frame, (0, 0), (WIDTH, 30), (30, 30, 30), -1)
        cv2.putText(frame, info_text, (10, 20), 
                    cv2.FONT_HERSHEY_SIMPLEX, 0.4, (255, 255, 255), 1, cv2.LINE_AA)

        out.write(frame)
        
        if (i + 1) % 100 == 0:
            print(f"진행 중: {i+1}/{len(points)}")

    out.release()
    print(f"영상 제작 완료: {OUTPUT_FILE}")

if __name__ == "__main__":
    create_video()
