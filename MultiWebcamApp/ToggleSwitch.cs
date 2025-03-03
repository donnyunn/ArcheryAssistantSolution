using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MultiWebcamApp
{
    /// <summary>
    /// 터치 모니터에 최적화된 ToggleSwitch 컨트롤
    /// </summary>
    public class ToggleSwitch : Control
    {
        #region 필드 및 속성

        private bool _checked = false;
        private bool _mouseDown = false;
        private Color _onColor = Color.FromArgb(0, 122, 204);
        private Color _offColor = Color.FromArgb(160, 160, 160);
        private Color _thumbColor = Color.White;
        private Color _borderColor = Color.FromArgb(120, 120, 120);
        private int _thumbSize = 24;
        private int _borderRadius = 16;
        private int _borderWidth = 1;
        private bool _animating = false;
        private float _animationProgress = 0f;
        private System.Windows.Forms.Timer _animationTimer = new System.Windows.Forms.Timer();
        private string _onText = "켜짐";
        private string _offText = "꺼짐";
        private bool _showText = true;
        private Font _textFont = new Font("맑은 고딕", 9f);

        /// <summary>
        /// ToggleSwitch의 상태(켜짐/꺼짐)
        /// </summary>
        [Category("동작")]
        [Description("ToggleSwitch의 상태(켜짐/꺼짐)")]
        [DefaultValue(false)]
        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked != value)
                {
                    _checked = value;
                    StartAnimation();
                    OnCheckedChanged(EventArgs.Empty);
                    Invalidate();
                }
            }
        }

        /// <summary>
        /// ToggleSwitch가 켜져 있을 때의 배경색
        /// </summary>
        [Category("모양")]
        [Description("ToggleSwitch가 켜져 있을 때의 배경색")]
        [DefaultValue(typeof(Color), "0, 122, 204")]
        public Color OnColor
        {
            get { return _onColor; }
            set
            {
                _onColor = value;
                Invalidate();
            }
        }

        /// <summary>
        /// ToggleSwitch가 꺼져 있을 때의 배경색
        /// </summary>
        [Category("모양")]
        [Description("ToggleSwitch가 꺼져 있을 때의 배경색")]
        [DefaultValue(typeof(Color), "160, 160, 160")]
        public Color OffColor
        {
            get { return _offColor; }
            set
            {
                _offColor = value;
                Invalidate();
            }
        }

        /// <summary>
        /// ToggleSwitch의 슬라이더(Thumb) 색상
        /// </summary>
        [Category("모양")]
        [Description("ToggleSwitch의 슬라이더(Thumb) 색상")]
        [DefaultValue(typeof(Color), "White")]
        public Color ThumbColor
        {
            get { return _thumbColor; }
            set
            {
                _thumbColor = value;
                Invalidate();
            }
        }

        /// <summary>
        /// ToggleSwitch의 테두리 색상
        /// </summary>
        [Category("모양")]
        [Description("ToggleSwitch의 테두리 색상")]
        [DefaultValue(typeof(Color), "120, 120, 120")]
        public Color BorderColor
        {
            get { return _borderColor; }
            set
            {
                _borderColor = value;
                Invalidate();
            }
        }

        /// <summary>
        /// ToggleSwitch의 슬라이더(Thumb) 크기
        /// </summary>
        [Category("모양")]
        [Description("ToggleSwitch의 슬라이더(Thumb) 크기")]
        [DefaultValue(24)]
        public int ThumbSize
        {
            get { return _thumbSize; }
            set
            {
                _thumbSize = value;
                UpdateControlSize();
                Invalidate();
            }
        }

        /// <summary>
        /// ToggleSwitch의 테두리 둥근 정도
        /// </summary>
        [Category("모양")]
        [Description("ToggleSwitch의 테두리 둥근 정도")]
        [DefaultValue(16)]
        public int BorderRadius
        {
            get { return _borderRadius; }
            set
            {
                _borderRadius = value;
                Invalidate();
            }
        }

        /// <summary>
        /// ToggleSwitch의 테두리 두께
        /// </summary>
        [Category("모양")]
        [Description("ToggleSwitch의 테두리 두께")]
        [DefaultValue(1)]
        public int BorderWidth
        {
            get { return _borderWidth; }
            set
            {
                _borderWidth = value;
                Invalidate();
            }
        }

        /// <summary>
        /// ToggleSwitch가 켜졌을 때 표시되는 텍스트
        /// </summary>
        [Category("모양")]
        [Description("ToggleSwitch가 켜졌을 때 표시되는 텍스트")]
        [DefaultValue("켜짐")]
        public string OnText
        {
            get { return _onText; }
            set
            {
                _onText = value;
                Invalidate();
            }
        }

        /// <summary>
        /// ToggleSwitch가 꺼졌을 때 표시되는 텍스트
        /// </summary>
        [Category("모양")]
        [Description("ToggleSwitch가 꺼졌을 때 표시되는 텍스트")]
        [DefaultValue("꺼짐")]
        public string OffText
        {
            get { return _offText; }
            set
            {
                _offText = value;
                Invalidate();
            }
        }

        /// <summary>
        /// ToggleSwitch에 텍스트 표시 여부
        /// </summary>
        [Category("모양")]
        [Description("ToggleSwitch에 텍스트 표시 여부")]
        [DefaultValue(true)]
        public bool ShowText
        {
            get { return _showText; }
            set
            {
                _showText = value;
                Invalidate();
            }
        }

        /// <summary>
        /// ToggleSwitch에 표시되는 텍스트 폰트
        /// </summary>
        [Category("모양")]
        [Description("ToggleSwitch에 표시되는 텍스트 폰트")]
        public Font TextFont
        {
            get { return _textFont; }
            set
            {
                _textFont = value;
                UpdateControlSize();
                Invalidate();
            }
        }

        #endregion

        #region 이벤트

        /// <summary>
        /// ToggleSwitch의 상태가 변경되었을 때 발생하는 이벤트
        /// </summary>
        [Category("동작")]
        [Description("ToggleSwitch의 상태가 변경되었을 때 발생하는 이벤트")]
        public event EventHandler CheckedChanged;

        /// <summary>
        /// CheckedChanged 이벤트를 발생시킵니다.
        /// </summary>
        protected virtual void OnCheckedChanged(EventArgs e)
        {
            CheckedChanged?.Invoke(this, e);
        }

        #endregion

        #region 생성자 및 초기화

        /// <summary>
        /// ToggleSwitch 컨트롤의 생성자
        /// </summary>
        public ToggleSwitch()
        {
            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);

            BackColor = Color.Transparent;
            UpdateControlSize();

            // 애니메이션 타이머 설정
            _animationTimer.Interval = 10;
            _animationTimer.Tick += AnimationTick;
        }

        /// <summary>
        /// 컨트롤 크기를 업데이트합니다.
        /// </summary>
        private void UpdateControlSize()
        {
            // 터치 모니터에 최적화된 크기 설정
            int width = _thumbSize * 2 + 10; // 충분한 너비
            int height = _thumbSize + 6;     // 터치에 적합한 높이

            if (_showText)
            {
                width += 200;
            }

            Size = new Size(width, height);
        }

        #endregion

        #region 애니메이션 관련 메서드

        /// <summary>
        /// 애니메이션을 시작합니다.
        /// </summary>
        private void StartAnimation()
        {
            _animating = true;
            _animationProgress = 0f;
            _animationTimer.Start();
        }

        /// <summary>
        /// 애니메이션 틱 이벤트 핸들러
        /// </summary>
        private void AnimationTick(object sender, EventArgs e)
        {
            const float animationSpeed = 0.1f; // 애니메이션 속도
            _animationProgress += animationSpeed;

            if (_animationProgress >= 1.0f)
            {
                _animationProgress = 1.0f;
                _animating = false;
                _animationTimer.Stop();
            }

            Invalidate();
        }

        #endregion

        #region 이벤트 오버라이드

        /// <summary>
        /// 마우스 버튼을 눌렀을 때 발생하는 이벤트
        /// </summary>
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _mouseDown = true;
            Invalidate();
        }

        /// <summary>
        /// 마우스 버튼을 놓았을 때 발생하는 이벤트
        /// </summary>
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_mouseDown)
            {
                _mouseDown = false;
                Checked = !Checked; // 상태를 전환
            }
        }

        /// <summary>
        /// 마우스 포인터가 컨트롤을 벗어났을 때 발생하는 이벤트
        /// </summary>
        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _mouseDown = false;
            Invalidate();
        }

        /// <summary>
        /// 키를 눌렀을 때 발생하는 이벤트
        /// </summary>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space)
            {
                _mouseDown = true;
                Invalidate();
            }
        }

        /// <summary>
        /// 키를 놓았을 때 발생하는 이벤트
        /// </summary>
        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.Space && _mouseDown)
            {
                _mouseDown = false;
                Checked = !Checked; // 상태를 전환
            }
        }

        #endregion

        #region 페인팅

        /// <summary>
        /// 컨트롤을 그리는 메서드
        /// </summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 애니메이션 진행 계산
            float animProgress = _animating ? _animationProgress : 1.0f;
            bool isOn = _checked;

            // 토글 스위치의 배경색 계산 (애니메이션 중에는 색상 보간)
            Color backgroundColor;
            if (_animating)
            {
                int r = (int)(_offColor.R + (_onColor.R - _offColor.R) * (isOn ? animProgress : 1 - animProgress));
                int g_val = (int)(_offColor.G + (_onColor.G - _offColor.G) * (isOn ? animProgress : 1 - animProgress));
                int b = (int)(_offColor.B + (_onColor.B - _offColor.B) * (isOn ? animProgress : 1 - animProgress));
                backgroundColor = Color.FromArgb(r, g_val, b);
            }
            else
            {
                backgroundColor = isOn ? _onColor : _offColor;
            }

            int trackWidth = Width - (_showText ? 200 : 0);
            Rectangle trackRectangle = new Rectangle(0, 0, trackWidth, Height);
            RectangleF thumbRectangle;

            // 슬라이더 위치 계산 (애니메이션 중에는 위치 보간)
            float thumbPosition;
            if (_animating)
            {
                float startPos = isOn ? _borderWidth + 1 : trackWidth - _thumbSize - _borderWidth - 1;
                float endPos = isOn ? trackWidth - _thumbSize - _borderWidth - 1 : _borderWidth + 1;
                thumbPosition = startPos + (endPos - startPos) * animProgress;
            }
            else
            {
                thumbPosition = isOn ? trackWidth - _thumbSize - _borderWidth - 1 : _borderWidth + 1;
            }

            thumbRectangle = new RectangleF(thumbPosition, _borderWidth + 1, _thumbSize, Height - 2 * _borderWidth - 2);

            // 배경 그리기
            using (var path = CreateRoundedRectangle(trackRectangle, _borderRadius))
            {
                using (var brush = new SolidBrush(backgroundColor))
                {
                    g.FillPath(brush, path);
                }

                // 테두리 그리기
                using (var pen = new Pen(_borderColor, _borderWidth))
                {
                    g.DrawPath(pen, path);
                }
            }

            // 슬라이더(Thumb) 그리기
            using (var thumbPath = CreateRoundedRectangle(thumbRectangle, _borderRadius - 2))
            {
                using (var thumbBrush = new SolidBrush(_thumbColor))
                {
                    g.FillPath(thumbBrush, thumbPath);
                }

                // 슬라이더 테두리
                using (var thumbPen = new Pen(Color.FromArgb(80, Color.Gray), 1))
                {
                    g.DrawPath(thumbPen, thumbPath);
                }
            }

            // 텍스트 그리기
            if (_showText)
            {
                string text = isOn ? _onText : _offText;
                using (var textBrush = new SolidBrush(ForeColor))
                using (var textFormat = new StringFormat())
                {
                    textFormat.Alignment = StringAlignment.Near;
                    textFormat.LineAlignment = StringAlignment.Center;

                    // 텍스트 영역을 더 넓게 설정 (trackWidth + 10에서 조절 가능)
                    Rectangle textRect = new Rectangle(trackWidth + 10, 0, Width - trackWidth - 10, Height);

                    // 텍스트를 그릴 때 설정된 폰트 사용, 이전에는 _textFont 사용
                    g.DrawString(text, _textFont, textBrush, textRect, textFormat);
                }
            }
        }

        /// <summary>
        /// 둥근 모서리 사각형 경로를 생성합니다.
        /// </summary>
        private GraphicsPath CreateRoundedRectangle(RectangleF rect, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float diameter = radius * 2;

            // 상단 왼쪽 호
            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            // 상단 오른쪽 호
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            // 하단 오른쪽 호
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            // 하단 왼쪽 호
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();

            return path;
        }

        #endregion

        #region 리소스 정리

        /// <summary>
        /// 사용된 리소스를 정리합니다.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _animationTimer?.Dispose();
                _textFont?.Dispose();
            }
            base.Dispose(disposing);
        }

        #endregion
    }
}
