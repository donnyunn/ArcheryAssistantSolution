using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SharpDX.Direct3D11;

namespace MultiWebcamApp
{
    public class PressurePadSource : IFrameSource
    {
        private readonly string[] _portNames = { "COM3", "COM4", "COM5", "COM6" };
        private readonly RJCP.IO.Ports.SerialPortStream[] _ports;
        private readonly ConcurrentQueue<(ushort[] Data, long Timestamp)> _dataQueue;
        private CancellationTokenSource _cts;
        private Task _captureTask;
        private Task[] _requestTasks;
        private ushort[] _combinedData = new ushort[9216]; // 96x96
        private bool _isStopped; 
        private bool _isInitialized;
        private Calibration _calibration = new Calibration();

        private const int PACKET_SIZE = 4622;
        private const int DATA_OFFSET = 11;
        private const int HEADER_SIZE = 7;
        private const int DATA_SIZE = 4608;
        private const int TIMEOUT_MS = 100;
        private static readonly byte[] HEADER_PATTERN = { 0x53, 0x41, 0x31, 0x41, 0x33, 0x35, 0x35 };
        private static readonly byte[] TAIL_PATTERN = { 0x46, 0x46, 0x45 };

        private const int REQUEST_SIZE = 14;
        private static readonly byte[] REQUEST_PATTERN = { 0x53, 0x41, 0x33, 0x41, 0x31, 0x35, 0x35, 0x00, 0x00, 0x00, 0x00, 0x46, 0x46, 0x45 };

        private static readonly Point[] StartPositions = new Point[]
        {
            new Point(0, 0),   // COM3: 1사분면 (좌측 상단)
            new Point(0, 48),  // COM4: 2사분면 (좌측 하단)
            new Point(48, 0),  // COM5: 3사분면 (우측 상단)
            new Point(48, 48)  // COM6: 4사분면 (우측 하단)
        };

        Multimedia.Timer _requestTimer;

        public PressurePadSource()
        {
            _ports = new RJCP.IO.Ports.SerialPortStream[_portNames.Length];
            _dataQueue = new ConcurrentQueue<(ushort[], long)>();
            _cts = new CancellationTokenSource();

            // COM 포트 초기화 시도
            InitializePorts();

            //_requestTimer = new Multimedia.Timer()
            //{
            //    Period = 16,
            //    Resolution = 1,
            //    Mode = Multimedia.TimerMode.Periodic
            //};
            //_requestTimer.Tick += new EventHandler(RequestTimer_Tick);
        }

        private void InitializePorts()
        {
            _isInitialized = false;
            for (int i = 0; i < _portNames.Length; i++)
            {
                try
                {
                    _ports[i] = new RJCP.IO.Ports.SerialPortStream(_portNames[i], 3000000, 8, RJCP.IO.Ports.Parity.None, RJCP.IO.Ports.StopBits.One)
                    {
                        ReadTimeout = 50,
                        ReadBufferSize = 16384
                    };
                    _ports[i].Open();
                    _isInitialized = true;
                    Console.WriteLine($"Opened COM port: {_portNames[i]}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to open COM port {_portNames[i]}: {ex.Message}");
                    _ports[i] = null;
                }
            }

            if (!_isInitialized)
            {
                Console.WriteLine("No pressure pad devices initialized. Running in camera-only mode.");
            }
        }

        public FrameData CaptureFrame()
        {
            if (!_isInitialized) return new FrameData { PressureData = null, Timestamp = DateTime.Now.Ticks };
            if (_dataQueue.TryDequeue(out var data))
            {
                return new FrameData { PressureData = data.Data, Timestamp = data.Timestamp };
            }
            return new FrameData { Timestamp = DateTime.Now.Ticks };
        }

        public void Start()
        {
            if (!_isInitialized || _requestTasks != null) return; // 디바이스 없으면 실행하지 않음

            _cts = new CancellationTokenSource();
            _isStopped = false;

            // 유효한 포트만 대상으로 요청 및 캡처 시작
            var activePorts = _ports.Where(p => p != null && p.IsOpen).ToArray();
            //_requestTasks = _ports.Select(port => Task.Run(() => RequestLoop(port, _cts.Token))).ToArray();
            //_requestTimer.Start();
            _captureTask = Task.Run(() => CaptureLoop(activePorts, _cts.Token));
        }

