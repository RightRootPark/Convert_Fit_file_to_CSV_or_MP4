import cv2
import numpy as np
import sys
import os
import xml.etree.ElementTree as ET
from datetime import datetime, timedelta
try:
    from fitparse import FitFile
except ImportError:
    pass # FIT 지원을 위해 fitparse 필요

# 400x400 비율 설정
WIDTH, HEIGHT = 400, 400
FPS = 60  # 재생 배속. 영상 속도는 현실 1초=1프레임 기준이므로, FPS 60은 60배속을 뜻합니다. 1배속(실시간)은 1로 변경하세요.

class DataParser:
    @staticmethod
    def parse_fit(filepath):
        from fitparse import FitFile
        try:
            fitfile = FitFile(filepath)
            points = []
            for record in fitfile.get_messages('record'):
                try:
                    lat = record.get_value('position_lat')
                    lon = record.get_value('position_long')
                    timestamp = record.get_value('timestamp')
                    speed = record.get_value('speed')
                    hr = record.get_value('heart_rate')
                    if lat is not None and lon is not None and timestamp is not None:
                        points.append({
                            'coord': (lat * (180.0 / 2**31), lon * (180.0 / 2**31)),
                            'timestamp': timestamp,
                            'speed': float(speed) if speed is not None else 0.0,
                            'hr': int(hr) if hr is not None else 0
                        })
                except Exception:
                    continue
            return points
        except Exception as e:
            print(f"FIT 파싱 오류: {e}")
            return []

    @staticmethod
    def parse_gpx(filepath):
        try:
            tree = ET.parse(filepath)
            root = tree.getroot()
            # 네임스페이스 정의
            ns = {'default': 'http://www.topografix.com/GPX/1/1',
                  'ns3': 'http://www.garmin.com/xmlschemas/TrackPointExtension/v1'}
            
            points = []
            for trkpt in root.findall('.//default:trkpt', ns):
                lat = float(trkpt.get('lat'))
                lon = float(trkpt.get('lon'))
                time_node = trkpt.find('default:time', ns)
                if time_node is None: continue
                timestamp_str = time_node.text.replace('Z', '')
                try:
                    timestamp = datetime.strptime(timestamp_str[:19], "%Y-%m-%dT%H:%M:%S")
                except:
                    continue
                
                # 속도 정보 (보통 extensions 내에 위치)
                speed = 0.0
                speed_node = trkpt.find('.//ns3:speed', ns)
                if speed_node is not None:
                    speed = float(speed_node.text)

                # 심박수 정보
                hr = 0
                hr_node = trkpt.find('.//ns3:hr', ns)
                if hr_node is not None:
                    hr = int(hr_node.text)
                
                points.append({
                    'coord': (lat, lon),
                    'timestamp': timestamp,
                    'speed': speed,
                    'hr': hr
                })
            return points
        except Exception as e:
            print(f"GPX 파싱 오류: {e}")
            return []

    @staticmethod
    def parse_tcx(filepath):
        try:
            tree = ET.parse(filepath)
            root = tree.getroot()
            ns = {'default': 'http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2',
                  'ns3': 'http://www.garmin.com/xmlschemas/ActivityExtension/v2'}
            
            points = []
            for tp in root.findall('.//default:Trackpoint', ns):
                pos = tp.find('default:Position', ns)
                if pos is None: continue
                
                lat = float(pos.find('default:LatitudeDegrees', ns).text)
                lon = float(pos.find('default:LongitudeDegrees', ns).text)
                time_node = tp.find('default:Time', ns)
                if time_node is None: continue
                timestamp_str = time_node.text.replace('Z', '')
                try:
                    timestamp = datetime.strptime(timestamp_str[:19], "%Y-%m-%dT%H:%M:%S")
                except:
                    continue
                
                speed = 0.0
                speed_node = tp.find('.//ns3:Speed', ns)
                if speed_node is not None:
                    speed = float(speed_node.text)

                hr = 0
                hr_node = tp.find('.//default:HeartRateBpm/default:Value', ns)
                if hr_node is not None:
                    hr = int(hr_node.text)
                
                points.append({
                    'coord': (lat, lon),
                    'timestamp': timestamp,
                    'speed': speed,
                    'hr': hr
                })
            return points
        except Exception as e:
            print(f"TCX 파싱 오류: {e}")
            return []

