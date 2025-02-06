using System;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace MultiWebcamApp
{
    public partial class FootpadForm : Form
    {
        private SerialPort serialPort;
        private System.Windows.Forms.Timer timer;
        private const int Baudrate = 3000000;
        private const int DataRequestInterval = 33;
        private byte[] receiveBuffer = new byte[0];

        private PressureMapViewer.MainWindow pressureMapWindow;

        public FootpadForm()
        {
            InitializeComponent();
            InitializeSerialPort();
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

        private void InitializeSerialPort()
        {
            string[] ports = SerialPort.GetPortNames();
            if (ports.Length < 2)
            {
                Console.WriteLine("연결된 COM 포트가 없습니다.");
                return;
            }

            serialPort = new SerialPort(ports[1], Baudrate, Parity.None, 8, StopBits.One);
            serialPort.DataReceived += SerialPort_DataReceived;

            try
            {
                serialPort.Open();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"COM 포트 열기 실패: {ex:Message}");
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
            if (serialPort != null && serialPort.IsOpen)
            {
                serialPort.Write(requestPacket, 0, requestPacket.Length);
            }
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (serialPort.BytesToRead > 0)
            {
                byte[] buffer = new byte[serialPort.BytesToRead];
                serialPort.Read(buffer, 0, buffer.Length);

                receiveBuffer = CombineArrays(receiveBuffer, buffer);

                ProcessReceivedData();
            }
        }

        private void ProcessReceivedData()
        {
            // 패킷의 최소 길이 (헤더 5바이트 + 커맨드 2바이트 + 데이터 길이 4바이트 + 테일 3바이트)
            const int minPacketLength = 14;

            while (receiveBuffer.Length >= minPacketLength)
            {
                // 헤더 확인 (53 41 31 41 33)
                if (receiveBuffer[0] != 0x53 || receiveBuffer[1] != 0x41 || receiveBuffer[2] != 0x31 || receiveBuffer[3] != 0x41 || receiveBuffer[4] != 0x33)
                {
                    // 헤더가 맞지 않으면 첫 번째 바이트 제거
                    receiveBuffer = RemoveFirstByte(receiveBuffer);
                    continue;
                }

                // 데이터 길이 추출 (패킷 7~10: 아스키로 된 헥사 값)
                string dataLengthHex = Encoding.ASCII.GetString(receiveBuffer, 7, 4);
                int dataLength = Convert.ToInt32(dataLengthHex, 16); // 헥사 값을 정수로 변환

                // 전체 패킷 길이 계산 (헤더 5 + 커맨드 2 + 데이터 길이 4 + 데이터 + 테일 3)
                int totalPacketLength = 14 + dataLength;

                // 버퍼에 전체 패킷이 도착했는지 확인
                if (receiveBuffer.Length < totalPacketLength)
                {
                    break; // 아직 패킷이 완전히 도착하지 않음
                }

                // 테일 확인 (46 46 45)
                if (receiveBuffer[totalPacketLength - 3] != 0x46 || receiveBuffer[totalPacketLength - 2] != 0x46 || receiveBuffer[totalPacketLength - 1] != 0x45)
                {
                    // 테일이 맞지 않으면 첫 번째 바이트 제거
                    receiveBuffer = RemoveFirstByte(receiveBuffer);
                    continue;
                }

                // 유효한 패킷 추출
                byte[] packet = new byte[totalPacketLength];
                Array.Copy(receiveBuffer, 0, packet, 0, totalPacketLength);

                // 버퍼에서 처리된 패킷 제거
                receiveBuffer = RemoveFirstBytes(receiveBuffer, totalPacketLength);

                // 데이터 처리
                ProcessResponsePacket(packet);
            }
        }

        private void ProcessResponsePacket(byte[] packet)
        {
            // 데이터 부분 추출 (패킷의 11번째 바이트부터 끝에서 3바이트 전까지)
            int dataStartIndex = 11;
            int dataLength = packet.Length - 14;
            byte[] dataBytes = new byte[dataLength];
            Array.Copy(packet, dataStartIndex, dataBytes, 0, dataLength);

            // 데이터 디코딩 (2바이트씩 uint16_t로 변환)
            ushort[] sensorData = new ushort[dataLength / 2];
            for (int i = 0; i < sensorData.Length; i++)
            {
                sensorData[i] = BitConverter.ToUInt16(dataBytes, i * 2);
            }

            // 화면에 데이터 표시
            DisplaySensorData(sensorData);
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

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (serialPort != null && serialPort.IsOpen)
            {
                serialPort.Close();
            }
            timer.Stop();
            base.OnFormClosed(e);
        }
    }
}