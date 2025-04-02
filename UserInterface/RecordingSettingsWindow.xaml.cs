using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;

namespace UserInterface
{
    /// <summary>
    /// 녹화 설정 선택 창
    /// </summary>
    public partial class RecordingSettingsWindow : Window
    {
        // 결과 반환을 위한 프로퍼티
        public bool UseDesktop { get; private set; } = true;
        public bool UseVerticalLayout { get; private set; } = true;
        public string SelectedDrive { get; private set; } = string.Empty;

        public RecordingSettingsWindow()
        {
            InitializeComponent();
            LoadUsbDrives();
        }

        // USB 드라이브 목록 로드
        private void LoadUsbDrives()
        {
            cmbUsbDrives.Items.Clear();

            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType == DriveType.Removable && drive.IsReady)
                {
                    string driveInfo = $"{drive.Name} ({drive.VolumeLabel}) - 여유공간: {FormatBytes(drive.AvailableFreeSpace)}";
                    cmbUsbDrives.Items.Add(driveInfo);

                    // 첫 번째 드라이브 자동 선택
                    if (cmbUsbDrives.SelectedIndex == -1)
                        cmbUsbDrives.SelectedIndex = 0;
                }
            }

            // USB 드라이브가 없는 경우
            if (cmbUsbDrives.Items.Count == 0)
            {
                cmbUsbDrives.Items.Add("연결된 USB 드라이브가 없습니다");
                cmbUsbDrives.SelectedIndex = 0;
                rbUsb.IsEnabled = false;
            }
        }

        // 용량 형식화 함수
        private string FormatBytes(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            double dblBytes = bytes;

            while (dblBytes >= 1024 && i < suffixes.Length - 1)
            {
                dblBytes /= 1024;
                i++;
            }

            return $"{dblBytes:0.##} {suffixes[i]}";
        }

        // 확인 버튼 클릭
        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            UseDesktop = rbDesktop.IsChecked ?? true;
            UseVerticalLayout = rbVertical.IsChecked ?? true;

            if (!UseDesktop && cmbUsbDrives.SelectedIndex >= 0 && cmbUsbDrives.SelectedItem.ToString() != "연결된 USB 드라이브가 없습니다")
            {
                string selectedDriveInfo = cmbUsbDrives.SelectedItem.ToString();
                SelectedDrive = selectedDriveInfo.Substring(0, 3); // "D:\" 형식으로 추출
            }

            DialogResult = true;
        }

        // 취소 버튼 클릭
        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}