def create_video(input_path):
    print(f"파일 분석 중: {input_path}")
    ext = os.path.splitext(input_path)[1].lower()
    
    if ext == '.fit':
        raw_points = DataParser.parse_fit(input_path)
    elif ext == '.gpx':
        raw_points = DataParser.parse_gpx(input_path)
    elif ext == '.tcx':
        raw_points = DataParser.parse_tcx(input_path)
    else:
        print(f"지원하지 않는 확장자입니다: {ext}")
        return

    if not raw_points:
        print("유효한 데이터를 찾을 수 없습니다.")
        return

    # 타임스탬프 순으로 정렬 (혹시 모를 오류 방지)
    raw_points.sort(key=lambda x: x['timestamp'])

    # 속도 필터링 및 km/h 변환 (원본 데이터 기준)
    window_size = 5
    for i in range(len(raw_points)):
        start = max(0, i - window_size // 2)
        end = min(len(raw_points), i + window_size // 2 + 1)
        avg_speed = sum(p['speed'] for p in raw_points[start:end]) / (end - start)
        raw_points[i]['speed_kmh'] = round(avg_speed * 3.6, 1)

    # 1초 간격으로 프레임 보간 생성 (실제 시간 흐름 반영)
    start_time = raw_points[0]['timestamp']
    end_time = raw_points[-1]['timestamp']
    total_duration = int((end_time - start_time).total_seconds())

    # 데이터 유지(Hold) 및 타임아웃 관리용 변수
    current_hr = 0
    current_speed_kmh = 0.0
    hr_gap_timer = 0
    speed_gap_timer = 0
    HR_TIMEOUT = 20    # 심박수는 20초까지 유지
    SPEED_TIMEOUT = 10 # 속도는 10초까지 유지

    points = []
    curr_idx = 0
    for elapsed in range(total_duration + 1):
        target_time = start_time + timedelta(seconds=elapsed)
        
        # 이번 초에 새로운 유효 데이터(0보다 큰 값)가 들어왔는지 체크
        found_fresh_hr = False
        found_fresh_speed = False
        
        # 현재 target_time에 해당하는 원본 데이터들을 처리
        while curr_idx < len(raw_points) and raw_points[curr_idx]['timestamp'] <= target_time:
            p_raw = raw_points[curr_idx]
            
            # 유효한 심박수가 들어왔다면 업데이트 및 타이머 리셋
            if p_raw.get('hr', 0) > 0:
                current_hr = p_raw['hr']
                hr_gap_timer = 0
                found_fresh_hr = True
            
            # 유효한 속도가 들어왔다면 업데이트 및 타이머 리셋
            if p_raw.get('speed_kmh', 0) > 0:
                current_speed_kmh = p_raw['speed_kmh']
                speed_gap_timer = 0
                found_fresh_speed = True
                
            curr_idx += 1
            
        # 이번 1초(elapsed) 동안 새로운 비-제로 데이터를 못 만났다면 타이머 증가
        if not found_fresh_hr:
            hr_gap_timer += 1
        if not found_fresh_speed:
            speed_gap_timer += 1

        # 타임아웃 체크: 정해진 시간을 넘으면 0으로 리셋
        if hr_gap_timer > HR_TIMEOUT:
            current_hr = 0
        if speed_gap_timer > SPEED_TIMEOUT:
            current_speed_kmh = 0.0

        p = raw_points[max(0, curr_idx - 1)].copy()
        p['hr'] = current_hr
        p['speed_kmh'] = current_speed_kmh
        
        # 경과 시간을 HH:MM:SS 포맷으로 변환하여 추가
        hours, rem = divmod(elapsed, 3600)
        minutes, seconds = divmod(rem, 60)
        p['elapsed_str'] = f"{int(hours):02d}:{int(minutes):02d}:{int(seconds):02d}"
        
        points.append(p)

    # 출력파일명 설정 및 중복 방지 로직
    base_name = os.path.join(os.path.dirname(input_path), 
                             os.path.splitext(os.path.basename(input_path))[0] + "route")
    output_file = f"{base_name}.mp4"
    
    counter = 1
    while os.path.exists(output_file):
        output_file = f"{base_name}({counter}).mp4"
        counter += 1

    # 좌표 최소/최대값 및 여백
    lats, lons = zip(*[p['coord'] for p in points])
    min_lat, max_lat = min(lats), max(lats)
    min_lon, max_lon = min(lons), max(lons)
    lat_range = max(max_lat - min_lat, 0.001)
    lon_range = max(max_lon - min_lon, 0.001)
    padding = 0.1
    min_lat -= lat_range * padding; max_lat += lat_range * padding
    min_lon -= lon_range * padding; max_lon += lon_range * padding
    
    current_lat_range, current_lon_range = max_lat - min_lat, max_lon - min_lon

    def to_pixel(lat, lon):
        x = int(((lon - min_lon) / current_lon_range) * WIDTH)
        y = int(HEIGHT - ((lat - min_lat) / current_lat_range) * HEIGHT)
        return x, y

    fourcc = cv2.VideoWriter_fourcc(*'mp4v')
    out = cv2.VideoWriter(output_file, fourcc, FPS, (WIDTH, HEIGHT))

    # [최적화] 1. 배경 이미지 미리 생성 (전체 경로 회색 선)
    base_bg = np.zeros((HEIGHT, WIDTH, 3), dtype=np.uint8)
    for j in range(1, len(points)):
        p1 = to_pixel(*points[j-1]['coord'])
        p2 = to_pixel(*points[j]['coord'])
        cv2.line(base_bg, p1, p2, (60, 60, 60), 1)

    # [최적화] 2. 진행 경로 누적 이미지 (오렌지색 선)
    route_overlay = np.zeros((HEIGHT, WIDTH, 3), dtype=np.uint8)

    print(f"영상 제작 시작: {output_file} ({len(points)} 프레임)")
    for i in range(len(points)):
        # 누적 경로 업데이트 (이전 점과 현재 점 연결)
        if i > 0:
            p1 = to_pixel(*points[i-1]['coord'])
            p2 = to_pixel(*points[i]['coord'])
            cv2.line(route_overlay, p1, p2, (0, 165, 255), 2)

        # 프레임 합성 (배경 + 누적 경로)
        frame = cv2.addWeighted(base_bg, 1.0, route_overlay, 1.0, 0)

        # 현재 위치 표시 (빨간 점) 
        # (합성된 프레임 위에 그려야 매번 위치가 바뀜)
        curr_p = to_pixel(*points[i]['coord'])
        cv2.circle(frame, curr_p, 5, (0, 0, 255), -1)

        # 상단 정보 바 및 텍스트 (경과시간 / 속도 / 심박수)
        cv2.rectangle(frame, (0, 0), (WIDTH, 30), (30, 30, 30), -1)
        info_text = f"{points[i]['elapsed_str']} | {points[i]['speed_kmh']} km/h | {points[i]['hr']} bpm"
        cv2.putText(frame, info_text, (10, 20), cv2.FONT_HERSHEY_SIMPLEX, 0.4, (255, 255, 255), 1, cv2.LINE_AA)

        out.write(frame)
        if (i+1) % 500 == 0: print(f"진행: {i+1}/{len(points)}")

    out.release()
    print(f"작업 완료: {output_file}")

if __name__ == "__main__":
    if len(sys.argv) > 1:
        # 터미널에서 여러 인자를 한 번에 전달할 경우
        for arg in sys.argv[1:]:
            create_video(arg)
    else:
        # 인자 없이 실행 시, 파일 선택 대화상자 표시
        import tkinter as tk
        from tkinter import filedialog
        
        root = tk.Tk()
        root.withdraw() # 메인 윈도우 숨기기
        root.attributes('-topmost', True) # 다이얼로그를 최상단에 띄우기
        
        # 파일 여러 개 선택 가능하도록 askopenfilenames 사용
        filepaths = filedialog.askopenfilenames(
            title="운동 기록 파일 선택 (여러 개 선택 가능)",
            filetypes=[("운동 기록 파일 (FIT, GPX, TCX)", "*.fit *.gpx *.tcx"), ("All Files", "*.*")]
        )
        
        if not filepaths:
            print("선택된 파일이 없어 프로그램을 종료합니다.")
        else:
            for path in filepaths:
                create_video(path)
            print("선택한 모든 파일들의 변환 작업이 완료되었습니다!")
