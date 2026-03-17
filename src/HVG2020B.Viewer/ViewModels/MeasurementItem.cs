using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using HVG2020B.Core;
using HVG2020B.Core.Models;

namespace HVG2020B.Viewer.ViewModels;

public partial class MeasurementItem : ObservableObject, IDisposable
{
    private StreamWriter? _csvWriter;
    private bool _disposed;
    private readonly string _studyFolderPath;

    // 가로 쓰기 및 타이머 기록을 위한 변수
    private readonly Dictionary<string, string> _latestPressures = new();
    private DispatcherTimer? _recordingTimer;

    public MeasurementItem(MeasurementRecord record, string studyFolderPath)
    {
        Record = record;
        _studyFolderPath = studyFolderPath;
        RecordedSampleCount = record.RecordedSampleCount;

        State = Enum.TryParse<MeasurementState>(record.State, out var s)
            ? s : MeasurementState.Ready;

        if (State == MeasurementState.Done && !string.IsNullOrEmpty(record.CsvFileName))
        {
            var fullPath = Path.Combine(studyFolderPath, record.CsvFileName);
            if (!File.Exists(fullPath))
                IsBroken = true;
        }
    }

    public MeasurementRecord Record { get; }

    public string MeasurementId => Record.MeasurementId;
    public string Label => Record.Label;
    public string DevicesSummary => string.Join(", ", Record.DeviceIds);

    public string? CsvFilePath => string.IsNullOrEmpty(Record.CsvFileName)
        ? null
        : Path.Combine(_studyFolderPath, Record.CsvFileName);

    [ObservableProperty]
    private MeasurementState _state = MeasurementState.Ready;

    [ObservableProperty]
    private int _recordedSampleCount;

    [ObservableProperty]
    private bool _isBroken;

    public ObservableCollection<FluxAnalysisResult> AnalysisResults { get; } = new();

    public void StartRecording()
    {
        if (State != MeasurementState.Ready) return;

        if (!Directory.Exists(_studyFolderPath))
            Directory.CreateDirectory(_studyFolderPath);

        Record.CsvFileName = $"{MeasurementId}.csv";
        var csvPath = Path.Combine(_studyFolderPath, Record.CsvFileName);
        _csvWriter = new StreamWriter(csvPath, false, Encoding.UTF8);
        
        // 장비 ID 뒤에 단위(/mbar)를 붙여서 헤더 생성 (Torr를 원하시면 /Torr로 변경)
        var header = "timestamp_iso," + string.Join(",", Record.DeviceIds.Select(id => $"{id}/mbar"));
        _csvWriter.WriteLine(header);

        Record.StartTime = DateTimeOffset.Now;
        RecordedSampleCount = 0;
        
        // 상태 초기화
        _latestPressures.Clear();
        State = MeasurementState.Recording;

        // 250ms마다 무조건 기록하는 독립 타이머 시작
        _recordingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _recordingTimer.Tick += RecordingTimer_Tick;
        _recordingTimer.Start();
    }

    private void RecordingTimer_Tick(object? sender, EventArgs e)
    {
        if (State != MeasurementState.Recording || _csvWriter == null) return;

        var now = DateTimeOffset.Now;
        // 밀리초 3자리까지 깔끔하게 포맷팅
        var timestampIso = now.ToString("yyyy-MM-dd HH:mm:ss.000", CultureInfo.InvariantCulture);
        var sb = new StringBuilder(timestampIso);

        foreach (var dev in Record.DeviceIds)
        {
            sb.Append(",");
            if (_latestPressures.TryGetValue(dev, out var val))
                sb.Append(val); // 정상 값이거나 "Err"
        }

        _csvWriter.WriteLine(sb.ToString());
        RecordedSampleCount++;

        // 1초(4번)마다 디스크에 저장(Flush)
        if (RecordedSampleCount % 4 == 0) 
            _csvWriter.Flush();
    }

