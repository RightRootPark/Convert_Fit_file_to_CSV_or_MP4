using System.IO;
using System.Text;
using ZeeplogWpf.Parsers;

namespace ZeeplogWpf.Services;

public static class CsvExporter
{
    public static string Export(string fitPath, IProgress<string> log)
    {
        log.Report($"FIT 파싱 중: {Path.GetFileName(fitPath)}");
        var records = FitParser.ParseForCsv(fitPath);

        if (records.Count == 0)
        {
            log.Report("record 데이터를 찾을 수 없습니다.");
            return "";
        }

        // Collect all unique headers (preserving insertion order across all rows)
        var headerSet = new LinkedList<string>();
        var headerIndex = new HashSet<string>();
        foreach (var row in records)
            foreach (var key in row.Keys)
                if (headerIndex.Add(key))
                    headerSet.AddLast(key);

        // Put timestamp first if present, then sort the rest
        var headers = headerSet
            .OrderBy(h => h == "timestamp" ? 0 : 1)
            .ThenBy(h => h)
            .ToList();

        string outPath = Path.ChangeExtension(fitPath, ".csv");
        // Avoid overwrite
        if (File.Exists(outPath))
        {
            int n = 1;
            string noExt = Path.Combine(Path.GetDirectoryName(fitPath)!,
                Path.GetFileNameWithoutExtension(fitPath));
            while (File.Exists(outPath))
                outPath = $"{noExt}({n++}).csv";
        }

        using var writer = new StreamWriter(outPath, false, System.Text.Encoding.UTF8);
        writer.WriteLine(string.Join(",", headers));

        foreach (var row in records)
        {
            var values = headers.Select(h =>
            {
                if (!row.TryGetValue(h, out var v) || v is null) return "";
                return v.ToString() ?? "";
            });
            writer.WriteLine(string.Join(",", values));
        }

        log.Report($"CSV 저장 완료: {Path.GetFileName(outPath)} ({records.Count}행)");
        return outPath;
    }
}
