using System.IO;
using ZeeplogWpf.Models;

namespace ZeeplogWpf.Parsers;

/// <summary>
/// Minimal FIT binary parser.
/// Spec: FIT Protocol 21.x — record message (global #20) fields.
/// </summary>
public static class FitParser
{
    // FIT epoch: 1989-12-31 00:00:00 UTC  →  Unix offset = 631,065,600 s
    private static readonly DateTime FitEpoch = new(1989, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    private const int MsgRecord = 20;

    // (name, scale, isSigned)
    private static readonly Dictionary<byte, (string Name, double Scale, bool IsSigned)> FieldDefs = new()
    {
        { 253, ("timestamp",           1.0,                         false) },
        { 0,   ("position_lat",        180.0 / 2147483648.0,        true)  },
        { 1,   ("position_long",       180.0 / 2147483648.0,        true)  },
        { 2,   ("altitude",            0.2,                         false) },
        { 3,   ("heart_rate",          1.0,                         false) },
        { 4,   ("cadence",             1.0,                         false) },
        { 5,   ("distance",            0.01,                        false) },
        { 6,   ("speed",               0.001,                       false) },
        { 7,   ("power",               1.0,                         false) },
        { 9,   ("grade",               0.01,                        true)  },
        { 13,  ("temperature",         1.0,                         true)  },
        { 29,  ("accumulated_power",   1.0,                         false) },
        { 31,  ("gps_accuracy",        1.0,                         false) },
        { 32,  ("vertical_speed",      0.001,                       true)  },
        { 33,  ("calories",            1.0,                         false) },
        { 39,  ("vertical_oscillation",0.1,                         false) },
        { 40,  ("stance_time_percent", 0.01,                        false) },
        { 41,  ("stance_time",         0.1,                         false) },
        { 53,  ("fractional_cadence",  1.0 / 128.0,                 false) },
        { 73,  ("enhanced_speed",      0.001,                       false) },
        { 78,  ("enhanced_altitude",   0.2,                         false) },
        { 83,  ("vertical_ratio",      0.01,                        false) },
        { 84,  ("stance_time_balance", 0.01,                        false) },
        { 85,  ("step_length",         0.1,                         false) },
    };

    // Additive offset applied after scale (altitude, enhanced_altitude)
    private static readonly Dictionary<byte, double> FieldOffsets = new()
    {
        { 2,  -500.0 },
        { 78, -500.0 },
    };

    // ── Public API ──────────────────────────────────────────────────────────

    /// <summary>Returns all record fields; used for CSV export (FIT only).</summary>
    public static List<Dictionary<string, object?>> ParseForCsv(string filePath)
    {
        var rows = new List<Dictionary<string, object?>>();
        ParseFile(filePath, null, rows);
        return rows;
    }

    /// <summary>Returns lat/lon/speed/hr/timestamp points; used for video generation.</summary>
    public static List<DataPoint> ParseForVideo(string filePath)
    {
        var pts = new List<DataPoint>();
        ParseFile(filePath, pts, null);
        return pts;
    }

    // ── Internal structures ─────────────────────────────────────────────────

    private sealed class FieldDefEntry
    {
        public byte FieldNumber;
        public byte Size;
        public byte BaseType;
    }

    private sealed class MsgDefEntry
    {
        public int GlobalMsgNumber;
        public bool BigEndian;
        public List<FieldDefEntry> Fields = [];
    }

    // ── Core parser ─────────────────────────────────────────────────────────

    private static void ParseFile(string filePath, List<DataPoint>? videoOut, List<Dictionary<string, object?>>? csvOut)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);

        // File header
        byte headerSize = reader.ReadByte();
        if (headerSize < 12) throw new InvalidDataException("FIT 파일 헤더가 올바르지 않습니다.");
        reader.ReadByte();   // protocol version
        reader.ReadUInt16(); // profile version
        uint dataSize = reader.ReadUInt32();
        byte[] magic = reader.ReadBytes(4);
        if (magic[0] != '.' || magic[1] != 'F' || magic[2] != 'I' || magic[3] != 'T')
            throw new InvalidDataException("FIT 파일이 아닙니다.");
        if (headerSize > 12)
            reader.ReadBytes(headerSize - 12);

        long dataEnd = stream.Position + dataSize;
        var defs = new Dictionary<int, MsgDefEntry>();

