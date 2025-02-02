using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MultiWebcamApp
{
    public partial class RoundButton : Button
    {
        public RoundButton()
        {
            InitializeComponent();
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
        }

        protected override void OnPaint(PaintEventArgs pe)
        {
            base.OnPaint(pe);

            // 원형 경로 설정
            GraphicsPath path = new GraphicsPath();
            path.AddEllipse(0, 0, ClientSize.Width, ClientSize.Height);
            this.Region = new Region(path);

            // 안티앨리어싱 설정 (더 부드러운 테두리)
            pe.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            // 테두리 그리기
            using (Pen borderPen = new Pen(Color.Black, 2))
            {
                // 테두리가 버튼 안에 맞게 위치하도록 조정
                int borderOffset = (int)(borderPen.Width / 2);
                pe.Graphics.DrawEllipse(borderPen, borderOffset, borderOffset, ClientSize.Width - borderPen.Width, ClientSize.Height - borderPen.Width);
            }
        }
    }
}
