import csv
import sys
try:
    from fitparse import FitFile
except ImportError:
    print("fitparse 모듈이 설치되어 있지 않습니다. 'pip install fitparse'를 실행해주세요.")
    sys.exit(1)

def fit_to_csv(fit_filepath, csv_filepath):
    print(f"분석 중: {fit_filepath}")
    try:
        fitfile = FitFile(fit_filepath)
    except Exception as e:
        print(f"FIT 파일 읽기 오류: {e}")
        return

    records = []
    
    # 'record' 타입의 메시지만 추출 (보통 시간, 심박수, GPS 등 운동 데이터)
    for record in fitfile.get_messages('record'):
        record_data = {}
        for record_data_field in record:
            val = record_data_field.value
            name = record_data_field.name
            
            # 위도/경도(semicircles)를 Degrees로 변환 (FIT 파일 표준 규칙)
            if name in ['position_lat', 'position_long'] and val is not None:
                val = val * (180.0 / 2**31)
            
            # 숫자 데이터인 경우 소수점 첫째 자리까지 반올림 (사용자 요청: 로봇 제어 루프 필터링 방식)
            if isinstance(val, (int, float)) and not isinstance(val, bool):
                val = round(float(val), 1)
                
            record_data[name] = val
        records.append(record_data)

    if not records:
        print("FIT 파일에서 'record' 데이터를 찾을 수 없습니다.")
        return

    # CSV 헤더 생성을 위해 모든 필드명 수집
    headers = set()
    for record in records:
        headers.update(record.keys())
    
    headers = sorted(list(headers))

    try:
        with open(csv_filepath, 'w', newline='', encoding='utf-8') as f:
            writer = csv.DictWriter(f, fieldnames=headers)
            writer.writeheader()
            for record in records:
                writer.writerow(record)
        print(f"성공적으로 변환 완료: {csv_filepath}")
    except Exception as e:
        print(f"CSV 파일 쓰기 오류: {e}")

if __name__ == "__main__":
    fit_filename = r"C:\DTWorkSpace\AntiGravityProject\ZEEPLOG\Zepp20260408070313.fit"
    csv_filename = r"C:\DTWorkSpace\AntiGravityProject\ZEEPLOG\Zepp20260408070313.csv"
    fit_to_csv(fit_filename, csv_filename)
