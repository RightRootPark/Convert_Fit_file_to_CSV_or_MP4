using System.Xml.Linq;
using ZeeplogWpf.Models;

namespace ZeeplogWpf.Parsers;

public static class GpxParser
{
    private static readonly XNamespace Ns  = "http://www.topografix.com/GPX/1/1";
    private static readonly XNamespace Ns3 = "http://www.garmin.com/xmlschemas/TrackPointExtension/v1";

    public static List<DataPoint> Parse(string filePath)
    {
        var pts = new List<DataPoint>();
        var doc = XDocument.Load(filePath);

        foreach (var trkpt in doc.Descendants(Ns + "trkpt"))
        {
            if (!double.TryParse(trkpt.Attribute("lat")?.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double lat)) continue;
            if (!double.TryParse(trkpt.Attribute("lon")?.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double lon)) continue;

            var timeStr = trkpt.Element(Ns + "time")?.Value?.Replace("Z", "");
            if (!DateTime.TryParseExact(timeStr?[..19], "yyyy-MM-ddTHH:mm:ss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime ts)) continue;

            double speed = 0;
            var speedNode = trkpt.Descendants(Ns3 + "speed").FirstOrDefault();
            if (speedNode != null)
                double.TryParse(speedNode.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out speed);

            int hr = 0;
            var hrNode = trkpt.Descendants(Ns3 + "hr").FirstOrDefault();
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
