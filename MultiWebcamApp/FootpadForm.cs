using System;
using System.IO.Ports;
using System.Linq;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Documents;

namespace MultiWebcamApp
{
    public class FootpadDevice
    {
        public SerialPort Port {  get; private set; }
        public int QuadrantIndex { get; private set; }
        public ushort[] LastData { get; private set; }
        private static readonly byte[] RequestPacket = new byte[] { 0x53, 0x41, 0x33, 0x41, 0x31, 0x35, 0x35, 0x00, 0x00, 0x00, 0x00, 0x46, 0x46, 0x45 };
        private bool isProcessing = false;

        public FootpadDevice(string portName, int quadrantIndex)
        {
            QuadrantIndex = quadrantIndex;
            LastData = new ushort[48 * 48];
            InitializePort(portName);
        }

        private void InitializePort(string portName)
        {
            try
            {
                Port = new SerialPort(portName, 3000000, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = 100,
                    WriteTimeout = 100,
                    ReadBufferSize = 16384,
                };
                Port.Open();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to open oprt {portName}: {ex.Message}");
            }
        }

        public bool RequestAndReceiveData()
        {
            if (!Port?.IsOpen == true || isProcessing) return false;

            isProcessing = true;

            try
            {
                // Clear buffers
                if (Port.BytesToRead > 1000)
                {
                    Port.DiscardInBuffer();
                    Port.DiscardOutBuffer();
                }

                // Send request
                Port.Write(RequestPacket, 0, RequestPacket.Length);

                // 예상되는 전체 응답 크기
                const int EXPECTED_SIZE = 4622;  // 48*48*2 + overhead
                byte[] buffer = new byte[EXPECTED_SIZE];
                int totalBytesRead = 0;

                // 타임아웃 설정
                var startTime = DateTime.Now;
                const int TIMEOUT_MS = 100;  // 1000ms에서 100ms로 감소

                // 2단계 읽기 접근법: 첫 번째로 헤더를 찾고, 그 다음 나머지 데이터를 읽음
                // 헤더 읽기 (최대 20바이트 정도면 충분)
                const int HEADER_SIZE = 20;
                int headerBytesRead = 0;
                while (headerBytesRead < HEADER_SIZE)
                {
                    if ((DateTime.Now - startTime).TotalMilliseconds > TIMEOUT_MS)
                        return false;

                    int bytesToRead = Port.BytesToRead;
                    if (bytesToRead == 0) continue;

                    int maxRead = Math.Min(bytesToRead, HEADER_SIZE - headerBytesRead);
                    int bytesRead = Port.Read(buffer, headerBytesRead, maxRead);

                    if (bytesRead == 0) continue;

                    headerBytesRead += bytesRead;
                }

                // 헤더 확인
                int headerStart = FindHeader(buffer, headerBytesRead);
                if (headerStart == -1) 
                    return false;

                // 길이 확인
                int dataLength = GetDataLength(buffer, headerStart);
                if (dataLength != 4608)
                    return false;

                // 총 패킷 길이 계산
                int totalPacketLength = headerStart + 11 + dataLength + 3;
                totalBytesRead = headerBytesRead;

                // 나머지 데이터 읽기
                while (totalBytesRead < totalPacketLength)
                {
                    // 타임아웃 체크
                    if ((DateTime.Now - startTime).TotalMilliseconds > TIMEOUT_MS)
                        return false;

                    int bytesToRead = Port.BytesToRead;
                    if (bytesToRead == 0) continue;

                    int maxRead = Math.Min(bytesToRead, totalPacketLength - totalBytesRead);
                    int bytesRead = Port.Read(buffer, totalBytesRead, maxRead);

                    if (bytesRead == 0) continue;

                    totalBytesRead += bytesRead;
                }

                // 테일 확인
                if (!CheckTail(buffer, totalPacketLength - 3))
                    return false;

                // 데이터 처리
                ProcessData(buffer, headerStart + 11, dataLength);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in device {QuadrantIndex}: {ex.Message}");
            }
            finally
            {
                isProcessing = false;
            }

            return false;
        }

