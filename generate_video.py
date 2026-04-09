import cv2
import numpy as np
import sys
import os
import xml.etree.ElementTree as ET
try:
    from fitparse import FitFile
except ImportError:
    pass # FIT 지원을 위해 fitparse 필요

# 400x400 비율 설정
WIDTH, HEIGHT = 400, 400
FPS = 10  # 초당 프레임 수

class DataParser:
    @staticmethod
    def parse_fit(filepath):
        from fitparse import FitFile
        try:
            fitfile = FitFile(filepath)
            points = []
            for record in fitfile.get_messages('record'):
                lat = record.get_value('position_lat')
                lon = record.get_value('position_long')
                timestamp = record.get_value('timestamp')
                speed = record.get_value('speed')
                if lat is not None and lon is not None:
                    points.append({
                        'coord': (lat * (180.0 / 2**31), lon * (180.0 / 2**31)),
                        'timestamp': str(timestamp).split('+')[0] if timestamp else "N/A",
                        'speed': float(speed) if speed is not None else 0.0
                    })
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
                timestamp = time_node.text if time_node is not None else "N/A"
                
                # 속도 정보 (보통 extensions 내에 위치)
                speed = 0.0
                speed_node = trkpt.find('.//ns3:speed', ns)
                if speed_node is not None:
                    speed = float(speed_node.text)
                
                points.append({
                    'coord': (lat, lon),
                    'timestamp': timestamp.replace('T', ' ').replace('Z', ''),
                    'speed': speed
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
                timestamp = time_node.text if time_node is not None else "N/A"
                
                speed = 0.0
                speed_node = tp.find('.//ns3:Speed', ns)
                if speed_node is not None:
                    speed = float(speed_node.text)
                
                points.append({
                    'coord': (lat, lon),
                    'timestamp': timestamp.replace('T', ' ').replace('Z', ''),
                    'speed': speed
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

    # 속도 필터링 및 km/h 변환
    window_size = 5
    points = []
    for i in range(len(raw_points)):
        start = max(0, i - window_size // 2)
        end = min(len(raw_points), i + window_size // 2 + 1)
        avg_speed = sum(p['speed'] for p in raw_points[start:end]) / (end - start)
        
        p = raw_points[i].copy()
        p['speed_kmh'] = round(avg_speed * 3.6, 1)
        points.append(p)

    # 출력파일명 설정
    output_file = os.path.join(os.path.dirname(input_path), 
                               os.path.splitext(os.path.basename(input_path))[0] + "route.mp4")

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

        # 상단 정보 바 및 텍스트
        cv2.rectangle(frame, (0, 0), (WIDTH, 30), (30, 30, 30), -1)
        info_text = f"{points[i]['timestamp']} | {points[i]['speed_kmh']} km/h"
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
