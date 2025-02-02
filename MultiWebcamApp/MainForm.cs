using System;
using System.Windows.Forms;

namespace MultiWebcamApp
{
    public partial class MainForm : Form
    {
        private WebcamForm _webcamFormHead = new WebcamForm(0);
        private WebcamForm _webcamFormBody = new WebcamForm(1);
        private int _delaySeconds = 0; 
        private bool _isDelayed = false;

        public MainForm()
        {
            const int margin = 100;
            const int width = 300;
            const int height = 300;
            int x = margin, y = margin;

            InitializeComponent();

            // MainForm �Ӽ�
            this.Location = new Point(0, 1080);
            this.Size = new Size(2560, 720);
            this.BackColor = Color.GhostWhite;

            // ���� ��ư �߰�
            _closeButton.Location = new Point(x, y);
            _closeButton.Size = new Size(width, height);
            _closeButton.Font = new Font("�ü�ü", 50);
            _closeButton.BackColor = Color.LightGray;
            _closeButton.TextAlign = ContentAlignment.MiddleCenter;
            _closeButton.Text = "����";
            _closeButton.Click += new EventHandler(CloseButton_Click);

            x += width + margin;

            // ������ �����̴� �߰�
            _delayTextbox.Location = new Point(x + margin/2, y + margin/2);
            _delayTextbox.Size = new Size(margin*2, margin);
            _delayTextbox.Font = new Font("Arial", margin);
            _delayTextbox.TextAlign = HorizontalAlignment.Center;
            _delayTextbox.BackColor = Color.Black;
            _delayTextbox.ForeColor = Color.Red;
            _delayTextbox.BorderStyle = BorderStyle.None;
            _delayTextbox.Text = "0";

            _delaySlider.Location = new Point(x, y + height - margin);
            _delaySlider.Size = new Size(width, height);
            _delaySlider.Minimum = 0;
            _delaySlider.Maximum = 20;
            _delaySlider.TickFrequency = 1;
            _delaySlider.LargeChange = 1;
            _delaySlider.TickStyle = TickStyle.BottomRight;
            _delaySlider.ValueChanged += DelaySlider_ValueChanged;

            x += width + margin;

            // ������ ��� ��ư �߰�
            _toggleDelayButton.Location = new Point(x, y);
            _toggleDelayButton.Size = new Size(width, height);
            _toggleDelayButton.Font = new Font("�ü�ü", 50);
            _toggleDelayButton.BackColor = Color.LightGray;
            _toggleDelayButton.TextAlign = ContentAlignment.MiddleCenter;
            _toggleDelayButton.Text = "����";
            _toggleDelayButton.Click += new EventHandler(ToggleDelayButton_Click);
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            var screens = Screen.AllScreens;
            if (screens.Length > 3)
            {
                var monitor1 = screens[2];
                _webcamFormHead.StartPosition = FormStartPosition.Manual;
                _webcamFormHead.Location = monitor1.WorkingArea.Location;
                _webcamFormHead.Size = monitor1.WorkingArea.Size;
                _webcamFormHead.FormBorderStyle = FormBorderStyle.None;
                _webcamFormHead.WindowState = FormWindowState.Maximized;

                var monitor2 = screens[1];
                this.StartPosition = FormStartPosition.Manual;
                this.Location = monitor2.WorkingArea.Location; // 2�� ������� ��ġ�� �̵�
                this.Size = monitor2.WorkingArea.Size; // 2�� ����� ũ�� ����
                this.FormBorderStyle = FormBorderStyle.None; // �׵θ� ����
                this.WindowState = FormWindowState.Maximized; // �ִ�ȭ

                var monitor3 = screens[3];
                _webcamFormBody.StartPosition = FormStartPosition.Manual;
                _webcamFormBody.Location = monitor3.WorkingArea.Location;
                _webcamFormBody.Size = monitor3.WorkingArea.Size;
                _webcamFormBody.FormBorderStyle = FormBorderStyle.None;
                _webcamFormBody.WindowState = FormWindowState.Maximized;
            }

            _webcamFormHead.Text = "Head";
            _webcamFormHead.Show();
            _webcamFormBody.Text = "Body";
            _webcamFormBody.Show();
        }

        private void DelaySlider_ValueChanged(object sender, EventArgs e)
        {
            // �����̴� ���� �о� ������ �ð� ����
            _delaySeconds = ((CustomTrackBar)sender).Value;
            _delayTextbox.Text = _delaySeconds.ToString();
        }

        private void ToggleDelayButton_Click(object sender, EventArgs e)
        {
            _isDelayed = !_isDelayed;
            // ��ư ���¿� ���� ���� ���� ���� �Ǵ� ����
            if (_isDelayed)
            {
                _webcamFormHead.StartDelay(_delaySeconds);
                _webcamFormBody.StartDelay(_delaySeconds);
                _toggleDelayButton.Text = "���";
            }
            else
            {
                _webcamFormHead.StopDelay();
                _webcamFormBody.StopDelay();
                _toggleDelayButton.Text = "����";
            }
        }

        private void CloseButton_Click(object sender, EventArgs e)
        {
            // ��� WebcamForm �ݱ�
            _webcamFormHead.Close();
            _webcamFormBody.Close();
            this.Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);

            _webcamFormHead.Close();
            _webcamFormBody.Close();
        }
    }
}
