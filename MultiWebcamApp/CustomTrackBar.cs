using System;
using System.Drawing;
using System.Windows.Forms;

namespace MultiWebcamApp
{
    public partial class CustomTrackBar : TrackBar
    {
        private const int ThumbWidth = 50;
        private const int ThumbHeight = 100;
        private const int TrackHeight = 50;

        private int minimum = 0;
        private int maximum = 100;
        private int value = 0;
        private bool isDragging = false;

        public event EventHandler ValueChanged;

        public int Minimum
        {
            get { return minimum; }
            set
            {
                minimum = value;
                if (this.value < minimum) this.value = minimum;
                Invalidate();
            }
        }

        public int Maximum
        {
            get { return maximum; }
            set
            {
                maximum = value;
                if (this.value > maximum) this.value = maximum;
                Invalidate();
            }
        }

        public int Value
        {
            get { return value; }
            set
            {
                if (this.value != value)
                {
                    this.value = Math.Max(minimum, Math.Min(maximum, value));
                    ValueChanged?.Invoke(this, EventArgs.Empty);
                    Invalidate();
                }
            }
        }
        public CustomTrackBar()
        {
            SetStyle(ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer, true);

            this.Height = ThumbHeight + 10;
            this.MinimumSize = new Size(ThumbWidth * 2, ThumbHeight + 10);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle bounds = this.ClientRectangle;

            // 트랙 그리기
            int trackY = bounds.Height / 2 - TrackHeight / 2;
            Rectangle trackRect = new Rectangle(ThumbWidth / 2, trackY, bounds.Width - ThumbWidth, TrackHeight);
            using (SolidBrush trackBrush = new SolidBrush(Color.LightGray))
            {
                g.FillRectangle(trackBrush, trackRect);
            }

            // 진행률 표시 부분 그리기
            float progress = (float)(value - minimum) / (maximum - minimum);
            Rectangle progressRect = new Rectangle(ThumbWidth / 2, trackY,
                (int)((bounds.Width - ThumbWidth) * progress), TrackHeight);
            using (SolidBrush progressBrush = new SolidBrush(Color.DodgerBlue))
            {
                g.FillRectangle(progressBrush, progressRect);
            }

            // 썸(Thumb) 그리기
            int thumbX = (int)(progress * (bounds.Width - ThumbWidth));
            int thumbY = bounds.Height / 2 - ThumbHeight / 2;
            Rectangle thumbRect = new Rectangle(thumbX, thumbY, ThumbWidth, ThumbHeight);

            using (SolidBrush thumbBrush = new SolidBrush(Color.RoyalBlue))
            {
                g.FillRectangle(thumbBrush, thumbRect);
            }

            // 테두리 그리기
            using (Pen borderPen = new Pen(Color.DarkBlue, 2))
            {
                g.DrawRectangle(borderPen, thumbRect);
            }

            // 값 표시
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(value.ToString(), this.Font, Brushes.White, thumbRect, sf);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            isDragging = true;
            UpdateValue(e.X);
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (isDragging)
            {
                UpdateValue(e.X);
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            isDragging = false;
            base.OnMouseUp(e);
        }

        private void UpdateValue(int mouseX)
        {
            float usableWidth = Width - ThumbWidth;
            float position = Math.Max(0, Math.Min(usableWidth, mouseX - ThumbWidth / 2));
            Value = minimum + (int)((maximum - minimum) * (position / usableWidth));
        }
    }
}
