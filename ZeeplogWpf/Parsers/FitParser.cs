using System.IO;
using ZeeplogWpf.Models;

namespace ZeeplogWpf.Parsers;

/// <summary>
/// Minimal FIT binary parser.
/// Spec: FIT Protocol 21.x — record message (global #20) fields.
///
/// Key fix: compressed timestamp records (header bit7=1) do NOT contain field 253.
/// The timestamp is reconstructed from the 5-bit time offset in the record header
/// and the last full timestamp seen in a normal record (field 253).
/// Most Zepp/Amazfit FIT files use this scheme for the majority of records.
/// </summary>
public static class FitParser
{
    // FIT epoch: 1989-12-31 00:00:00 UTC
    private static readonly DateTime FitEpoch = new(1989, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    private const int MsgRecord = 20;

    private static readonly Dictionary<byte, (string Name, double Scale, bool IsSigned)> FieldDefs = new()
    {
        { 253, ("timestamp",            1.0,                   false) },
        { 0,   ("position_lat",         180.0 / 2147483648.0,  true)  },
        { 1,   ("position_long",        180.0 / 2147483648.0,  true)  },
        { 2,   ("altitude",             0.2,                   false) },
        { 3,   ("heart_rate",           1.0,                   false) },
        { 4,   ("cadence",              1.0,                   false) },
        { 5,   ("distance",             0.01,                  false) },
        { 6,   ("speed",                0.001,                 false) },
        { 7,   ("power",                1.0,                   false) },
        { 9,   ("grade",                0.01,                  true)  },
        { 13,  ("temperature",          1.0,                   true)  },
        { 29,  ("accumulated_power",    1.0,                   false) },
        { 31,  ("gps_accuracy",         1.0,                   false) },
        { 32,  ("vertical_speed",       0.001,                 true)  },
        { 33,  ("calories",             1.0,                   false) },
        { 39,  ("vertical_oscillation", 0.1,                   false) },
        { 40,  ("stance_time_percent",  0.01,                  false) },
        { 41,  ("stance_time",          0.1,                   false) },
        { 53,  ("fractional_cadence",   1.0 / 128.0,           false) },
        { 73,  ("enhanced_speed",       0.001,                 false) },
        { 78,  ("enhanced_altitude",    0.2,                   false) },
        { 83,  ("vertical_ratio",       0.01,                  false) },
        { 84,  ("stance_time_balance",  0.01,                  false) },
        { 85,  ("step_length",          0.1,                   false) },
    };

    private static readonly Dictionary<byte, double> FieldOffsets = new()
    {
        { 2,  -500.0 },
        { 78, -500.0 },
    };

    // ── Public API ──────────────────────────────────────────────────────────

    public static List<Dictionary<string, object?>> ParseForCsv(string filePath)
    {
        var rows = new List<Dictionary<string, object?>>();
        ParseFile(filePath, null, rows);
        return rows;
    }

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

        // Tracks the last full FIT timestamp (uint32, seconds since FIT epoch).
        // Required to reconstruct timestamps from compressed timestamp records.
        uint lastTimestampRaw = 0;

        while (stream.Position < dataEnd && stream.Position < stream.Length - 1)
        {
            byte hdr = reader.ReadByte();
            bool compressed = (hdr & 0x80) != 0;

            if (compressed)
            {
                // Compressed timestamp header:
                //   bit 7   = 1 (compressed)
                //   bits 6-5 = local message type (0-3)
                //   bits 4-0 = 5-bit time offset (seconds, wraps every 32 s)
                int lt = (hdr >> 5) & 0x03;
                uint timeOffset = (uint)(hdr & 0x1F);

                // Reconstruct full timestamp: keep upper bits, replace lower 5 bits.
                // If the result would be earlier than the last known timestamp, a
                // 5-bit rollover (32 s) has occurred — add 32.
                uint actualTs = (lastTimestampRaw & 0xFFFFFFE0u) | timeOffset;
                if (actualTs < lastTimestampRaw)
                    actualTs += 0x20u;
                lastTimestampRaw = actualTs;

                if (defs.TryGetValue(lt, out var d))
                    ProcessData(reader, d, videoOut, csvOut,
                        FitEpoch.AddSeconds(actualTs), ref lastTimestampRaw);
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
                        Size        = reader.ReadByte(),
                        BaseType    = reader.ReadByte()
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
                ProcessData(reader, def, videoOut, csvOut, null, ref lastTimestampRaw);
            }
        }
    }

    /// <param name="compressedTs">
    /// Pre-computed timestamp for compressed-timestamp records (field 253 absent).
    /// Null for normal data records (timestamp comes from field 253 in data).
    /// </param>
    private static void ProcessData(
        BinaryReader reader,
        MsgDefEntry def,
        List<DataPoint>? videoOut,
        List<Dictionary<string, object?>>? csvOut,
        DateTime? compressedTs,
        ref uint lastTimestampRaw)
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

            long uval = f.Size switch
            {
                1 => raw[0],
                2 => BitConverter.ToUInt16(raw, 0),
                4 => (long)BitConverter.ToUInt32(raw, 0),
                _ => 0L
            };

            if (IsInvalid(f.FieldNumber, uval, f.Size))
            {
                csvRow?.TryAdd(FieldName(f.FieldNumber), null);
                continue;
            }

            bool isSigned = FieldDefs.TryGetValue(f.FieldNumber, out var fd) && fd.IsSigned;
            long sval = isSigned ? f.Size switch
            {
                1 => (sbyte)raw[0],
                2 => BitConverter.ToInt16(raw, 0),
                4 => (long)BitConverter.ToInt32(raw, 0),
                _ => uval
            } : uval;

            bool knownField = fd.Name != null;
            double value = knownField ? sval * fd.Scale : uval;
            if (FieldOffsets.TryGetValue(f.FieldNumber, out double off))
                value += off;

            switch (f.FieldNumber)
            {
                case 253:
                    // Normal (non-compressed) timestamp — update tracker for future compressed records
                    lastTimestampRaw = (uint)uval;
                    ts = FitEpoch.AddSeconds(uval);
                    csvRow?.TryAdd("timestamp", ts.Value.ToString("yyyy-MM-ddTHH:mm:ss"));
                    continue;
                case 0:  lat = value; break;
                case 1:  lon = value; break;
                case 3:  hr = (int)uval; break;
                case 6:  speedMs = value; break;
                case 73: enhancedSpeedMs = value; break;
            }

            if (csvRow != null)
                csvRow[FieldName(f.FieldNumber)] = Math.Round(value, 6);
        }

        // For compressed timestamp records, field 253 is absent — use pre-computed value
        ts ??= compressedTs;

        if (videoOut != null && isRecord && lat.HasValue && lon.HasValue && ts.HasValue)
            videoOut.Add(new DataPoint
            {
                Timestamp  = ts.Value,
                Latitude   = lat.Value,
                Longitude  = lon.Value,
                Speed      = enhancedSpeedMs ?? speedMs ?? 0.0,
                HeartRate  = hr ?? 0
            });

        if (csvRow != null && csvRow.Count > 0)
        {
            // For compressed timestamp rows that have no "timestamp" key yet, add it
            if (!csvRow.ContainsKey("timestamp") && compressedTs.HasValue)
                csvRow["timestamp"] = compressedTs.Value.ToString("yyyy-MM-ddTHH:mm:ss");
            csvOut!.Add(csvRow);
        }
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
