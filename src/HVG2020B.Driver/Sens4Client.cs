using System.IO.Ports;
using System.Text;
using HVG2020B.Core;

namespace HVG2020B.Driver;

public class Sens4Client : IGaugeDevice
{
    private SerialPort? _serialPort;
    private bool _isConnected;

    private static int _instanceCount;

    // 수정 전: public string DeviceId { get; }
    public string DeviceId { get; private set; } // ⬅️ private set 추가

    public Sens4Client(string? deviceId = null)
    {
        // 입력된 이름이 없으면 "SENS4-순번" 으로 자동 부여
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            var id = System.Threading.Interlocked.Increment(ref _instanceCount);
            DeviceId = $"SENS4-{id}";
        }
        else
        {
            DeviceId = deviceId.Trim();
        }
    }
    public string DeviceType => "SENS4"; // 예: "MKS-925"
    public string DisplayName { get; set; } = "SENS4";
    public bool IsConnected => _isConnected;
    public string? PortName => _serialPort?.PortName;

    public event EventHandler<GaugeReading>? ReadingReceived;
    public event EventHandler<Exception>? ConnectionLost;

    public async Task ConnectAsync(string portName, CancellationToken cancellationToken = default)
    {
        // 1. 통신 속도 설정 (9600 bps)
        _serialPort = new SerialPort(portName, 9600, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 500,
            WriteTimeout = 500,
            DtrEnable = true,
            RtsEnable = true
        };
        
        _serialPort.Open();

        // =========================================================
        // 2. 장비 연결 직후 단위를 강제로 TORR 변경 [추가된 부분]
        // =========================================================
        _serialPort.DiscardInBuffer();
        
        // 254(글로벌 주소)를 사용하여 압력 단위를 TORR 세팅하는 명령어 전송
        string setUnitCommand = "@253U!TORR\\"; 
        byte[] cmdBytes = Encoding.ASCII.GetBytes(setUnitCommand);
        
        await _serialPort.BaseStream.WriteAsync(cmdBytes, 0, cmdBytes.Length, cancellationToken);
        await _serialPort.BaseStream.FlushAsync(cancellationToken);
        
        // 장비가 단위 설정을 완료할 수 있도록 아주 잠깐 대기 (0.1초)
        await Task.Delay(100, cancellationToken); 
        
        // 돌아온 응답 찌꺼기 비우기
        _serialPort.DiscardInBuffer();
        // =========================================================

        _isConnected = true;
        // =========================================================
        // ✅ 2. 시리얼 넘버(S/N) 요청 및 이름 변경
        // =========================================================
        string snCmd = "@253SN?\\"; 
        byte[] snBytes = System.Text.Encoding.ASCII.GetBytes(snCmd);
        await _serialPort.BaseStream.WriteAsync(snBytes, 0, snBytes.Length, cancellationToken);
        await _serialPort.BaseStream.FlushAsync(cancellationToken);
        
        _serialPort.NewLine = "\\"; 
        string snResponse = await Task.Run(() => _serialPort.ReadLine(), cancellationToken);
        
        // 응답(예: @253ACK25211122348)에서 숫자만 추출
        string sn = snResponse.Replace("@253ACK", "").Replace("@253ACK", "").Trim();
        if (!string.IsNullOrEmpty(sn))
        {
            DeviceId = $"SENS4_{sn}";
            DisplayName = DeviceId;
        }
        // =========================================================
        // ✅ async Task 메서드로 변경되었으므로 return Task.CompletedTask; 는 삭제합니다.
    }

    public void Disconnect()
    {
        _isConnected = false;
        if (_serialPort?.IsOpen == true)
        {
            _serialPort.Close();
        }
        _serialPort?.Dispose();
    }

    public async Task<GaugeReading> ReadOnceAsync(CancellationToken cancellationToken = default)
    {
        if (_serialPort == null || !_serialPort.IsOpen)
            throw new Exception("포트가 열려있지 않습니다.");

        // 버퍼 비우기 (이전 찌꺼기 데이터 제거)
        _serialPort.DiscardInBuffer();

        // ---------------------------------------------------------
        // 1. 명령어 전송
        // ---------------------------------------------------------
        // 주의: 만약 터미널에서 보신 '\'가 실제 백슬래시 문자가 아니라
        // '엔터(Carriage Return)'를 의미하는 기호였다면 "@253P?\r" 로 변경해 주세요.
        string command = "@253P?\\"; 
        byte[] commandBytes = System.Text.Encoding.ASCII.GetBytes(command);
        
        await _serialPort.BaseStream.WriteAsync(commandBytes, 0, commandBytes.Length, cancellationToken);
        await _serialPort.BaseStream.FlushAsync(cancellationToken);

        // ---------------------------------------------------------
        // 2. 응답 읽기
        // ---------------------------------------------------------
        // 장비가 응답의 끝을 '\'로 알려준다고 설정합니다.
        // (엔터로 끝난다면 "\r" 로 변경하세요)
        _serialPort.NewLine = "\\"; 
        
        // ReadLine()을 호출하면 장비가 '\'를 보낼 때까지 기다렸다가 '\'를 제외한 문자열을 가져옵니다.
        // 즉, rawResponse 에는 "@253ACK7.6506E+02" 가 들어오게 됩니다.
        string rawResponse = await Task.Run(() => _serialPort.ReadLine(), cancellationToken);

        // ---------------------------------------------------------
        // 3. 응답 파싱 (글자 잘라내기)
        // ---------------------------------------------------------
        // "@253ACK" 부분을 공백("")으로 치환하여 지워버립니다.
        string cleanedResponse = rawResponse.Replace("@253ACK", "").Trim();
        
        // 남은 "7.6506E+02" 형태의 문자열을 소수점 숫자로 완벽하게 변환합니다.
        if (!double.TryParse(cleanedResponse, 
            System.Globalization.NumberStyles.Float, 
            System.Globalization.CultureInfo.InvariantCulture, 
            out double pressureValue))
        {
            throw new Exception($"데이터 변환 실패. 원본 데이터: {rawResponse}");
        }

        // ---------------------------------------------------------
        // 4. 공통 규격(GaugeReading)에 담아서 리턴
        // ---------------------------------------------------------
        var reading = new GaugeReading
        {
            DeviceId = this.DeviceId,
            Timestamp = DateTimeOffset.UtcNow,
            PressureTorr = pressureValue, // 추출한 압력 값 (765.06)
            RawUnit = "Torr",             // 장비 매뉴얼 상의 기본 단위
            RawResponse = rawResponse + "\\", // 디버깅용 (잘려나간 \ 복구해서 기록)
            Status = GaugeStatus.Ok
        };

        // 데이터가 들어왔음을 알림 (뷰어의 차트가 업데이트됨)
        ReadingReceived?.Invoke(this, reading);
        
        return reading;
    }

    public void Dispose() => Disconnect();
}