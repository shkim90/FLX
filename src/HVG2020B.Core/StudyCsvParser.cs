using System.Globalization;

namespace HVG2020B.Core;

/// <summary>
/// Parses study CSV files produced by StudyItem recording.
/// CSV format: timestamp_iso,device_id,pressure_torr
/// </summary>
public static class StudyCsvParser
{
    public record struct CsvRow(DateTimeOffset Timestamp, string DeviceId, double PressureTorr);

    public record ParsedStudyData(List<string> DeviceIds, Dictionary<string, List<CsvRow>> RowsByDevice);

    /// <summary>
    /// Parses a study CSV and groups rows by device.
    /// </summary>
    public static ParsedStudyData Parse(string csvFilePath)
    {
        var rowsByDevice = new Dictionary<string, List<CsvRow>>();
        var deviceOrder = new List<string>();

        using var reader = new StreamReader(csvFilePath);
        var headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine)) 
            return new ParsedStudyData(deviceOrder, rowsByDevice);

        // 첫 번째 줄(헤더)을 읽어서 장비 목록 파악
        var headers = headerLine.Split(',');
        for (int i = 1; i < headers.Length; i++)
        {
            var deviceId = headers[i];
            deviceOrder.Add(deviceId);
            rowsByDevice[deviceId] = new List<CsvRow>();
        }

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            
            var parts = line.Split(',');
            if (parts.Length < headers.Length) continue;

            if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
                continue;

            // 각 열(장비)의 데이터를 파싱
            for (int i = 1; i < headers.Length; i++)
            {
                var pStr = parts[i];
                if (!string.IsNullOrWhiteSpace(pStr) && double.TryParse(pStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var pressure))
                {
                    var deviceId = deviceOrder[i - 1];
                    rowsByDevice[deviceId].Add(new CsvRow(timestamp, deviceId, pressure));
                }
            }
        }

        return new ParsedStudyData(deviceOrder, rowsByDevice);
    }

    /// <summary>
    /// Extracts time (seconds from start) and pressure (Torr) arrays for a single device.
    /// </summary>
    public static (double[] TimeSeconds, double[] PressureTorr) ExtractDeviceData(List<CsvRow> rows)
    {
        if (rows.Count == 0)
            return (Array.Empty<double>(), Array.Empty<double>());

        var sorted = rows.OrderBy(r => r.Timestamp).ToList();
        var startTime = sorted[0].Timestamp;

        var timeSeconds = new double[sorted.Count];
        var pressureTorr = new double[sorted.Count];

        for (var i = 0; i < sorted.Count; i++)
        {
            timeSeconds[i] = (sorted[i].Timestamp - startTime).TotalSeconds;
            pressureTorr[i] = sorted[i].PressureTorr;
        }

        return (timeSeconds, pressureTorr);
    }
}