    public void StopRecording()
    {
        if (State != MeasurementState.Recording) return;

        // 타이머 중지
        _recordingTimer?.Stop();
        _recordingTimer = null;

        _csvWriter?.Flush();
        _csvWriter?.Close();
        _csvWriter?.Dispose();
        _csvWriter = null;

        Record.EndTime = DateTimeOffset.Now;
        Record.RecordedSampleCount = RecordedSampleCount;
        Record.State = "Done";
        State = MeasurementState.Done;
    }

    // 통신 시 들어오는 값을 캐싱하는 함수
    public void UpdateReading(string deviceId, GaugeReading reading)
    {
        if (State != MeasurementState.Recording) return;
        if (!Record.DeviceIds.Contains(deviceId)) return;

        _latestPressures[deviceId] = reading.PressureTorr.ToString("G", CultureInfo.InvariantCulture);
    }

    // 장비 연결 끊김 시 "Err"로 표기하는 함수
    public void SetDeviceError(string deviceId)
    {
        if (State != MeasurementState.Recording) return;
        if (!Record.DeviceIds.Contains(deviceId)) return;

        _latestPressures[deviceId] = "Err";
    }

    public void AddAnalysisResult(FluxAnalysisResult result)
    {
        result.AnalysisId = Record.LatestAnalysisId + 1;
        Record.LatestAnalysisId = result.AnalysisId;
        result.MeasurementRecordId = MeasurementId;
        AnalysisResults.Insert(0, result);
    }

    public void SyncToRecord()
    {
        Record.State = State == MeasurementState.Recording ? "Done" : State.ToString();
        Record.RecordedSampleCount = RecordedSampleCount;
        Record.AnalysisResults = AnalysisResults.ToList();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _recordingTimer?.Stop();
        _csvWriter?.Flush();
        _csvWriter?.Dispose();
        _csvWriter = null;
    }
}
// using System.Collections.ObjectModel;
// using System.Globalization;
// using System.IO;
// using System.Text;
// using CommunityToolkit.Mvvm.ComponentModel;
// using HVG2020B.Core;
// using HVG2020B.Core.Models;
// using System.Windows.Threading;

// namespace HVG2020B.Viewer.ViewModels;

// public partial class MeasurementItem : ObservableObject, IDisposable
// {
//     private StreamWriter? _csvWriter;
//     private readonly Dictionary<string, int> _logTickCounters = new();
//     private bool _disposed;
//     private readonly string _studyFolderPath;

//     // ✅ 가로 쓰기를 위해 추가된 변수들
//     private DateTimeOffset _lastWriteTime = DateTimeOffset.MinValue;
//     private readonly Dictionary<string, double> _latestPressures = new();
//     public MeasurementItem(MeasurementRecord record, string studyFolderPath)
//     {
//         Record = record;
//         _studyFolderPath = studyFolderPath;
//         RecordedSampleCount = record.RecordedSampleCount;

        
//         State = Enum.TryParse<MeasurementState>(record.State, out var s)
//             ? s : MeasurementState.Ready;

//         if (State == MeasurementState.Done && !string.IsNullOrEmpty(record.CsvFileName))
//         {
//             var fullPath = Path.Combine(studyFolderPath, record.CsvFileName);
//             if (!File.Exists(fullPath))
//                 IsBroken = true;
//         }
//     }

//     public MeasurementRecord Record { get; }

//     public string MeasurementId => Record.MeasurementId;
//     public string Label => Record.Label;
//     public string DevicesSummary => string.Join(", ", Record.DeviceIds);

//     public string? CsvFilePath => string.IsNullOrEmpty(Record.CsvFileName)
//         ? null
//         : Path.Combine(_studyFolderPath, Record.CsvFileName);

//     [ObservableProperty]
//     private MeasurementState _state = MeasurementState.Ready;

//     [ObservableProperty]
//     private int _recordedSampleCount;

//     [ObservableProperty]
//     private bool _isBroken;

//     public ObservableCollection<FluxAnalysisResult> AnalysisResults { get; } = new();

