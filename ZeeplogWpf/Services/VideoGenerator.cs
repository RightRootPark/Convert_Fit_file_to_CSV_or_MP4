using System.IO;
using OpenCvSharp;
using ZeeplogWpf.Models;
using ZeeplogWpf.Parsers;

namespace ZeeplogWpf.Services;

public static class VideoGenerator
{
    private const int Width  = 400;
    private const int Height = 400;

    private const int HrTimeout    = 20;   // seconds without fresh HR before zeroing
    private const int SpeedTimeout = 10;

    public static string Generate(
        string inputPath,
        int fps,
        IProgress<string> log,
        IProgress<(int Cur, int Total)> frameProgress,
        CancellationToken ct)
    {
        log.Report($"파일 분석 중: {Path.GetFileName(inputPath)}");

        var ext = Path.GetExtension(inputPath).ToLowerInvariant();
        List<DataPoint> rawPoints = ext switch
        {
            ".fit" => FitParser.ParseForVideo(inputPath),
            ".gpx" => GpxParser.Parse(inputPath),
            ".tcx" => TcxParser.Parse(inputPath),
            _      => throw new NotSupportedException($"지원하지 않는 확장자: {ext}")
        };

        if (rawPoints.Count == 0)
            throw new InvalidDataException("유효한 GPS 데이터를 찾을 수 없습니다.");

        rawPoints.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

        // Moving-average speed smoothing (window = 5, same as Python)
        const int window = 5;
        for (int i = 0; i < rawPoints.Count; i++)
        {
            int start = Math.Max(0, i - window / 2);
            int end   = Math.Min(rawPoints.Count, i + window / 2 + 1);
            double avg = rawPoints[start..end].Sum(p => p.Speed) / (end - start);
            rawPoints[i].SpeedKmh = Math.Round(avg * 3.6, 1);
        }

        // 1-second interpolated frame list
        var startTime = rawPoints[0].Timestamp;
        var endTime   = rawPoints[^1].Timestamp;
        int totalSecs = (int)(endTime - startTime).TotalSeconds;

        var frames = new List<DataPoint>(totalSecs + 1);
        int curIdx = 0;
        int hrTimer = 0, speedTimer = 0;
        int currentHr = 0;
        double currentSpeedKmh = 0.0;

        for (int elapsed = 0; elapsed <= totalSecs; elapsed++)
        {
            ct.ThrowIfCancellationRequested();
            var target = startTime.AddSeconds(elapsed);

            bool freshHr = false, freshSpeed = false;
            while (curIdx < rawPoints.Count && rawPoints[curIdx].Timestamp <= target)
            {
                var p = rawPoints[curIdx];
                if (p.HeartRate > 0) { currentHr = p.HeartRate; hrTimer = 0; freshHr = true; }
                if (p.SpeedKmh  > 0) { currentSpeedKmh = p.SpeedKmh; speedTimer = 0; freshSpeed = true; }
                curIdx++;
            }

            if (!freshHr)    hrTimer++;
            if (!freshSpeed) speedTimer++;
            if (hrTimer    > HrTimeout)    currentHr = 0;
            if (speedTimer > SpeedTimeout) currentSpeedKmh = 0.0;

            var snap = rawPoints[Math.Max(0, curIdx - 1)].Clone();
            snap.HeartRate = currentHr;
            snap.SpeedKmh  = currentSpeedKmh;

            int h = elapsed / 3600;
            int m = (elapsed % 3600) / 60;
            int s = elapsed % 60;
            snap.ElapsedStr = $"{h:D2}:{m:D2}:{s:D2}";
            frames.Add(snap);
        }

        // Output path — append "route", avoid overwrite
        string noExt = Path.Combine(
            Path.GetDirectoryName(inputPath)!,
            Path.GetFileNameWithoutExtension(inputPath) + "route");
        string outPath = noExt + ".mp4";
        int counter = 1;
        while (File.Exists(outPath))
            outPath = $"{noExt}({counter++}).mp4";

        // Coordinate → pixel mapping with 10 % padding
        var lats = frames.Select(p => p.Latitude);
        var lons = frames.Select(p => p.Longitude);
        double minLat = lats.Min(), maxLat = lats.Max();
        double minLon = lons.Min(), maxLon = lons.Max();
        double latRange = Math.Max(maxLat - minLat, 0.001);
        double lonRange = Math.Max(maxLon - minLon, 0.001);
        const double pad = 0.1;
        minLat -= latRange * pad; maxLat += latRange * pad;
        minLon -= lonRange * pad; maxLon += lonRange * pad;
        double finalLatRange = maxLat - minLat;
        double finalLonRange = maxLon - minLon;

        Point ToPixel(double lat, double lon) => new(
            (int)((lon - minLon) / finalLonRange * Width),
            Height - (int)((lat - minLat) / finalLatRange * Height));

        int fourcc = VideoWriter.FourCC('m', 'p', '4', 'v');
        using var writer = new VideoWriter(outPath, fourcc, fps, new Size(Width, Height));

        if (!writer.IsOpened())
            throw new InvalidOperationException($"VideoWriter를 열 수 없습니다: {outPath}");

        // Pre-render full-route gray background
        using var baseBg = new Mat(Height, Width, MatType.CV_8UC3, Scalar.All(0));
        for (int i = 1; i < frames.Count; i++)
        {
            var p1 = ToPixel(frames[i - 1].Latitude, frames[i - 1].Longitude);
            var p2 = ToPixel(frames[i].Latitude, frames[i].Longitude);
            Cv2.Line(baseBg, p1, p2, new Scalar(60, 60, 60), 1);
        }

        using var routeOverlay = new Mat(Height, Width, MatType.CV_8UC3, Scalar.All(0));

        log.Report($"영상 제작 시작: {Path.GetFileName(outPath)} ({frames.Count} 프레임)");

        for (int i = 0; i < frames.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (i > 0)
            {
                var p1 = ToPixel(frames[i - 1].Latitude, frames[i - 1].Longitude);
                var p2 = ToPixel(frames[i].Latitude, frames[i].Longitude);
                Cv2.Line(routeOverlay, p1, p2, new Scalar(0, 165, 255), 2);
            }

            using var frame = new Mat();
            Cv2.AddWeighted(baseBg, 1.0, routeOverlay, 1.0, 0, frame);

            var curr = ToPixel(frames[i].Latitude, frames[i].Longitude);
            Cv2.Circle(frame, curr, 5, new Scalar(0, 0, 255), -1);

            Cv2.Rectangle(frame, new Rect(0, 0, Width, 30), new Scalar(30, 30, 30), -1);
            string info = $"{frames[i].ElapsedStr} | {frames[i].SpeedKmh:F1} km/h | {frames[i].HeartRate} bpm";
            Cv2.PutText(frame, info, new Point(10, 20),
                HersheyFonts.HersheySimplex, 0.4, new Scalar(255, 255, 255), 1, LineTypes.AntiAlias);

            writer.Write(frame);

            if ((i + 1) % 500 == 0)
            {
                log.Report($"  진행: {i + 1}/{frames.Count}");
                frameProgress.Report((i + 1, frames.Count));
            }
        }

        frameProgress.Report((frames.Count, frames.Count));
        log.Report($"영상 저장 완료: {Path.GetFileName(outPath)}");
        return outPath;
    }
}

file static class DataPointEx
{
    public static DataPoint Clone(this DataPoint p) => new()
    {
        Timestamp  = p.Timestamp,
        Latitude   = p.Latitude,
        Longitude  = p.Longitude,
        Speed      = p.Speed,
        HeartRate  = p.HeartRate,
        SpeedKmh   = p.SpeedKmh,
        ElapsedStr = p.ElapsedStr
    };
}