        public async void Stop()
        {
            if (_isStopped || !_isInitialized) return;
            _isStopped = true;

            _cts.Cancel();
            try
            {
                //_requestTimer.Stop();
                //_requestTimer?.Dispose();
                if (_captureTask != null)
                    await _captureTask;
                if (_requestTasks != null)
                    await Task.WhenAll(_requestTasks);
                Console.WriteLine("PressurePadSource stopped successfully.");
            }
            catch (TaskCanceledException)
            {
                Console.WriteLine("Capture and request tasks were canceled as expected.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Stop error: {ex.Message}");
            }

            _requestTasks = null;
            _captureTask = null;
        }

        public void ResetAllPorts()
        {
            Console.WriteLine("Resetting all pressure pad ports...");

            if (!_isStopped)
            {
                Task.Run(() => Stop()).Wait();
            }

            foreach (var port in _ports.Where(p => p != null))
            {
                try
                {
                    if (port.IsOpen)
                    {
                        port.Close();
                        Console.WriteLine($"Closed COM port: {port.PortName}");
                    }
                    port.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error closing COM port {port.PortName}: {ex.Message}");
                }
            }

            Task.Delay(1000).Wait();

            InitializePorts();

            if (_isInitialized)
            {
                Start();
                Console.WriteLine("Pressure pad ports reset and restarted successfully.");
            }
            else
            {
                Console.WriteLine("Failed to reset pressure pad ports. Continuing in camera-only mode.");
            }
        }

        //private void RequestTimer_Tick(object? sender, EventArgs e)
        //{
        //    _requestTasks = _ports.Select(port => Task.Run(() => Request(port, _cts.Token))).ToArray();
        //}

        private async Task Request(RJCP.IO.Ports.SerialPortStream port, CancellationToken token)
        {
            if (port.IsOpen)
            {
                //lock (port)
                //{
                    try
                    {
                        await port.WriteAsync(REQUEST_PATTERN, 0, REQUEST_SIZE, token);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Request error on {port.PortName}: {ex.Message}");
                    }
            //}
        }
        }

        private void RequestLoop(RJCP.IO.Ports.SerialPortStream port, CancellationToken token)
        {
            byte[] request = { 0x53, 0x41, 0x33, 0x41, 0x31, 0x35, 0x35, 0x00, 0x00, 0x00, 0x00, 0x46, 0x46, 0x45 };
            while (!token.IsCancellationRequested)
            {
                if (port.IsOpen)
                {
                    lock (port)
                    {
                        try
                        {
                            port.Write(request, 0, request.Length);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Request error on {port.PortName}: {ex.Message}");
                        }
                    }
                }
                Thread.Sleep(10); // 10ms 간격으로 요청 전송
            }
        }

