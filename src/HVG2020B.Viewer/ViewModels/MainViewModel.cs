using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Ports;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HVG2020B.Core;
using HVG2020B.Core.Models;
using HVG2020B.Core.Services;
using HVG2020B.Driver;
using HVG2020B.Viewer.Services;

namespace HVG2020B.Viewer.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly DeviceManager _deviceManager;
    private readonly HashSet<string> _managedDeviceIds = new();
    private readonly Dictionary<string, DeviceSeries> _seriesByDevice = new();
    private readonly ObservableCollection<DeviceSeries> _deviceSeries = new();
    private readonly List<string> _seriesPalette = new()
    {
        "#0076C0",
        "#4CAF50",
        "#FF9800",
        "#9C27B0",
        "#E53935",
        "#00BCD4"
    };
    private int _seriesPaletteIndex;

    private readonly List<double> _timeData = new();
    private readonly List<double> _pressureData = new();
    private DateTime _startTime;
    private DateTime _recordingStartTime;
    private int _sampleCount;
    private bool _disposed;

    // Per-measurement recording (multiple simultaneous)
    private readonly List<MeasurementItem> _recordingMeasurements = new();

    // Study persistence
    private readonly string _logDir;
    private readonly StudyFolderStore _studyFolderStore;

    private const int LiveIntervalMs = 100;

    // Logging tick threshold
    private const int LogTickThreshold = 5; // 500ms / 100ms = 5 ticks

    // Blinking indicator timer
    private readonly DispatcherTimer _blinkTimer;
    private readonly DispatcherTimer _scanTimer;
    private CancellationTokenSource? _scanCts;

    // Minimum pressure value for log scale (avoids log(0) issues)
    private const double MinPressureForLog = 1e-12;

    public MainViewModel()
    {
        _deviceManager = new DeviceManager(TimeSpan.FromMilliseconds(LiveIntervalMs));
        _deviceManager.ReadingReceived += OnReadingReceived;
        _deviceManager.ConnectionLost += OnDeviceConnectionLost;

        // Setup blink timer for recording indicator
        _blinkTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _blinkTimer.Tick += (_, _) =>
        {
            if (CurrentState == ViewerState.Recording)
            {
                IndicatorVisible = !IndicatorVisible;
            }
        };

        _scanTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _scanTimer.Tick += (_, _) =>
        {
            if (IsScanning)
            {
                ScanElapsedSeconds++;
            }
        };

        // Study persistence — per-folder store
        _logDir = Path.Combine(Directory.GetCurrentDirectory(), "logs");
        _studyFolderStore = new StudyFolderStore(_logDir);

        // One-time migration from legacy studies.json
        var migratedCount = _studyFolderStore.MigrateFromLegacy();

        // Load studies from per-folder store
        foreach (var record in _studyFolderStore.LoadAll())
        {
            // Determine folder path: prefer existing folder from CsvFilePath, else default
            string folderPath;
            if (!string.IsNullOrEmpty(record.CsvFilePath) &&
                Directory.Exists(Path.GetDirectoryName(record.CsvFilePath)))
            {
                folderPath = Path.GetDirectoryName(record.CsvFilePath)!;
            }
            else
            {
                folderPath = _studyFolderStore.GetStudyFolderPath(record.Metadata.StudyId);
            }

            Studies.Add(new StudyItem(record, folderPath));
        }

        ApplyStudyFilter();
        if (FilteredStudies.Count > 0)
            SelectedStudy = FilteredStudies[0];
        RefreshFluxResultsDashboard();

        if (migratedCount > 0)
            StatusMessage = $"Migrated {migratedCount} legacy studies";
    }

     #region Observable Properties
    // 사용 가능한 장비 목록과 선택된 장비
    [ObservableProperty]
    private ObservableCollection<string> _availableDeviceTypes = new() { "HVG-2020B", "신규 장비(NewGauge)" };

    [ObservableProperty]
    private string? _selectedDeviceType = "HVG-2020B";
    
    // 기존에 추가하셨던 포트 관련 변수 (유지)
    [ObservableProperty]
    private ObservableCollection<string> _availablePorts = new();

    [ObservableProperty]
    private string? _selectedPort;

    [ObservableProperty]
    private ObservableCollection<DeviceItem> _devices = new();

    [ObservableProperty]
    private DeviceItem? _selectedDevice;

    [ObservableProperty]
    private ViewerState _currentState = ViewerState.Disconnected;

    [ObservableProperty]
    private string _statusMessage = "Ready";


    [ObservableProperty]
    private ObservableCollection<StudyItem> _studies = new();

    [ObservableProperty]
    private ObservableCollection<StudyItem> _filteredStudies = new();

    [ObservableProperty]
    private string _studySearchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedStudy))]
    private StudyItem? _selectedStudy;

    [ObservableProperty]
    private bool _isNewStudyDialogOpen;

    [ObservableProperty]
    private string _newStudyTitle = string.Empty;

    [ObservableProperty]
    private string _newStudyId = string.Empty;

    // Add Measurement dialog
    [ObservableProperty]
    private bool _isAddMeasurementDialogOpen;

    [ObservableProperty]
    private string _newMeasurementLabel = string.Empty;

    [ObservableProperty]
    private ObservableCollection<NewStudyDeviceOption> _newMeasurementDeviceSelections = new();

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _scanProgressText = string.Empty;

    [ObservableProperty]
    private double _scanProgressValue;

    [ObservableProperty]
    private int _scanElapsedSeconds;

    [ObservableProperty]
    private bool _hasScanResults;

    public ObservableCollection<ScannedDevice> ScanResults { get; } = new();

    [ObservableProperty]
    private int _sampleCountDisplay;

    [ObservableProperty]
    private int _recordedSampleCountDisplay;

    [ObservableProperty]
    private string _elapsedTime = "00:00:00";

    [ObservableProperty]
    private string _recordingTime = "00:00:00";

    [ObservableProperty]
    private bool _indicatorVisible = true;

    [ObservableProperty]
    private string? _logFilePath;

    /// <summary>
    /// Y-axis scale type: true = Log, false = Linear
    /// </summary>
    [ObservableProperty]
    private bool _useLogScale = true;

    [ObservableProperty]
    private ObservableCollection<FluxAnalysisResult> _allFluxResults = new();

    [ObservableProperty]
    private FluxAnalysisResult? _selectedFluxResult;

    #endregion

    #region Computed Properties

    public bool IsDisconnected => CurrentState == ViewerState.Disconnected;
    public bool IsLive => CurrentState == ViewerState.Live;
    public bool IsRecording => CurrentState == ViewerState.Recording;
    public bool IsConnected => CurrentState != ViewerState.Disconnected;
    public bool HasSelectedStudy => SelectedStudy != null;

    public ObservableCollection<DeviceSeries> DeviceSeries => _deviceSeries;

    #endregion

    #region Chart Data

    public List<double> TimeData => _timeData;
    public List<double> PressureData => _pressureData;

    public event Action? DataUpdated;
    public event Action? ScaleChanged;

    #endregion

    #region State Change Handler

    partial void OnCurrentStateChanged(ViewerState value)
    {
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(IsConnected));

        // Handle blink timer
        if (value == ViewerState.Recording)
        {
            _blinkTimer.Start();
        }
        else
        {
            _blinkTimer.Stop();
            IndicatorVisible = true;
        }
    }

    partial void OnUseLogScaleChanged(bool value)
    {
        ScaleChanged?.Invoke();
    }

    partial void OnSelectedDeviceChanged(DeviceItem? value)
    {
        UpdateSelectedSeriesData(value?.DeviceId);
    }

    #endregion

    #region Device Commands

    [RelayCommand]
    private async Task ScanForDevices()
    {
        if (IsScanning)
        {
            return;
        }

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();

        IsScanning = true;
        HasScanResults = false;
        ScanProgressValue = 0;
        ScanElapsedSeconds = 0;
        ScanProgressText = "Starting scan...";
        ScanResults.Clear();
        _scanTimer.Start();

        var token = _scanCts.Token;
        try
        {
            var ports = SerialPort.GetPortNames().OrderBy(p => p).ToArray();
            var connectedPorts = Devices
                .Where(d => d.IsConnected && !string.IsNullOrWhiteSpace(d.PortName))
                .Select(d => d.PortName!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var portsToScan = ports.Where(p => !connectedPorts.Contains(p)).ToArray();
            if (portsToScan.Length == 0)
            {
                ScanProgressText = "No available ports to scan";
                HasScanResults = true;
                return;
            }

            for (var i = 0; i < portsToScan.Length; i++)
            {
                token.ThrowIfCancellationRequested();

                var port = portsToScan[i];
                ScanProgressText = $"{port} 시도 중 ({i + 1}/{portsToScan.Length})";
                ScanProgressValue = (double)(i + 1) / portsToScan.Length;

                var client = new HVG2020BClient();
                try
                {
                    await client.ConnectWithAutoScanAsync(port, token);

                    ScanResults.Add(new ScannedDevice
                    {
                        DeviceId = client.DeviceId,
                        PortName = port,
                        DeviceType = client.DeviceType,
                        Device = client,
                        IsAdded = false
                    });
                }
                catch (OperationCanceledException)
                {
                    client.Dispose();
                    throw;
                }
                catch
                {
                    client.Dispose();
                }
            }

            HasScanResults = true;
            ScanProgressText = ScanResults.Count > 0
                ? $"✅ {ScanResults.Count} devices found"
                : "No devices found";
        }
        catch (OperationCanceledException)
        {
            ScanProgressText = "Scan cancelled";
        }
        finally
        {
            IsScanning = false;
            _scanTimer.Stop();
        }
    }
    [RelayCommand]
    private async Task AutoConnectAllDevices()
    {
        // 현재 PC의 모든 COM 포트를 가져옵니다.
        var ports = System.IO.Ports.SerialPort.GetPortNames().OrderBy(p => p).ToArray();
        var connectedPorts = Devices.Where(d => d.IsConnected).Select(d => d.PortName).ToList();

        int foundCount = 0;

        foreach (var port in ports)
        {
            if (connectedPorts.Contains(port)) continue;

            StatusMessage = $"{port} 장비 식별 중...";

            // ==========================================
            // 1. HVG-2020B 먼저 테스트 (포트를 안전하게 닫아주므로 먼저 실행)
            // ==========================================
            var hvg = new HVG2020B.Driver.HVG2020BClient();
            try
            {
                var settings = HVG2020B.Driver.HVGSerialSettings.ForRs232(baudRate: 19200);
                await hvg.ConnectAsync(port, settings);

                using var cts = new CancellationTokenSource(500);
                await hvg.ReadOnceAsync(cts.Token);

                // 통과하면 2020B가 맞음!
                RegisterDeviceToUI(hvg, port);
                foundCount++;
                continue; // ❗ 찾았으면 아래 Sens4 테스트는 건너뛰고 다음 포트로 이동
            }
            catch
            {
                // 아니면 안전하게 포트 닫기
                hvg.Dispose();
            }

            // ⭐⭐⭐ [핵심] 포트가 닫히고 윈도우가 리셋할 0.5초의 시간을 줍니다. ⭐⭐⭐
            await Task.Delay(500);

            // ==========================================
            // 2. Sens4 장비 테스트 (그 다음 순서로 실행)
            // ==========================================
            var sens4 = new HVG2020B.Driver.Sens4Client(); // (클래스명은 맞춰주세요)
            try
            {
                await sens4.ConnectAsync(port);
                
                using var cts = new CancellationTokenSource(500); 
                await sens4.ReadOnceAsync(cts.Token);
                
                RegisterDeviceToUI(sens4, port);
                foundCount++;
                continue;
            }
            catch
            {
                sens4.Dispose(); 
            }
            await Task.Delay(500); // 0.5초 대기

            // ==========================================
            // 3. ✨ 세 번째 장비 테스트 (새로 추가)
            // ==========================================
            var ccd100 = new HVG2020B.Driver.CCD100Client();
            try
            {
                await ccd100.ConnectAsync(port);
                using var cts = new CancellationTokenSource(500); 
                await ccd100.ReadOnceAsync(cts.Token);
                
                // =======================================================
                // ✅ 통과했다면(장비가 맞다면), 팝업을 띄워 사용자에게 이름을 입력받습니다!
                string customName = PromptForDeviceName(port);
                ccd100.SetCustomId(customName);
                // =======================================================

                RegisterDeviceToUI(ccd100, port);
                foundCount++;
                continue;
            }
            catch
            {
                ccd100.Dispose(); 
            }
            await Task.Delay(200);
        }

        StatusMessage = $"자동 식별 완료: 총 {foundCount}개의 장비가 새로 연결되었습니다.";
    }
    private void RegisterDeviceToUI(IGaugeDevice client, string portName)
    {
        var item = new DeviceItem(client) { IsConnected = true, PortName = portName };
        Devices.Add(item);
        item.PropertyChanged += OnDeviceItemPropertyChanged;
        EnsureDeviceSeries(item.DeviceId);
        SelectedDevice ??= item;

        if (_managedDeviceIds.Add(item.DeviceId))
        {
            _deviceManager.AddDevice(item.Device);
        }

        if (CurrentState == ViewerState.Disconnected)
        {
            CurrentState = ViewerState.Live;
            _startTime = DateTime.Now;
            _sampleCount = 0;
        }
    }

    private string PromptForDeviceName(string portName)
    {
        string inputName = $"CCD100_{portName}"; // 미입력 시 기본값

        Application.Current.Dispatcher.Invoke(() =>
        {
            var window = new Window
            {
                Title = "장비 이름 설정", Width = 350, Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30))
            };

            var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(15) };
            
            var text = new System.Windows.Controls.TextBlock 
            { 
                Text = $"{portName} 포트에 CCD-100 장비가 감지되었습니다.\n구분을 위한 장비 이름을 입력해 주세요.", 
                Foreground = System.Windows.Media.Brushes.White, 
                Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap
            };
            
            var textBox = new System.Windows.Controls.TextBox 
            { 
                Text = "", 
                Padding = new Thickness(6), FontSize = 14,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 56, 56)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderBrush = System.Windows.Media.Brushes.Gray
            };
            
            var btn = new System.Windows.Controls.Button 
            { 
                Content = "확인(Enter)", Margin = new Thickness(0, 15, 0, 0), Width = 100, Padding = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(33, 150, 243)),
                Foreground = System.Windows.Media.Brushes.White, IsDefault = true
            };

            btn.Click += (s, e) => 
            { 
                if (!string.IsNullOrWhiteSpace(textBox.Text)) inputName = textBox.Text.Trim(); 
                window.DialogResult = true; 
            };

            stack.Children.Add(text);
            stack.Children.Add(textBox);
            stack.Children.Add(btn);
            window.Content = stack;

            window.ShowDialog();
        });
        
        return inputName;
    }
    
    [RelayCommand]
    private void CancelScan()
    {
        _scanCts?.Cancel();
    }

    [RelayCommand]
    private async Task AddScannedDevice(ScannedDevice device)
    {
        if (device.IsAdded)
        {
            return;
        }

        IGaugeDevice deviceToAdd = device.Device;
        var replacedDevice = false;
        if (!deviceToAdd.IsConnected)
        {
            try
            {
                var reconnectClient = new HVG2020BClient(device.DeviceId);
                await reconnectClient.ConnectWithAutoScanAsync(device.PortName);
                deviceToAdd = reconnectClient;
                replacedDevice = true;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Reconnect failed: {ex.Message}";
                return;
            }
        }

        var item = new DeviceItem(deviceToAdd);
        item.IsConnected = deviceToAdd.IsConnected;
        item.PortName = device.PortName;

        Devices.Add(item);
        item.PropertyChanged += OnDeviceItemPropertyChanged;
        EnsureDeviceSeries(item.DeviceId);

        if (SelectedDevice == null)
        {
            SelectedDevice = item;
        }

        if (replacedDevice)
        {
            device.Device.Dispose();
        }

        device.IsAdded = true;

        if (_managedDeviceIds.Add(item.DeviceId))
        {
            _deviceManager.AddDevice(item.Device);
        }

        if (CurrentState == ViewerState.Disconnected)
        {
            CurrentState = ViewerState.Live;
            _startTime = DateTime.Now;
            _sampleCount = 0;
        }

        StatusMessage = $"Device added: {item.DeviceId}";

        if (ScanResults.All(r => r.IsAdded))
        {
            HasScanResults = false;
        }
    }

    [RelayCommand]
    private void AddEmulatorDevice()
    {
        var emulator = new EmulatorDevice();
        emulator.ConnectAsync("EMULATOR").GetAwaiter().GetResult();

        var item = new DeviceItem(emulator)
        {
            IsConnected = true,
            PortName = "EMULATOR"
        };

        Devices.Add(item);
        item.PropertyChanged += OnDeviceItemPropertyChanged;
        EnsureDeviceSeries(item.DeviceId);

        SelectedDevice ??= item;

        if (_managedDeviceIds.Add(item.DeviceId))
        {
            _deviceManager.AddDevice(item.Device);
        }

        if (CurrentState == ViewerState.Disconnected)
        {
            CurrentState = ViewerState.Live;
            _startTime = DateTime.Now;
            _sampleCount = 0;
        }

        StatusMessage = $"Emulator added: {item.DeviceId}";
    }

    [RelayCommand]
    private void RemoveDevice(DeviceItem device)
    {
        if (device == null)
        {
            return;
        }

        // Prevent removal while a recording measurement uses this device
        if (_recordingMeasurements.Any(m => m.Record.DeviceIds.Contains(device.DeviceId)))
        {
            StatusMessage = $"Cannot remove {device.DeviceId} — recording in progress";
            return;
        }

        device.Device.Disconnect();
        device.IsConnected = false;
        device.PortName = null;
        device.CurrentPressure = "---";

        _deviceManager.RemoveDevice(device.DeviceId);
        _managedDeviceIds.Remove(device.DeviceId);

        if (_seriesByDevice.TryGetValue(device.DeviceId, out var series))
        {
            _seriesByDevice.Remove(device.DeviceId);
            _deviceSeries.Remove(series);
        }

        Devices.Remove(device);
        device.PropertyChanged -= OnDeviceItemPropertyChanged;

        UpdateCurrentStateFromDevices();
        DataUpdated?.Invoke();
    }

    [RelayCommand]
    private void ToggleGraph(DeviceItem device)
    {
        if (device == null)
        {
            return;
        }

        device.IsVisibleOnChart = !device.IsVisibleOnChart;
    }

    #endregion

    #region Study Commands

    [RelayCommand]
    private void OpenNewStudyDialog()
    {
        IsNewStudyDialogOpen = true;
        NewStudyTitle = string.Empty;
        NewStudyId = string.Empty;
    }

    [RelayCommand]
    private void CancelNewStudy()
    {
        IsNewStudyDialogOpen = false;
        NewStudyTitle = string.Empty;
        NewStudyId = string.Empty;
    }

    [RelayCommand]
    private void CreateNewStudy()
    {
        if (string.IsNullOrWhiteSpace(NewStudyId))
        {
            StatusMessage = "Please enter an Id";
            return;
        }

        var studyId = GenerateStudyId();
        var metadata = new StudyMetadata
        {
            StudyId = studyId,
            Title = NewStudyTitle.Trim(),
            UserTag = NewStudyId.Trim(),
            CreatedAt = DateTimeOffset.Now,
            Status = "Active"
        };

        var folderPath = _studyFolderStore.GetStudyFolderPath(studyId);
        var newStudy = new StudyItem(metadata, folderPath);
        Studies.Insert(0, newStudy);
        ApplyStudyFilter();
        SelectedStudy = newStudy;
        PersistStudy(newStudy);

        IsNewStudyDialogOpen = false;
        NewStudyTitle = string.Empty;
        NewStudyId = string.Empty;

        StatusMessage = $"Study created: {studyId}";
    }

    [RelayCommand]
    private void OpenAddMeasurementDialog()
    {
        if (SelectedStudy == null || !SelectedStudy.IsActive) return;

        IsAddMeasurementDialogOpen = true;
        NewMeasurementLabel = string.Empty;
        NewMeasurementDeviceSelections.Clear();

        foreach (var device in Devices.Where(d => d.IsConnected))
        {
            NewMeasurementDeviceSelections.Add(
                new NewStudyDeviceOption(device.DeviceId, isSelected: true));
        }

        if (NewMeasurementDeviceSelections.Count == 0)
        {
            StatusMessage = "No connected devices available";
        }
    }

    [RelayCommand]
    private void CancelAddMeasurement()
    {
        IsAddMeasurementDialogOpen = false;
        NewMeasurementLabel = string.Empty;
        NewMeasurementDeviceSelections.Clear();
    }

    [RelayCommand]
    private void SelectAllMeasurementDevices()
    {
        foreach (var d in NewMeasurementDeviceSelections) d.IsSelected = true;
    }

    [RelayCommand]
    private void DeselectAllMeasurementDevices()
    {
        foreach (var d in NewMeasurementDeviceSelections) d.IsSelected = false;
    }

    [RelayCommand]
    private void CreateAndStartMeasurement()
    {
        if (SelectedStudy == null || !SelectedStudy.IsActive) return;

        if (CurrentState == ViewerState.Disconnected)
        {
            StatusMessage = "Cannot record: no devices connected";
            return;
        }

        var selectedDevices = NewMeasurementDeviceSelections
            .Where(d => d.IsSelected)
            .Select(d => d.DeviceId)
            .ToList();

        if (selectedDevices.Count == 0)
        {
            StatusMessage = "Please select at least one device";
            return;
        }

        try
        {
            var measurement = SelectedStudy.AddMeasurement(
                NewMeasurementLabel.Trim(), selectedDevices);

            measurement.StartRecording();
            _recordingMeasurements.Add(measurement);
            SelectedStudy.NotifyRecordingChanged();

            if (SelectedStudy.Metadata.StartTime == null)
                SelectedStudy.Metadata.StartTime = DateTimeOffset.Now;

            SelectedStudy.IsExpanded = true;
            PersistStudy(SelectedStudy);

            IsAddMeasurementDialogOpen = false;
            NewMeasurementLabel = string.Empty;
            NewMeasurementDeviceSelections.Clear();

            if (CurrentState != ViewerState.Recording)
            {
                CurrentState = ViewerState.Recording;
                _recordingStartTime = DateTime.Now;
                RecordingTime = "00:00:00";
            }

            StatusMessage = $"Recording {measurement.MeasurementId} in '{SelectedStudy.Title}'";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to start measurement: {ex.Message}";
        }
    }

    [RelayCommand]
    private void StopMeasurementRecording(object? parameter)
    {
        if (parameter is not MeasurementItem measurement) return;
        if (measurement.State != MeasurementState.Recording) return;

        measurement.StopRecording();
        _recordingMeasurements.Remove(measurement);

        var parentStudy = FindParentStudy(measurement);
        if (parentStudy != null)
        {
            parentStudy.NotifyRecordingChanged();
            PersistStudy(parentStudy);
        }

        StatusMessage = $"Stopped {measurement.MeasurementId} - {measurement.RecordedSampleCount} samples";

        if (_recordingMeasurements.Count == 0)
        {
            CurrentState = ViewerState.Live;
        }
    }

    [RelayCommand]
    private void CloseStudy(object? parameter)
    {
        if (parameter is not StudyItem study) return;
        if (!study.IsActive) return;

        // Stop any active recordings in this study
        foreach (var m in study.Measurements
            .Where(m => m.State == MeasurementState.Recording).ToList())
        {
            m.StopRecording();
            _recordingMeasurements.Remove(m);
        }

        study.CloseStudy();
        PersistStudy(study);

        if (_recordingMeasurements.Count == 0 && CurrentState == ViewerState.Recording)
            CurrentState = ViewerState.Live;

        StatusMessage = $"Study closed: {study.Title}";
    }

    [RelayCommand]
    private void DeleteStudy(object? parameter)
    {
        if (parameter is not StudyItem study) return;

        // Stop any active recordings in this study
        foreach (var m in study.Measurements
            .Where(m => m.State == MeasurementState.Recording).ToList())
        {
            m.StopRecording();
            _recordingMeasurements.Remove(m);
        }

        // Ask user whether to also delete files from disk
        var folderPath = study.StudyFolderPath;
        bool deleteFiles = false;
        if (!string.IsNullOrEmpty(folderPath) && Directory.Exists(folderPath))
        {
            // ✅ 전체 경로 대신 폴더 이름만 추출
            string folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            
            // ✅ 깔끔한 메시지 구성
            string message = $"Are you sure you want to delete this study and its files from disk?\n\n" +
                             $"ID: {study.UserTag}\n" +
                             $"Study: {study.Title}\n" +
                             $"Folder: {folderName}";

            var result = MessageBox.Show(
                message,
                "Delete Study",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Cancel)
                return;

            deleteFiles = result == MessageBoxResult.Yes;
        }

        study.Dispose();
        Studies.Remove(study);
        ApplyStudyFilter();
        RefreshFluxResultsDashboard();

        if (deleteFiles && !string.IsNullOrEmpty(folderPath))
        {
            try
            {
                _studyFolderStore.DeleteFolder(folderPath);
                StatusMessage = $"Study deleted (files removed): {study.StudyId}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Study deleted, but folder removal failed: {ex.Message}";
            }
        }
        else
        {
            StatusMessage = $"Study deleted: {study.StudyId}";
        }

        if (_recordingMeasurements.Count == 0 && CurrentState == ViewerState.Recording)
            CurrentState = ViewerState.Live;
    }

    [RelayCommand]
    private void AnalyzeMeasurement(object? parameter)
    {
        if (parameter is not MeasurementItem measurement) return;
        if (measurement.State != MeasurementState.Done) return;

        var csvPath = measurement.CsvFilePath;
        if (string.IsNullOrEmpty(csvPath) || !File.Exists(csvPath))
        {
            StatusMessage = "CSV file not found for this measurement";
            return;
        }

        var parentStudy = FindParentStudy(measurement);
        if (parentStudy == null) return;

        try
        {
            var parsed = StudyCsvParser.Parse(csvPath);

            if (parsed.DeviceIds.Count == 0)
            {
                StatusMessage = "No data found in measurement CSV";
                return;
            }

            string selectedDeviceId;
            if (parsed.DeviceIds.Count == 1)
            {
                selectedDeviceId = parsed.DeviceIds[0];
            }
            else
            {
                var dialog = new DeviceSelectionDialog(parsed.DeviceIds);
                dialog.Owner = Application.Current.MainWindow;
                if (dialog.ShowDialog() != true || dialog.SelectedDeviceId == null)
                    return;
                selectedDeviceId = dialog.SelectedDeviceId;
            }

            var (timeSeconds, pressureTorr) = StudyCsvParser.ExtractDeviceData(
                parsed.RowsByDevice[selectedDeviceId]);

            if (timeSeconds.Length < 2)
            {
                StatusMessage = $"Insufficient data for device {selectedDeviceId} ({timeSeconds.Length} points)";
                return;
            }

            var studyFolder = parentStudy.StudyFolderPath;
            var fluxWindow = new FluxCalculationWindow(
                new List<double>(timeSeconds), new List<double>(pressureTorr), studyFolder);
            fluxWindow.Title = $"Calculation - {parentStudy.Title} / {measurement.MeasurementId} ({selectedDeviceId})";
            fluxWindow.Owner = Application.Current.MainWindow;
            fluxWindow.ShowDialog();

            if (fluxWindow.CalculationResult is { } calc)
            {
                // Capture screenshot
                string? screenshotPath = null;
                if (studyFolder != null)
                {
                    try
                    {
                        screenshotPath = ScreenshotCapture.CaptureWindow(
                            fluxWindow, studyFolder,
                            $"{measurement.MeasurementId}_analysis_{measurement.Record.LatestAnalysisId + 1}");
                    }
                    catch { /* screenshot failure is non-fatal */ }
                }

                var analysisResult = new FluxAnalysisResult
                {
                    CalculatedAt = DateTimeOffset.Now,
                    StudyId = parentStudy.StudyId,
                    StudyTitle = parentStudy.Title,
                    MeasurementId = parentStudy.UserTag,
                    MeasurementRecordId = measurement.MeasurementId,
                    DeviceId = selectedDeviceId,
                    ScreenshotPath = screenshotPath,
                    Mode = calc.Mode,
                    MembraneArea = calc.MembraneArea,
                    Temperature = calc.Temperature,
                    FeedSidePressure = calc.FeedSidePressure,
                    ChamberVolume = calc.ChamberVolume,
                    StartTime = calc.StartTime,
                    EndTime = calc.EndTime,
                    Flux = calc.Flux,
                    Permeance = calc.Permeance,
                    PermeanceGpu = calc.PermeanceGpu,
                    PressureChangeRate = calc.PressureChangeRate,
                    RSquared = calc.RSquared,
                    DataPointCount = calc.DataPointCount,
                    LeakRateTorrLps = calc.LeakRateTorrLps,
                    LeakRatePaM3ps = calc.LeakRatePaM3ps,
                    LeakRateMbarLps = calc.LeakRateMbarLps,
                    ConfigMemo = calc.ConfigMemo
                };

                measurement.AddAnalysisResult(analysisResult);
                PersistStudy(parentStudy);
                RefreshFluxResultsDashboard();

                // Auto-export Excel to study folder
                if (studyFolder != null)
                {
                    try
                    {
                        var excelPath = Path.Combine(studyFolder,
                            $"{SanitizeFolderName(parentStudy.Title)}.xlsx");
                        StudyExcelExporter.Export(excelPath, parentStudy, _studyFolderStore);
                        StatusMessage = $"Analysis #{analysisResult.AnalysisId} saved + Excel exported";
                    }
                    catch (Exception excelEx)
                    {
                        StatusMessage = $"Analysis saved, Excel export failed: {excelEx.Message}";
                    }
                }
                else
                {
                    StatusMessage = $"Analysis saved: {measurement.MeasurementId} #{analysisResult.AnalysisId}";
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Analysis failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void DeleteSelectedStudy()
    {
        if (SelectedStudy != null)
            DeleteStudy(SelectedStudy);
    }

    [RelayCommand]
    private void OpenStudyFolder()
    {
        if (SelectedStudy?.StudyFolderPath == null) return;
        var folderPath = SelectedStudy.StudyFolderPath;
        if (!Directory.Exists(folderPath))
        {
            StatusMessage = $"Folder not found: {folderPath}";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start("explorer.exe", folderPath);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to open folder: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearChart()
    {
        foreach (var series in _deviceSeries)
        {
            series.TimeData.Clear();
            series.PressureData.Clear();
            series.StartTime = default;
        }

        _timeData.Clear();
        _pressureData.Clear();
        _sampleCount = 0;
        SampleCountDisplay = 0;
        _startTime = DateTime.Now;
        ElapsedTime = "00:00:00";
        DataUpdated?.Invoke();
    }

    #endregion

    #region Device Readings

    private void OnReadingReceived(object? sender, (string DeviceId, GaugeReading Reading) payload)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(() => HandleReading(payload.DeviceId, payload.Reading));
            return;
        }

        HandleReading(payload.DeviceId, payload.Reading);
    }

    private void HandleReading(string deviceId, GaugeReading reading)
    {
        var deviceItem = Devices.FirstOrDefault(d => d.DeviceId == deviceId);
        if (deviceItem == null)
        {
            return;
        }

        _sampleCount++;
        SampleCountDisplay = _sampleCount;

        // Update device display
        deviceItem.CurrentPressure = $"{reading.PressureTorr:E3} Torr";
        deviceItem.IsConnected = true;
        deviceItem.PortName = deviceItem.Device.PortName;

        // Update elapsed time (since first connection)
        var elapsed = DateTime.Now - _startTime;
        ElapsedTime = elapsed.ToString(@"hh\:mm\:ss");

        // Add to chart data (per device)
        var series = EnsureDeviceSeries(deviceId);
        if (series.StartTime == default)
        {
            series.StartTime = reading.Timestamp;
        }

        var timeSeconds = (reading.Timestamp - series.StartTime).TotalSeconds;
        series.TimeData.Add(timeSeconds);
        var pressure = Math.Max(reading.PressureTorr, MinPressureForLog);
        series.PressureData.Add(pressure);

        // Update selected series buffer for existing chart binding
        if (SelectedDevice?.DeviceId == deviceId)
        {
            UpdateSelectedSeriesData(deviceId);
        }

        // Per-measurement recording (multiple simultaneous)
        if (_recordingMeasurements.Count > 0)
        {
            foreach (var activeMeasurement in _recordingMeasurements)
            {
                // ✅ 바뀐 함수인 UpdateReading 사용 (카운트는 타이머가 알아서 함)
                activeMeasurement.UpdateReading(deviceId, reading);
            }

            var totalSamples = _recordingMeasurements.Sum(m => m.RecordedSampleCount);
            RecordedSampleCountDisplay = totalSamples;
            
            var recordingElapsed = DateTime.Now - _recordingStartTime;
            RecordingTime = recordingElapsed.ToString(@"hh\:mm\:ss");
        }

        DataUpdated?.Invoke();

        if (CurrentState == ViewerState.Live)
        {
            StatusMessage = $"Live - {_sampleCount} samples";
        }
    }

    #endregion

    private void OnDeviceConnectionLost(object? sender, (string DeviceId, Exception Error) payload)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(() => OnDeviceConnectionLost(sender, payload));
            return;
        }

        var deviceItem = Devices.FirstOrDefault(d => d.DeviceId == payload.DeviceId);
        if (deviceItem != null)
        {
            deviceItem.IsConnected = false;
            deviceItem.CurrentPressure = "---";
        }

        // =========================================================
        // ✅ [추가] 에러(끊김) 발생 시 해당 장비 값을 'Err'로 기록하도록 전달
        // =========================================================
        foreach (var measurement in _recordingMeasurements)
        {
            measurement.SetDeviceError(payload.DeviceId);
        }

        StatusMessage = $"Connection lost ({payload.DeviceId}): {payload.Error.Message}";
        UpdateCurrentStateFromDevices();
    }

    private void OnDeviceItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceItem.IsVisibleOnChart))
        {
            DataUpdated?.Invoke();
        }
    }

    private DeviceSeries EnsureDeviceSeries(string deviceId)
    {
        if (_seriesByDevice.TryGetValue(deviceId, out var existing))
        {
            return existing;
        }

        var series = new DeviceSeries(deviceId, GetNextSeriesColor());
        _seriesByDevice[deviceId] = series;
        _deviceSeries.Add(series);
        return series;
    }

    private string GetNextSeriesColor()
    {
        var color = _seriesPalette[_seriesPaletteIndex % _seriesPalette.Count];
        _seriesPaletteIndex++;
        return color;
    }

    private void UpdateSelectedSeriesData(string? deviceId)
    {
        _timeData.Clear();
        _pressureData.Clear();

        if (deviceId == null)
        {
            DataUpdated?.Invoke();
            return;
        }

        if (_seriesByDevice.TryGetValue(deviceId, out var series))
        {
            _timeData.AddRange(series.TimeData);
            _pressureData.AddRange(series.PressureData);
        }

        DataUpdated?.Invoke();
    }

    private void UpdateCurrentStateFromDevices()
    {
        if (Devices.Any(d => d.IsConnected))
        {
            if (CurrentState == ViewerState.Recording)
            {
                return;
            }

            CurrentState = ViewerState.Live;
            return;
        }

        CurrentState = ViewerState.Disconnected;
    }

    private static string GenerateStudyId()
    {
        var now = DateTime.Now;
        return $"STD-{now:yyyyMMdd}-{now:HHmmss}";
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "Untitled" : sanitized.Trim();
    }

    partial void OnStudySearchTextChanged(string value)
    {
        ApplyStudyFilter();
    }

    private void ApplyStudyFilter()
    {
        var search = StudySearchText?.Trim() ?? "";
        FilteredStudies.Clear();
        foreach (var study in Studies)
        {
            if (string.IsNullOrEmpty(search)
                || study.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || study.StudyId.Contains(search, StringComparison.OrdinalIgnoreCase)
                || study.Measurements.Any(m =>
                    m.Label.Contains(search, StringComparison.OrdinalIgnoreCase)))
            {
                FilteredStudies.Add(study);
            }
        }
    }

    private void PersistStudy(StudyItem study)
    {
        try
        {
            _studyFolderStore.Save(study.ToRecord(), study.StudyFolderPath);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to save study: {ex.Message}";
        }
    }

    private void PersistAllStudies()
    {
        foreach (var study in Studies)
            PersistStudy(study);
    }

    private void RefreshFluxResultsDashboard()
    {
        AllFluxResults.Clear();
        foreach (var study in Studies)
        {
            foreach (var measurement in study.Measurements)
            {
                foreach (var result in measurement.AnalysisResults)
                {
                    AllFluxResults.Add(result);
                }
            }
        }
    }

    [RelayCommand]
    private void OpenFluxResultStudy(FluxAnalysisResult? result)
    {
        if (result == null || string.IsNullOrEmpty(result.StudyId))
        {
            StatusMessage = "Cannot navigate: no Study ID associated with this result";
            return;
        }

        var study = Studies.FirstOrDefault(s => s.StudyId == result.StudyId);
        if (study == null)
        {
            StatusMessage = $"Study not found: {result.StudyId}";
            return;
        }

        // Find matching measurement and analyze it
        var measurement = study.Measurements.FirstOrDefault(m =>
            m.MeasurementId == result.MeasurementRecordId);
        if (measurement != null)
        {
            SelectedStudy = study;
            study.IsExpanded = true;
            AnalyzeMeasurement(measurement);
        }
        else if (study.Measurements.Count > 0)
        {
            // Fallback: analyze first done measurement
            var firstDone = study.Measurements.FirstOrDefault(m => m.State == MeasurementState.Done);
            if (firstDone != null)
            {
                SelectedStudy = study;
                study.IsExpanded = true;
                AnalyzeMeasurement(firstDone);
            }
        }
    }

    private StudyItem? FindParentStudy(MeasurementItem measurement)
    {
        return Studies.FirstOrDefault(s => s.Measurements.Contains(measurement));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _blinkTimer.Stop();
        _scanTimer.Stop();
        _scanCts?.Cancel();
        _scanCts?.Dispose();

        foreach (var device in Devices)
        {
            device.PropertyChanged -= OnDeviceItemPropertyChanged;
        }

        foreach (var measurement in _recordingMeasurements.ToList())
        {
            measurement.StopRecording();
        }
        _recordingMeasurements.Clear();

        PersistAllStudies();

        foreach (var study in Studies)
        {
            study.Dispose();
        }

        _deviceManager.ReadingReceived -= OnReadingReceived;
        _deviceManager.ConnectionLost -= OnDeviceConnectionLost;
        _deviceManager.Dispose();
    }
}

public sealed partial class DeviceItem : ObservableObject
{
    public DeviceItem(IGaugeDevice device)
    {
        Device = device;
        DeviceId = device.DeviceId;
        CurrentPressure = "---";
    }

    public IGaugeDevice Device { get; }

    public string DeviceId { get; }

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isVisibleOnChart = true;

    [ObservableProperty]
    private string? _portName;

    [ObservableProperty]
    private string _currentPressure;
}

public sealed class DeviceSeries
{
    public DeviceSeries(string deviceId, string colorHex)
    {
        DeviceId = deviceId;
        ColorHex = colorHex;
    }

    public string DeviceId { get; }

    public string ColorHex { get; }

    public List<double> TimeData { get; } = new();

    public List<double> PressureData { get; } = new();

    public DateTimeOffset StartTime { get; set; }
}

public sealed partial class NewStudyDeviceOption : ObservableObject
{
    public NewStudyDeviceOption(string deviceId, bool isSelected)
    {
        DeviceId = deviceId;
        _isSelected = isSelected;
    }

    public string DeviceId { get; }

    [ObservableProperty]
    private bool _isSelected;

    
}
