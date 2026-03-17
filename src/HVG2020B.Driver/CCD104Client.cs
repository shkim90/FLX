using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using HVG2020B.Core;

namespace HVG2020B.Driver;

public class CCD100Client : IGaugeDevice
{
    private SerialPort? _serialPort;
    private bool _isConnected;
    private static int _instanceCount;

    // 수정 전: public string DeviceId { get; }
    public string DeviceId { get; private set; } // ⬅️ private set 추가
    public string DeviceType => "CCD100"; 
    public string DisplayName { get; set; } = "새 CCD100 게이지";
    public bool IsConnected => _isConnected;
    public string? PortName => _serialPort?.PortName;

    public event EventHandler<GaugeReading>? ReadingReceived;

#pragma warning disable CS0067
    public event EventHandler<Exception>? ConnectionLost;
#pragma warning restore CS0067

    public CCD100Client(string? deviceId = null)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            var id = System.Threading.Interlocked.Increment(ref _instanceCount);
            DeviceId = $"CCD100-{id}"; // 예: THIRD-1, THIRD-2
        }
        else
        {
            DeviceId = deviceId.Trim();
        }
    }

    public Task ConnectAsync(string portName, CancellationToken cancellationToken = default)
    {
        _serialPort = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 500,
            WriteTimeout = 500,
            DtrEnable = true,
            RtsEnable = true
        };
        
        _serialPort.Open();

        _isConnected = true;
        return Task.CompletedTask;
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
        try
        {
            if (_serialPort == null || !_serialPort.IsOpen)
                throw new Exception("포트가 열려있지 않습니다.");

            _serialPort.DiscardInBuffer();

            // 1. 명령어 전송 ('ar' 입력. 장비에 따라 끝에 \r 이나 \n 이 필요할 수 있습니다)
            string command = "ar\n"; 
            byte[] commandBytes = Encoding.ASCII.GetBytes(command);
            
            await _serialPort.BaseStream.WriteAsync(commandBytes, 0, commandBytes.Length, cancellationToken);
            await _serialPort.BaseStream.FlushAsync(cancellationToken);

            // 2. 응답 읽기
            // 데이터가 길게 여러 줄로 올 수 있으므로, 약간 대기 후 버퍼에 있는 모든 데이터를 한 번에 읽습니다.
            await Task.Delay(100, cancellationToken);
            string rawResponse = _serialPort.ReadExisting();

            // 3. 정규식(Regex)을 이용해 숫자만 추출
            // 해석: "READ:" 문자열 뒤에 나오는 숫자, 소수점, +, -, E 기호를 모두 찾아냅니다.
            var match = Regex.Match(rawResponse, @"READ:\s*([0-9\.\-E\+]+)");
            
            if (!match.Success)
            {
                throw new Exception($"데이터를 찾을 수 없습니다. 원본: {rawResponse}");
            }

            // 찾은 숫자(0.004)를 실수형으로 변환
            string numberString = match.Groups[1].Value;
            if (!double.TryParse(numberString, 
                System.Globalization.NumberStyles.Float, 
                System.Globalization.CultureInfo.InvariantCulture, 
                out double pressureValue))
            {
                throw new Exception($"숫자 변환 실패: {numberString}");
            }

            // 4. 결과 리턴
            var reading = new GaugeReading
            {
                DeviceId = this.DeviceId,
                Timestamp = DateTimeOffset.UtcNow,
                PressureTorr = pressureValue, 
                RawUnit = "Torr", // 기본 단위
                RawResponse = rawResponse.Replace("\r", "").Replace("\n", " "), // 줄바꿈을 공백으로 바꿔서 저장
                Status = GaugeStatus.Ok
            };

            ReadingReceived?.Invoke(this, reading);
            return reading;
        }
        catch (Exception ex)
        {
            _isConnected = false;
            ConnectionLost?.Invoke(this, ex);
            throw; 
        }
    }
    public void SetCustomId(string customName)
    {
        DeviceId = customName;
        DisplayName = customName;
    }
    public void Dispose() => Disconnect();
}