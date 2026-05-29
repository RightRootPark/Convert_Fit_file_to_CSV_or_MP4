using System.Xml.Linq;
using ZeeplogWpf.Models;

namespace ZeeplogWpf.Parsers;

public static class TcxParser
{
    private static readonly XNamespace Ns  = "http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2";
    private static readonly XNamespace Ns3 = "http://www.garmin.com/xmlschemas/ActivityExtension/v2";

    public static List<DataPoint> Parse(string filePath)
    {
        var pts = new List<DataPoint>();
        var doc = XDocument.Load(filePath);

        foreach (var tp in doc.Descendants(Ns + "Trackpoint"))
        {
            var pos = tp.Element(Ns + "Position");
            if (pos == null) continue;

            if (!double.TryParse(pos.Element(Ns + "LatitudeDegrees")?.Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double lat)) continue;
            if (!double.TryParse(pos.Element(Ns + "LongitudeDegrees")?.Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double lon)) continue;

            var timeStr = tp.Element(Ns + "Time")?.Value?.Replace("Z", "");
            if (!DateTime.TryParseExact(timeStr?[..19], "yyyy-MM-ddTHH:mm:ss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime ts)) continue;

            double speed = 0;
            var speedNode = tp.Descendants(Ns3 + "Speed").FirstOrDefault();
            if (speedNode != null)
                double.TryParse(speedNode.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out speed);

            int hr = 0;
            var hrNode = tp.Element(Ns + "HeartRateBpm")?.Element(Ns + "Value");
            if (hrNode != null)
                int.TryParse(hrNode.Value, out hr);

            pts.Add(new DataPoint
            {
                Timestamp = ts.ToUniversalTime(),
                Latitude = lat,
                Longitude = lon,
                Speed = speed,
                HeartRate = hr
            });
        }

        return pts;
    }
}