        private async Task CaptureLoop(RJCP.IO.Ports.SerialPortStream[] activePorts, CancellationToken token)
        {
            var stopwatch = new System.Diagnostics.Stopwatch();
            stopwatch.Start();

            byte[][] buffers = activePorts.Select(_ => new byte[PACKET_SIZE]).ToArray();
            ushort[][] quadrantData = activePorts.Select(_ => new ushort[DATA_SIZE / 2]).ToArray();

            _requestTasks = _ports.Select(port => Task.Run(() => Request(port, _cts.Token))).ToArray();
            await Task.WhenAll(_requestTasks);

            while (!token.IsCancellationRequested)
            {
                var startTime = stopwatch.ElapsedMilliseconds;

                var tasks = activePorts.Select((port, index) => Task.Run(async () =>
                {
                    if (port == null || !port.IsOpen) return; // null 체크 추가

                    byte[] buffer = buffers[index];
                    Array.Clear(buffer, 0, buffer.Length);
                    int totalBytesRead = 0;
                    var packetStartTime = DateTime.Now;

                    // 헤더 탐지
                    while (!token.IsCancellationRequested)
                    {
                        if ((DateTime.Now - packetStartTime).TotalMilliseconds > TIMEOUT_MS)
                        {
                            port.DiscardInBuffer();
                            _ = Task.Run(() => Request(port, token), token);
                            return;
                        }

                        int bytesToRead = Math.Min(port.BytesToRead, HEADER_SIZE - totalBytesRead);
                        if (bytesToRead > 0)
                        {
                            try
                            {
                                int bytesRead = await port.ReadAsync(buffer, totalBytesRead, bytesToRead, token);
                                totalBytesRead += bytesRead;

                                int headerStart = FindHeader(buffer, totalBytesRead);
                                if (headerStart >= 0)
                                {
                                    totalBytesRead = totalBytesRead - headerStart;
                                    if (headerStart > 0)
                                        Array.Copy(buffer, headerStart, buffer, 0, totalBytesRead);
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Header read error on {port.PortName}: {ex.Message}");
                                port.DiscardInBuffer();
                                _ = Task.Run(() => Request(port, token), token);
                                return;
                            }
                        }
                        await Task.Delay(1, token);
                    }

                    if (totalBytesRead < HEADER_SIZE) return;

                    // 데이터 길이 읽기
                    while (totalBytesRead < HEADER_SIZE + 4 && !token.IsCancellationRequested)
                    {
                        if ((DateTime.Now - packetStartTime).TotalMilliseconds > TIMEOUT_MS)
                        {
                            port.DiscardInBuffer();
                            _ = Task.Run(() => Request(port, token), token);
                            return;
                        }

                        int bytesToRead = Math.Min(port.BytesToRead, HEADER_SIZE + 4 - totalBytesRead);
                        if (bytesToRead > 0)
                        {
                            try
                            {
                                int bytesRead = await port.ReadAsync(buffer, totalBytesRead, bytesToRead, token);
                                totalBytesRead += bytesRead;
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Length read error on {port.PortName}: {ex.Message}");
                                port.DiscardInBuffer();
                                _ = Task.Run(() => Request(port, token), token);
                                return;
                            }
                        }
                        await Task.Delay(1, token);
                    }

                    string lengthHex = System.Text.Encoding.ASCII.GetString(buffer, HEADER_SIZE, 4);
                    int dataLength = Convert.ToInt32(lengthHex, 16);
                    if (dataLength != DATA_SIZE)
                    {
                        port.DiscardInBuffer();
                        _ = Task.Run(() => Request(port, token), token);
                        return;
                    }

                    // 데이터와 테일 읽기
                    while (totalBytesRead < PACKET_SIZE && !token.IsCancellationRequested)
                    {
                        if ((DateTime.Now - packetStartTime).TotalMilliseconds > TIMEOUT_MS)
                        {
                            port.DiscardInBuffer();
                            _ = Task.Run(() => Request(port, token), token);
                            return;
                        }

                        int bytesToRead = Math.Min(port.BytesToRead, PACKET_SIZE - totalBytesRead);
                        if (bytesToRead > 0)
                        {
                            try
                            {
                                int bytesRead = await port.ReadAsync(buffer, totalBytesRead, bytesToRead, token);
                                totalBytesRead += bytesRead;
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Data read error on {port.PortName}: {ex.Message}");
                                port.DiscardInBuffer();
                                _ = Task.Run(() => Request(port, token), token);
                                return;
                            }
                        }
                        await Task.Delay(1, token);
                    }

                    if (totalBytesRead == PACKET_SIZE && CheckTail(buffer, PACKET_SIZE - 3))
                    {
                        port.DiscardInBuffer();
                        _ = Task.Run(() => Request(port, token), token);

                        for (int i = 0; i < DATA_SIZE / 2; i++)
                        {
                            quadrantData[index][i] = BitConverter.ToUInt16(buffer, DATA_OFFSET + i * 2);
                        }

                        // Calibration 적용 (선택적)
                         quadrantData[index] = _calibration.Work(quadrantData[index], index);

                        int portIndex = Array.IndexOf(_portNames, port.PortName);
                        Point startPos = StartPositions[portIndex];
                        lock (_combinedData)
                        {
                            for (int col = 0; col < 48; col++)
                            {
                                for (int row = 0; row < 48; row++)
                                {
                                    int srcIndex = col * 48 + row;
                                    int destRow, destCol;

                                    // 사분면별 매핑 조정
                                    switch (portIndex)
                                    {
                                        case 0: // 좌측 상단: Y축 대칭
                                            destRow = (int)startPos.Y + (47 - row);
                                            destCol = (int)startPos.X + (47 - col);
                                            break;
                                        case 1: // 좌측 하단: Y축 대칭
                                            destRow = (int)startPos.Y + (47 - row);
                                            destCol = (int)startPos.X + col;
                                            break;
                                        case 2: // 우측 상단: X축 대칭
                                            destRow = (int)startPos.Y + row;
                                            destCol = (int)startPos.X + (47 - col);
                                            break;
                                        case 3: // 우측 하단: 정상
                                            destRow = (int)startPos.Y + row;
                                            destCol = (int)startPos.X + col;
                                            break;
                                        default:
                                            return;
                                    }
                                    int destIndex = destRow * 96 + destCol;
                                    _combinedData[destIndex] = quadrantData[index][srcIndex];
                                }
                            }
                        }
                    }
                }, token)).ToArray();

                try
                {
                    await Task.WhenAll(tasks);
                }
                catch (TaskCanceledException)
                {
                    // 취소로 인한 예외는 정상 종료로 간주
                    Console.WriteLine("Capture tasks canceled.");
                    return;
                }

                _dataQueue.Enqueue(((ushort[])_combinedData.Clone(), DateTime.Now.Ticks));
                while (_dataQueue.Count > 5)
                    _dataQueue.TryDequeue(out _);

                var elapsed = stopwatch.ElapsedMilliseconds - startTime;
                //Console.WriteLine($"Capture time: {elapsed}ms");
                var delay = 33 - elapsed;
                if (delay > 0) Thread.Sleep((int)delay);
            }
        }

        private int FindHeader(byte[] buffer, int length)
        {
            for (int i = 0; i <= length - HEADER_PATTERN.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < HEADER_PATTERN.Length; j++)
                {
                    if (buffer[i + j] != HEADER_PATTERN[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }

        private bool CheckTail(byte[] buffer, int startIndex)
        {
            for (int i = 0; i < TAIL_PATTERN.Length; i++)
            {
                if (buffer[startIndex + i] != TAIL_PATTERN[i]) return false;
            }
            return true;
        }

        // 프로그램 종료 시 리소스 정리
        ~PressurePadSource()
        {
            foreach (var port in _ports.Where(p => p != null && p.IsOpen))
            {
                port.Close();
                port.Dispose();
            }
        }
    }

    public class Calibration
    {
        private Dictionary<int, Dictionary<int, List<CalibrationRange>>> calibrationFactors;
        private readonly object lockObject = new object();
        private bool isInitialized = false;

        private class CalibrationRange
        {
            public float AdcThreshold { get; set; }
            public float Slope { get; set; }
            public float Intercept { get; set; }
        }

        public Calibration()
        {
            LoadCalibrationFiles();
        }

        private void LoadCalibrationFiles()
        {
            lock (lockObject)
            {
                try
                {
                    calibrationFactors = new Dictionary<int, Dictionary<int, List<CalibrationRange>>>();

                    // 각 사분면에 대한 보정 파일 로드 (1.csv, 2.csv, 3.csv, 4.csv)
                    for (int quadrant = 0; quadrant < 4; quadrant++)
                    {
                        string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{quadrant + 1}.csv");
                        if (File.Exists(filePath))
                        {
                            calibrationFactors[quadrant] = LoadCalibrationFile(filePath);
                            Console.WriteLine($"사분면 {quadrant + 1}의 보정 파일을 성공적으로 로드했습니다.");
                        }
                        else
                        {
                            // 파일이 없으면 기본값 설정
                            var defaultCalibration = new Dictionary<int, List<CalibrationRange>>();

                            // 모든 센서 인덱스(1~2304)에 대한 기본값 설정
                            for (int sensorIndex = 1; sensorIndex <= 2304; sensorIndex++)
                            {
                                defaultCalibration[sensorIndex] = new List<CalibrationRange>
                                {
                                    new CalibrationRange { AdcThreshold = float.MaxValue, Slope = 1.0f, Intercept = 0.0f }
                                };
                            }

                            calibrationFactors[quadrant] = defaultCalibration;
                            Console.WriteLine($"경고: 사분면 {quadrant + 1}의 보정 파일이 없습니다. 기본값(1.0)을 사용합니다.");
                        }
                    }
                    isInitialized = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"보정 파일 로드 중 오류 발생: {ex.Message}");
                    // 초기화 실패 시 기본값으로 설정
                    InitializeDefaultCalibration();
                }
            }
        }

        private void InitializeDefaultCalibration()
        {
            calibrationFactors = new Dictionary<int, Dictionary<int, List<CalibrationRange>>>();

            for (int quadrant = 0; quadrant < 4; quadrant++)
            {
                var defaultCalibration = new Dictionary<int, List<CalibrationRange>>();

                // 모든 센서 인덱스(1~2304)에 대한 기본값 설정
                for (int sensorIndex = 1; sensorIndex <= 2304; sensorIndex++)
                {
                    defaultCalibration[sensorIndex] = new List<CalibrationRange>
                    {
                        new CalibrationRange { AdcThreshold = float.MaxValue, Slope=1.0f, Intercept = 0.0f }
                    };
                }

                calibrationFactors[quadrant] = defaultCalibration;
            }

            isInitialized = true;
            Console.WriteLine("모든 사분면에 기본 보정값(1.0)을 적용했습니다.");
        }

        private Dictionary<int, List<CalibrationRange>> LoadCalibrationFile(string filePath)
        {
            var sensorCalibration = new Dictionary<int, List<CalibrationRange>>();
            List<int> sensorIndices = new List<int>();

            try
            {
                string[] lines = File.ReadAllLines(filePath);

                int currentLineIndex = 0;
                int sectionCount = 0;

                while (currentLineIndex < lines.Length)
                {
                    string[] columns = lines[currentLineIndex++].Split(',');

                    // 헤더 행 건너뛰기
                    if (columns.Length > 0 && (columns[0] == "Calibration Result" || columns[0] == "Pressure"))
                        continue;

                    // 인덱스 행 처리
                    if (columns.Length > 0 && (columns[0] == ""))
                    {
                        for (int col = 2; col < columns.Length; col++)
                        {
                            if (int.TryParse(columns[col], out int index))
                            {
                                sensorIndices.Add(index);
                                sensorCalibration.Add(index, new List<CalibrationRange>());
                            }
                        }
                        continue;
                    }

                    // Adc 행 처리
                    if (columns.Length > 0 && columns[0] == "Adc")
                    {
                        for (int col = 0, i = 0; col < columns.Length; col++)
                        {
                            if (float.TryParse(columns[col], NumberStyles.Any, CultureInfo.InvariantCulture, out float adcValue))
                            {
                                CalibrationRange calibrationRange = new CalibrationRange { AdcThreshold = adcValue };
                                sensorCalibration[sensorIndices[i++]].Add(calibrationRange);
                            }
                        }
                        sectionCount++;
                        continue;
                    }

                    // Slope 행 처리
                    if (columns.Length > 0 && columns[0] == "Slope")
                    {
                        for (int col = 0, i = 0; col < columns.Length; col++)
                        {
                            if (float.TryParse(columns[col], NumberStyles.Any, CultureInfo.InvariantCulture, out float slopeValue))
                            {
                                sensorCalibration[sensorIndices[i++]][sectionCount - 1].Slope = slopeValue;
                            }
                        }
                        continue;
                    }

                    // Intercept 행 처리
                    if (columns.Length > 0 && columns[0] == "Intercept")
                    {
                        for (int col = 0, i = 0; col < columns.Length; col++)
                        {
                            if (float.TryParse(columns[col], NumberStyles.Any, CultureInfo.InvariantCulture, out float interceptValue))
                            {
                                sensorCalibration[sensorIndices[i++]][sectionCount - 1].Intercept = interceptValue;
                            }
                        }
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"파일 읽기 오류 ({filePath}): {ex.Message}");
                // 오류 발생 시 기본값 1.0으로 설정된 배열 반환
            }

            return sensorCalibration;
        }

        public ushort[] Work(ushort[] rawData, int quadrant)
        {
            // 초기화되지 않았거나 해당 사분면 데이터가 없으면 원본 반환
            if (!isInitialized || !calibrationFactors.ContainsKey(quadrant))
            {
                return rawData;
            }

            lock (lockObject)
            {
                ushort[] calibratedData = new ushort[rawData.Length];

                for (int i = 0; i < rawData.Length; i++)
                {
                    int sensorIndex = i + 1;

                    if (calibrationFactors[quadrant].ContainsKey(sensorIndex))
                    {
                        var ranges = calibrationFactors[quadrant][sensorIndex];
                        float adcValue = rawData[i];

                        // 적절한 보정 범위 찾기
                        CalibrationRange selectedRange = null;
                        foreach (var range in ranges)
                        {
                            if (adcValue <= range.AdcThreshold)
                            {
                                selectedRange = range;
                                break;
                            }
                        }

                        // 범위를 찾지 못했으면 마지막 범위 사용
                        if (selectedRange == null && ranges.Count > 0)
                        {
                            selectedRange = ranges.Last();
                        }

                        // 보정 실시
                        if (selectedRange != null)
                        {
                            float calibratedValue = selectedRange.Slope * adcValue + selectedRange.Intercept;

                            // 음수 값 방지
                            calibratedValue = Math.Max(0, calibratedValue);
                            //calibratedValue = Math.Min(ushort.MaxValue, calibratedValue);

                            calibratedData[i] = (ushort)Math.Round(calibratedValue);
                        }
                        else
                        {
                            // 보정 정보가 없으면 원본 값 유지
                            calibratedData[i] = rawData[i];
                        }
                    }
                    else
                    {
                        // 센서 인덱스에 대한 보정 정보가 없으면 원본 값 유지
                        calibratedData[i] = rawData[i];
                    }
                }

                return calibratedData;
            }
        }

        public void ReloadCalibration()
        {
            lock (lockObject)
            {
                Console.WriteLine("보정 파일을 다시 로드합니다...");
                LoadCalibrationFiles();
            }
        }

        public bool IsCalibrationInitialized()
        {
            return isInitialized;
        }
    }
}