//     private StreamWriter? _csvWriter;
//     private bool _disposed;
//     private readonly string _studyFolderPath;
//     private readonly Dictionary<string, string> _latestPressures = new();
//     private DispatcherTimer? _recordingTimer;

//     public void StartRecording()
//     {
//         if (State != MeasurementState.Ready) return;

//         if (!Directory.Exists(_studyFolderPath))
//             Directory.CreateDirectory(_studyFolderPath);

//         Record.CsvFileName = $"{MeasurementId}.csv";
//         var csvPath = Path.Combine(_studyFolderPath, Record.CsvFileName);
//         _csvWriter = new StreamWriter(csvPath, false, Encoding.UTF8);
        
//         // ✅ 변경: 각 장비 ID 뒤에 "/Torr"를 붙여서 쉼표로 연결
//         var header = "timestamp_iso," + string.Join(",", Record.DeviceIds.Select(id => $"{id}/Torr"));
//         _csvWriter.WriteLine(header);

//         Record.StartTime = DateTimeOffset.Now;
//         RecordedSampleCount = 0;
        
//         // 상태 초기화
//         _latestPressures.Clear();
//         _lastWriteTime = DateTimeOffset.Now;
//         State = MeasurementState.Recording;
//     }

//     public void StopRecording()
//     {
//         if (State != MeasurementState.Recording) return;

//         _csvWriter?.Flush();
//         _csvWriter?.Close();
//         _csvWriter?.Dispose();
//         _csvWriter = null;

//         Record.EndTime = DateTimeOffset.Now;
//         Record.RecordedSampleCount = RecordedSampleCount;
//         Record.State = "Done";
//         State = MeasurementState.Done;
//     }

//     public bool TryWriteReading(string deviceId, GaugeReading reading, int tickThreshold)
//     {
//         if (State != MeasurementState.Recording || _csvWriter == null) return false;
//         if (!Record.DeviceIds.Contains(deviceId)) return false;

//         // ✅ 각 장비의 가장 최근 압력값을 캐싱해둡니다.
//         _latestPressures[deviceId] = reading.PressureTorr;

//         var now = DateTimeOffset.Now;
//         double intervalMs = 250.0;

//         // ✅ 마지막 기록 후 500ms가 지났으면 모아둔 값을 한 줄로 씁니다.
//         if ((now - _lastWriteTime).TotalMilliseconds >= intervalMs)
//         {
//             _lastWriteTime = now;

//             var timestampIso = now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
//             var sb = new StringBuilder(timestampIso);

//             // 등록된 장비 순서대로 값을 꺼내서 쉼표(,)로 이어 붙임
//             foreach (var dev in Record.DeviceIds)
//             {
//                 sb.Append(",");
//                 if (_latestPressures.TryGetValue(dev, out var p))
//                     sb.Append(p.ToString("G", CultureInfo.InvariantCulture));
//                 else
//                     sb.Append(""); // 아직 값이 안 들어왔으면 빈칸
//             }

//             _csvWriter.WriteLine(sb.ToString());
//             RecordedSampleCount++;

//             if (RecordedSampleCount % 10 == 0)
//                 _csvWriter.Flush();

//             return true;
//         }

//         return false;
//     }

//     public void AddAnalysisResult(FluxAnalysisResult result)
//     {
//         result.AnalysisId = Record.LatestAnalysisId + 1;
//         Record.LatestAnalysisId = result.AnalysisId;
//         result.MeasurementRecordId = MeasurementId;
//         AnalysisResults.Insert(0, result);
//     }

//     public void SyncToRecord()
//     {
//         Record.State = State == MeasurementState.Recording ? "Done" : State.ToString();
//         Record.RecordedSampleCount = RecordedSampleCount;
//         Record.AnalysisResults = AnalysisResults.ToList();
//     }

//     public void Dispose()
//     {
//         if (_disposed) return;
//         _disposed = true;
//         _csvWriter?.Flush();
//         _csvWriter?.Dispose();
//         _csvWriter = null;
//     }
// }