        private bool ProcessBuffer(byte[] buffer, int totalBytesRead)
        {
            try
            {
                // 최소 패킷 크기 확인 (헤더(7) + 길이필드(4) + 테일(3))
                if (totalBytesRead < 14) return false;

                // 헤더 찾기 (SA1A355)
                int headerStart = -1;
                for (int i = 0; i <= totalBytesRead - 7; i++)
                {
                    if (buffer[i] == 0x53 && buffer[i + 1] == 0x41 &&
                        buffer[i + 2] == 0x31 && buffer[i + 3] == 0x41 &&
                        buffer[i + 4] == 0x33 && buffer[i + 5] == 0x35 &&
                        buffer[i + 6] == 0x35)
                    {
                        headerStart = i;
                        break;
                    }
                }

                if (headerStart == -1 || headerStart + 11 >= totalBytesRead)
                    return false;

                // 데이터 길이 확인 (4바이트 ASCII HEX)
                string lengthHex = System.Text.Encoding.ASCII.GetString(buffer, headerStart + 7, 4);
                int dataLength;
                if (!int.TryParse(lengthHex, System.Globalization.NumberStyles.HexNumber, null, out dataLength))
                    return false;

                // 데이터 길이 유효성 검사 (48*48*2 = 4608)
                if (dataLength != 4608)
                    return false;

                // 전체 패킷 길이 확인
                int totalPacketLength = headerStart + 11 + dataLength + 3; // 헤더시작 + 헤더나머지(11) + 데이터길이 + 테일(3)
                //if (totalBytesRead < totalPacketLength)
                //    return false;

                // 테일 체크 (FFE)
                if (buffer[totalPacketLength - 3] != 0x46 ||
                    buffer[totalPacketLength - 2] != 0x46 ||
                    buffer[totalPacketLength - 1] != 0x45)
                    return false;

                // 데이터 처리 (48x48 array of 2-byte values)
                ProcessData(buffer, headerStart + 11, dataLength);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private int FindHeader(byte[] buffer, int length)
        {
            for (int i = 0; i <= length - 7; i++)
            {
                if (buffer[i] == 0x53 && buffer[i + 1] == 0x41 &&
                    buffer[i + 2] == 0x31 && buffer[i + 3] == 0x41 &&
                    buffer[i + 4] == 0x33 && buffer[i + 5] == 0x35 &&
                    buffer[i + 6] == 0x35)
                {
                    return i;
                }
            }
            return -1;
        }

        private int GetDataLength(byte[] buffer, int headerStart)
        {
            try
            {
                string lengthHex = System.Text.Encoding.ASCII.GetString(buffer, headerStart + 7, 4);
                return Convert.ToInt32(lengthHex, 16);
            }
            catch
            {
                return -1;
            }
        }

        private bool CheckTail(byte[] buffer, int tailStart)
        {
            return buffer[tailStart] == 0x46 &&
                   buffer[tailStart + 1] == 0x46 &&
                   buffer[tailStart + 2] == 0x45;
        }

        private void ProcessData(byte[] buffer, int dataStart, int dataLength)
        {
            ushort[] newData = new ushort[48 * 48];
            int numValues = Math.Min(dataLength / 2, newData.Length);

            for (int i = 0; i < numValues; i++)
            {
                newData[i] = BitConverter.ToUInt16(buffer, dataStart + (i * 2));
            }

            LastData = newData;
        }

        public void Close()
        {
            if (Port?.IsOpen == true)
            {
                Port.Close();
                Port.Dispose();
            }
        }
    }

    public partial class FootpadForm : Form
    {
        private Dictionary<int, FootpadDevice> devices = new Dictionary<int, FootpadDevice>();
        private PressureMapViewer.MainWindow pressureMapWindow;
        private readonly object dataLock = new object();
        private bool isRunning = false;

        private System.Windows.Forms.Timer _renderTimer;

        public FootpadForm()
        {
            InitializeComponent();
            InitializeDevices();

            _renderTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _renderTimer.Tick += (s, e) => renderTick(s, e);
            _renderTimer.Start();
        }

        private void renderTick(Object? sender, EventArgs e)
        {
            if (isRunning)
            {
                isRunning = false;
            }
        }

        private void InitializeDevices()
        {
            var ports = SerialPort.GetPortNames()
                .Where(p => p != "COM1")
                .OrderBy(p => p)
                .ToList();

            // 최대 4개의 포트만 사용
            for (int i = 0; i < Math.Min(4, ports.Count); i++)
            {
                devices[i] = new FootpadDevice(ports[i], i);
            }
        }

        private void FootpadForm_Load(object sender, EventArgs e)
        {
            pressureMapWindow = new PressureMapViewer.MainWindow();

            Screen currentScreen = Screen.FromControl(this);
            Rectangle screenBounds = currentScreen.Bounds;

            pressureMapWindow.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            pressureMapWindow.WindowStyle = System.Windows.WindowStyle.None;
            pressureMapWindow.ResizeMode = System.Windows.ResizeMode.NoResize;

            pressureMapWindow.Left = screenBounds.Left;
            pressureMapWindow.Top = screenBounds.Top;
            pressureMapWindow.Width = screenBounds.Width;
            pressureMapWindow.Height = screenBounds.Height;
            
            pressureMapWindow.Show();
        }

        private ushort[] combinedData = new ushort[9216]; // 96 * 96
        private int lastProcessedQuadrant = 3;
        public bool UpdateFrame()
        {
            bool ret = false;
            if (isRunning) return ret;
            _renderTimer.Start();
            isRunning = true;

            try
            {
                // 순환식으로 사분면 선택 (0->1->2->3->0->...)
                int currentQuadrant = (lastProcessedQuadrant + 1) % 4;
                lastProcessedQuadrant = currentQuadrant;

                // 선택된 사분면만 처리
                if (devices.ContainsKey(currentQuadrant))
                {
                    var device = devices[currentQuadrant];

                    // 현재 사분면 데이터 수집
                    bool deviceHasData = device.RequestAndReceiveData();

                    if (deviceHasData)
                    {
                        // 현재 사분면의 위치 계산
                        int baseRow = (currentQuadrant / 2) * 48;
                        int baseCol = (currentQuadrant % 2) * 48;
                        var deviceData = device.LastData;

                        // 해당 사분면 영역만 업데이트
                        for (int i = 0; i < 48; i++)
                        {
                            for (int j = 0; j < 48; j++)
                            {
                                combinedData[(baseRow + i) * 96 + (baseCol + j)] = deviceData[i * 48 + j];
                            }
                        }

                        // UI 업데이트
                        pressureMapWindow.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            pressureMapWindow.UpdatePressureData(combinedData);
                        }), System.Windows.Threading.DispatcherPriority.Background);

                        ret = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating frame: {ex.Message}");
                ret = false;
            }
            finally
            {
                isRunning = false;
                //_renderTimer.Stop();
            }

            return ret;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            isRunning = false;
            foreach (var device in devices.Values)
            {
                device.Close();
            }
            base.OnFormClosed(e);
        }
    }
}