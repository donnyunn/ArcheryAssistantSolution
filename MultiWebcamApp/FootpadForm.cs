using System;
using System.IO.Ports;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace MultiWebcamApp
{
    public class FootpadDevice
    {
        public SerialPort Port {  get; private set; }
        public int QuadrantIndex { get; private set; }
        public ushort[] LastData { get; set; }
        public bool HasNewData { get; set; }
        public byte[] ReceiveBuffer { get; set; } = new byte[0];

        public FootpadDevice(string portName, int quadrantIndex)
        {
            QuadrantIndex = quadrantIndex;
            LastData = new ushort[48 * 48];
            InitializePort(portName);
        }

        private void InitializePort(string portName)
        {
            Port = new SerialPort(portName, 3000000, Parity.None, 8, StopBits.One);
            try
            {
                Port.Open();
            }
            catch (Exception)
            {

            }
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
        private const int DataRequestInterval = 33;
        private System.Windows.Forms.Timer timer;
        private Dictionary<int, FootpadDevice> devices = new Dictionary<int, FootpadDevice>();
        private PressureMapViewer.MainWindow pressureMapWindow;
        private readonly object dataLock = new object();

        public FootpadForm()
        {
            InitializeComponent();
            InitializeDevices();
            InitializeTimer();
        }

        private void FootpadForm_Load(object sender, EventArgs e)
        {
            pressureMapWindow = new PressureMapViewer.MainWindow();
            pressureMapWindow.Show();

            Screen currentScreen = Screen.FromControl(this);
            Rectangle screenBounds = currentScreen.Bounds;
            pressureMapWindow.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            pressureMapWindow.WindowStyle = System.Windows.WindowStyle.None;
            pressureMapWindow.ResizeMode = System.Windows.ResizeMode.NoResize;

            pressureMapWindow.Left = screenBounds.Left;
            pressureMapWindow.Top = screenBounds.Top;
            pressureMapWindow.Width = screenBounds.Width;
            pressureMapWindow.Height = screenBounds.Height;
            //pressureMapWindow.WindowState = System.Windows.WindowState.Maximized;
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
                var device = new FootpadDevice(ports[i], i);
                devices[i] = device;
                if (device.Port.IsOpen)
                {
                    device.Port.DataReceived += (s, e) => SerialPort_DataReceived(device, e);
                }
            }
        }

        private void CheckForNewDevices()
        {
            var currentPorts = SerialPort.GetPortNames()
                .Where(p => p != "COM1")
                .OrderBy(p => p)
                .ToList();

            // 현재 연결된 포트들 중에서 아직 등록되지 않은 포트 확인
            for (int i = 0; i < Math.Min(4, currentPorts.Count); i++)
            {
                if (!devices.ContainsKey(i) || !devices[i].Port.IsOpen)
                {
                    // 기존 장치가 있다면 정리
                    if (devices.ContainsKey(i))
                    {
                        devices[i].Close();
                    }

                    // 새 장치 초기화
                    var device = new FootpadDevice(currentPorts[i], i);
                    devices[i] = device;
                    if (device.Port.IsOpen)
                    {
                        device.Port.DataReceived += (s, e) => SerialPort_DataReceived(device, e);
                    }
                }
            }
        }

        private void InitializeTimer()
        {
            timer = new System.Windows.Forms.Timer();
            timer.Interval = DataRequestInterval;
            timer.Tick += new EventHandler(Timer_Tick);
            timer.Start();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            SendRequestPacket();
        }

        private void SendRequestPacket()
        {
            byte[] requestPacket = new byte[] { 0x53, 0x41, 0x33, 0x41, 0x31, 0x35, 0x35, 0x00, 0x00, 0x00, 0x00, 0x46, 0x46, 0x45 };
            foreach (var device in devices.Values)
            {
                device.Port.Write(requestPacket, 0, requestPacket.Length);
            }
        }

        private void SerialPort_DataReceived(FootpadDevice device, SerialDataReceivedEventArgs e)
        {
            if (!device.Port.IsOpen) return;

            int bytesToRead = device.Port.BytesToRead;
            if (bytesToRead == 0) return;

            byte[] buffer = new byte[bytesToRead];
            device.Port.Read(buffer, 0, buffer.Length);

            lock (dataLock)
            {
                device.ReceiveBuffer = CombineArrays(device.ReceiveBuffer, buffer);
                ProcessReceivedData(device);
            }
        }

        private void ProcessReceivedData(FootpadDevice device)
        {
            const int minPacketLength = 14;

            while (device.ReceiveBuffer.Length >= minPacketLength)
            {
                // 헤더 확인 (53 41 31 41 33)
                if (!CheckHeader(device.ReceiveBuffer))
                {
                    device.ReceiveBuffer = RemoveFirstByte(device.ReceiveBuffer);
                    continue;
                }

                // 데이터 길이 추출
                if (!TryGetDataLength(device.ReceiveBuffer, out int dataLength))
                {
                    device.ReceiveBuffer = RemoveFirstByte(device.ReceiveBuffer);
                    continue;
                }

                int totalPacketLength = 14 + dataLength;
                if (device.ReceiveBuffer.Length < totalPacketLength) break;

                // 테일 확인
                if (!CheckTail(device.ReceiveBuffer, totalPacketLength))
                {
                    device.ReceiveBuffer = RemoveFirstByte(device.ReceiveBuffer);
                    continue;
                }

                // 패킷 추출 및 처리
                byte[] packet = new byte[totalPacketLength];
                Array.Copy(device.ReceiveBuffer, 0, packet, 0, totalPacketLength);
                device.ReceiveBuffer = RemoveFirstBytes(device.ReceiveBuffer, totalPacketLength);

                ProcessDevicePacket(device, packet);
            }
        }

        private void ProcessDevicePacket(FootpadDevice device, byte[] packet)
        {
            int dataStartIndex = 11;
            int dataLength = packet.Length - 14;
            byte[] dataBytes = new byte[dataLength];
            Array.Copy(packet, dataStartIndex, dataBytes, 0, dataLength);

            // 데이터 디코딩
            ushort[] sensorData = new ushort[dataLength / 2];
            for (int i = 0; i < sensorData.Length; i++)
            {
                sensorData[i] = BitConverter.ToUInt16(dataBytes, i * 2);
            }

            device.LastData = sensorData;
            device.HasNewData = true;

            // 모든 장치에서 새 데이터가 있는지 확인
            if (devices.Values.All(d => d.HasNewData || !d.Port.IsOpen))
            {
                CombineAndUpdateData();
            }
        }

        private void CombineAndUpdateData()
        {
            ushort[] combinedData = new ushort[96 * 96];

            // 4개 영역의 데이터를 96x96 배열로 통합
            for (int i = 0; i < 96; i++)
            {
                for (int j = 0; j < 96; j++)
                {
                    int quadrant = (i < 48 ? 0 : 1) + (j < 48 ? 0 : 2);
                    int sourceI = i % 48;
                    int sourceJ = j % 48;
                    int sourceIndex = sourceI * 48 + sourceJ;

                    if (devices.ContainsKey(quadrant) && devices[quadrant].Port.IsOpen)
                    {
                        combinedData[i * 96 + j] = devices[quadrant].LastData[sourceIndex];
                    }
                    // 없는 영역은 0으로 채움
                }
            }

            // 모든 장치의 HasNewData 플래그 리셋
            foreach (var device in devices.Values)
            {
                device.HasNewData = false;
            }

            // UI 업데이트
            pressureMapWindow?.Dispatcher.Invoke(() =>
            {
                pressureMapWindow.UpdatePressureData(combinedData);
            });
        }

        private void DisplaySensorData(ushort[] sensorData)
        {
            //for (int i = 0; i < sensorData.Length; i++)
            //{
            //    Console.Write(sensorData[i]);
            //}
            //Console.WriteLine();
            //if (pressureMapWindow != null && pressureMapWindow.IsLoaded) 
            {
                pressureMapWindow.Dispatcher.Invoke(() =>
                {
                    pressureMapWindow.UpdatePressureData(sensorData);
                });
            }
        }

        private byte[] CombineArrays(byte[] first, byte[] second)
        {
            byte[] result = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, result, 0, first.Length);
            Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
            return result;
        }

        private byte[] RemoveFirstByte(byte[] array)
        {
            byte[] result = new byte[array.Length - 1];
            Buffer.BlockCopy(array, 1, result, 0, result.Length);
            return result;
        }

        private byte[] RemoveFirstBytes(byte[] array, int count)
        {
            byte[] result = new byte[array.Length - count];
            Buffer.BlockCopy(array, count, result, 0, result.Length);
            return result;
        }

        private bool CheckHeader(byte[] buffer) =>
            buffer[0] == 0x53 && buffer[1] == 0x41 && buffer[2] == 0x31 &&
            buffer[3] == 0x41 && buffer[4] == 0x33;

        private bool CheckTail(byte[] buffer, int totalLength) =>
            buffer[totalLength - 3] == 0x46 && buffer[totalLength - 2] == 0x46 &&
            buffer[totalLength - 1] == 0x45;

        private bool TryGetDataLength(byte[] buffer, out int dataLength)
        {
            try
            {
                string dataLengthHex = Encoding.ASCII.GetString(buffer, 7, 4);
                dataLength = Convert.ToInt32(dataLengthHex, 16);
                return true;
            }
            catch
            {
                dataLength = 0;
                return false;
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            foreach (var device in devices.Values)
            {
                device.Close();
            }
            timer.Stop();
            base.OnFormClosed(e);
        }
    }
}