using System.IO;
using ZeeplogWpf.Models;

namespace ZeeplogWpf.Parsers;

/// <summary>
/// Minimal FIT binary parser — Spec: FIT Protocol 21.x
///
/// Bug history:
///  1. Compressed timestamp records (header bit7=1) do not contain field 253.
///     Fix: reconstruct timestamp from 5-bit offset + lastTimestampRaw.
///
///  2. Zepp/Huami FIT files embed developer-data fields (fit.huami.com).
///     Definition messages with has_dev=1 are followed by dev-field definitions
///     that describe extra bytes appended to every subsequent DATA message of
///     that local type.  If those dev bytes are not skipped, the stream position
///     drifts and every subsequent message is misread.
///     Fix: store DevDataTotalSize per local-message-type, skip in ProcessData.
/// </summary>
public static class FitParser
{
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
    }

    private sealed class MsgDefEntry
    {
        public int GlobalMsgNumber;
        public bool BigEndian;
        public List<FieldDefEntry> Fields = [];
        /// <summary>
        /// Total byte count of developer-data fields appended to each DATA
        /// message of this local type.  Must be skipped after regular fields.
        /// </summary>
        public int DevDataTotalSize;
    }

    // ── Core parser ─────────────────────────────────────────────────────────

    private static void ParseFile(string filePath,
        List<DataPoint>? videoOut, List<Dictionary<string, object?>>? csvOut)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);

        // ── File header ──────────────────────────────────────────────────────
        byte headerSize = reader.ReadByte();
        if (headerSize < 12) throw new InvalidDataException("FIT 파일 헤더가 올바르지 않습니다.");
        reader.ReadByte();    // protocol version
        reader.ReadUInt16();  // profile version
        uint dataSize = reader.ReadUInt32();
        byte[] magic = reader.ReadBytes(4);
        if (magic[0] != '.' || magic[1] != 'F' || magic[2] != 'I' || magic[3] != 'T')
            throw new InvalidDataException("FIT 파일이 아닙니다.");
        if (headerSize > 12)
            reader.ReadBytes(headerSize - 12); // skip header CRC etc.

        long dataEnd = stream.Position + dataSize;
        var defs = new Dictionary<int, MsgDefEntry>(); // local type → definition

        // Last full FIT timestamp seen in a normal field 253.
        // Used to reconstruct timestamps for compressed-timestamp records.
        uint lastTimestampRaw = 0;

        while (stream.Position < dataEnd && stream.Position < stream.Length - 1)
        {
            byte hdr = reader.ReadByte();
            bool compressed = (hdr & 0x80) != 0;

            if (compressed)
            {
                // ── Compressed timestamp record ──────────────────────────────
                // header: [1 | lt(2) | time_offset(5)]
                // The 5-bit time_offset is the lower 5 bits of the current
                // timestamp in seconds; reconstruct by OR-ing with upper bits.
                int  lt         = (hdr >> 5) & 0x03;
                uint timeOffset = (uint)(hdr & 0x1F);

                uint actualTs = (lastTimestampRaw & 0xFFFFFFE0u) | timeOffset;
                if (actualTs < lastTimestampRaw)
                    actualTs += 0x20u; // 5-bit rollover (32 s)
                lastTimestampRaw = actualTs;

                if (defs.TryGetValue(lt, out var d))
                    ProcessData(reader, d, videoOut, csvOut,
                        FitEpoch.AddSeconds(actualTs), ref lastTimestampRaw);
                continue;
            }

            bool isDef  = (hdr & 0x40) != 0;
            bool hasDev = (hdr & 0x20) != 0;
            int  local  = hdr & 0x0F;

            if (isDef)
            {
                // ── Definition message ────────────────────────────────────────
                reader.ReadByte(); // reserved
                byte arch = reader.ReadByte();
                bool bigEndian = arch == 1;

                ushort globalNum = bigEndian
                    ? (ushort)((reader.ReadByte() << 8) | reader.ReadByte())
                    : reader.ReadUInt16();

                byte numFields = reader.ReadByte();
                var def = new MsgDefEntry { GlobalMsgNumber = globalNum, BigEndian = bigEndian };

                for (int i = 0; i < numFields; i++)
                {
                    byte fn = reader.ReadByte();
                    byte fs = reader.ReadByte();
                    reader.ReadByte(); // base type (not needed for parsing)
                    def.Fields.Add(new FieldDefEntry { FieldNumber = fn, Size = fs });
                }

                if (hasDev)
                {
                    // Developer-field definitions: each is 3 bytes
                    // (field_num, size, developer_data_index).
                    // We don't interpret them but MUST accumulate their sizes so
                    // the matching DATA messages can be read correctly.
                    byte nd = reader.ReadByte();
                    for (int i = 0; i < nd; i++)
                    {
                        reader.ReadByte();                    // field number
                        def.DevDataTotalSize += reader.ReadByte(); // size — accumulate!
                        reader.ReadByte();                    // developer data index
                    }
                }

                defs[local] = def;
            }
            else
            {
                // ── Data message ─────────────────────────────────────────────
                if (!defs.TryGetValue(local, out var def)) break;
                ProcessData(reader, def, videoOut, csvOut, null, ref lastTimestampRaw);
            }
        }
    }

    private static void ProcessData(
        BinaryReader reader,
        MsgDefEntry  def,
        List<DataPoint>? videoOut,
        List<Dictionary<string, object?>>? csvOut,
        DateTime? compressedTs,       // pre-computed ts for compressed-header records
        ref uint  lastTimestampRaw)
    {
        bool isRecord = def.GlobalMsgNumber == MsgRecord;

        double? lat = null, lon = null, speedMs = null, enhancedSpeedMs = null;
        int?    hr  = null;
        DateTime? ts = null;
        Dictionary<string, object?>? csvRow = (csvOut != null && isRecord) ? [] : null;

        // ── Regular fields ───────────────────────────────────────────────────
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

            bool knownField = FieldDefs.TryGetValue(f.FieldNumber, out var fd);
            bool isSigned   = knownField && fd.IsSigned;
            long sval = isSigned ? f.Size switch
            {
                1 => (sbyte)raw[0],
                2 => BitConverter.ToInt16(raw, 0),
                4 => (long)BitConverter.ToInt32(raw, 0),
                _ => uval
            } : uval;

            double value = knownField ? sval * fd.Scale : uval;
            if (FieldOffsets.TryGetValue(f.FieldNumber, out double off))
                value += off;

            switch (f.FieldNumber)
            {
                case 253:
                    lastTimestampRaw = (uint)uval;
                    ts = FitEpoch.AddSeconds(uval);
                    csvRow?.TryAdd("timestamp", ts.Value.ToString("yyyy-MM-ddTHH:mm:ss"));
                    continue;
                case 0:  lat = value; break;
                case 1:  lon = value; break;
                case 3:  hr  = (int)uval; break;
                case 6:  speedMs         = value; break;
                case 73: enhancedSpeedMs = value; break;
            }

            if (csvRow != null)
                csvRow[FieldName(f.FieldNumber)] = Math.Round(value, 6);
        }

        // ── Developer-data fields (skip bytes) ───────────────────────────────
        // DATA messages whose definition had has_dev=1 append extra bytes after
        // the regular fields.  They must be consumed to keep stream in sync.
        if (def.DevDataTotalSize > 0)
            reader.ReadBytes(def.DevDataTotalSize);

        // ── Emit results ─────────────────────────────────────────────────────
        // For compressed-timestamp records, field 253 is absent; fall back.
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