        while (stream.Position < dataEnd && stream.Position < stream.Length - 1)
        {
            byte hdr = reader.ReadByte();
            bool compressed = (hdr & 0x80) != 0;

            if (compressed)
            {
                int lt = (hdr >> 5) & 0x03;
                if (defs.TryGetValue(lt, out var d))
                    ProcessData(reader, d, videoOut, csvOut);
                continue;
            }

            bool isDef = (hdr & 0x40) != 0;
            bool hasDev = (hdr & 0x20) != 0;
            int local = hdr & 0x0F;

            if (isDef)
            {
                reader.ReadByte(); // reserved
                byte arch = reader.ReadByte();
                bool bigEndian = arch == 1;

                ushort globalNum = bigEndian
                    ? (ushort)((reader.ReadByte() << 8) | reader.ReadByte())
                    : reader.ReadUInt16();

                byte numFields = reader.ReadByte();
                var def = new MsgDefEntry { GlobalMsgNumber = globalNum, BigEndian = bigEndian };

                for (int i = 0; i < numFields; i++)
                    def.Fields.Add(new FieldDefEntry
                    {
                        FieldNumber = reader.ReadByte(),
                        Size = reader.ReadByte(),
                        BaseType = reader.ReadByte()
                    });

                if (hasDev)
                {
                    byte nd = reader.ReadByte();
                    for (int i = 0; i < nd; i++) reader.ReadBytes(3);
                }

                defs[local] = def;
            }
            else
            {
                if (!defs.TryGetValue(local, out var def)) break;
                ProcessData(reader, def, videoOut, csvOut);
            }
        }
    }

    private static void ProcessData(BinaryReader reader, MsgDefEntry def,
        List<DataPoint>? videoOut, List<Dictionary<string, object?>>? csvOut)
    {
        bool isRecord = def.GlobalMsgNumber == MsgRecord;

        double? lat = null, lon = null, speedMs = null, enhancedSpeedMs = null;
        int? hr = null;
        DateTime? ts = null;
        Dictionary<string, object?>? csvRow = (csvOut != null && isRecord) ? [] : null;

        foreach (var f in def.Fields)
        {
            byte[] raw = reader.ReadBytes(f.Size);
            if (!isRecord) continue;

            if (def.BigEndian && f.Size > 1)
            {
                byte[] copy = (byte[])raw.Clone();
                Array.Reverse(copy);
                raw = copy;
            }

            // Read as unsigned first
            long uval = f.Size switch
            {
                1 => raw[0],
                2 => BitConverter.ToUInt16(raw, 0),
                4 => (long)BitConverter.ToUInt32(raw, 0),
                _ => 0L
            };

            // Invalid sentinel check
            if (IsInvalid(f.FieldNumber, uval, f.Size))
            {
                csvRow?.TryAdd(FieldName(f.FieldNumber), null);
                continue;
            }

            // Signed re-interpretation
            bool isSigned = FieldDefs.TryGetValue(f.FieldNumber, out var fd) && fd.IsSigned;
            long sval = isSigned ? f.Size switch
            {
                1 => (sbyte)raw[0],
                2 => BitConverter.ToInt16(raw, 0),
                4 => (long)BitConverter.ToInt32(raw, 0),
                _ => uval
            } : uval;

            double value = fd.Name != null ? sval * fd.Scale : uval;
            if (FieldOffsets.TryGetValue(f.FieldNumber, out double off))
                value += off;

            // Capture specific fields
            switch (f.FieldNumber)
            {
                case 253: ts = FitEpoch.AddSeconds(uval); csvRow?.TryAdd("timestamp", ts.Value.ToString("yyyy-MM-ddTHH:mm:ss")); continue;
                case 0:   lat = value; break;
                case 1:   lon = value; break;
                case 3:   hr = (int)uval; break;
                case 6:   speedMs = value; break;
                case 73:  enhancedSpeedMs = value; break;
            }

            if (csvRow != null)
                csvRow[FieldName(f.FieldNumber)] = Math.Round(value, 6);
        }

        if (videoOut != null && isRecord && lat.HasValue && lon.HasValue && ts.HasValue)
            videoOut.Add(new DataPoint
            {
                Timestamp = ts.Value,
                Latitude = lat.Value,
                Longitude = lon.Value,
                Speed = enhancedSpeedMs ?? speedMs ?? 0.0,
                HeartRate = hr ?? 0
            });

        if (csvRow != null && csvRow.Count > 0)
            csvOut!.Add(csvRow);
    }

    private static bool IsInvalid(byte fieldNum, long uval, int size)
    {
        if (size == 1) return uval == 0xFF;
        if (size == 2) return uval == 0xFFFF;
        if (size == 4)
        {
            bool signed = FieldDefs.TryGetValue(fieldNum, out var d) && d.IsSigned;
            return signed ? uval == 0x7FFFFFFF : uval == 0xFFFFFFFFL;
        }
        return false;
    }

    private static string FieldName(byte num)
        => FieldDefs.TryGetValue(num, out var d) ? d.Name : $"field_{num}";
}
