namespace ZeeplogWpf.Models;

public class DataPoint
{
    public DateTime Timestamp { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Speed { get; set; }       // m/s (raw from file)
    public int HeartRate { get; set; }

    // Populated during video frame interpolation
    public double SpeedKmh { get; set; }
    public string ElapsedStr { get; set; } = "";
}
