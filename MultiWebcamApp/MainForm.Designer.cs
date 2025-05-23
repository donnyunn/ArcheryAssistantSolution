﻿namespace MultiWebcamApp
{
    partial class MainForm
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            _closeButton = new RoundButton();
            _delayTextbox = new TextBox();
            _startButton = new RoundButton();
            _delaySlider = new CustomTrackBar();
            _slowButton = new RoundButton();
            _backwardButton = new FontAwesome.Sharp.IconButton();
            _pauseButton = new FontAwesome.Sharp.IconButton();
            _forwardButton = new FontAwesome.Sharp.IconButton();
            _recordButton = new ToggleSwitch();
            ((System.ComponentModel.ISupportInitialize)_delaySlider).BeginInit();
            SuspendLayout();
            // 
            // _closeButton
            // 
            _closeButton.FlatAppearance.BorderSize = 0;
            _closeButton.FlatStyle = FlatStyle.Flat;
            _closeButton.Location = new System.Drawing.Point(12, 12);
            _closeButton.Name = "_closeButton";
            _closeButton.Size = new Size(75, 23);
            _closeButton.TabIndex = 0;
            _closeButton.Text = "roundButton1";
            _closeButton.UseVisualStyleBackColor = true;
            // 
            // _delayTextbox
            // 
            _delayTextbox.Location = new System.Drawing.Point(93, 2);
            _delayTextbox.Name = "_delayTextbox";
            _delayTextbox.Size = new Size(100, 23);
            _delayTextbox.TabIndex = 1;
            // 
            // _startButton
            // 
            _startButton.FlatAppearance.BorderSize = 0;
            _startButton.FlatStyle = FlatStyle.Flat;
            _startButton.Location = new System.Drawing.Point(203, 12);
            _startButton.Name = "_startButton";
            _startButton.Size = new Size(75, 23);
            _startButton.TabIndex = 3;
            _startButton.Text = "roundButton1";
            _startButton.UseVisualStyleBackColor = true;
            // 
            // _delaySlider
            // 
            _delaySlider.Location = new System.Drawing.Point(93, 31);
            _delaySlider.Maximum = 100;
            _delaySlider.MinimumSize = new Size(100, 110);
            _delaySlider.Name = "_delaySlider";
            _delaySlider.Size = new Size(104, 110);
            _delaySlider.TabIndex = 4;
            // 
            // _slowButton
            // 
            _slowButton.FlatAppearance.BorderSize = 0;
            _slowButton.FlatStyle = FlatStyle.Flat;
            _slowButton.Location = new System.Drawing.Point(527, 12);
            _slowButton.Name = "_slowButton";
            _slowButton.Size = new Size(75, 23);
            _slowButton.TabIndex = 8;
            _slowButton.Text = "roundButton1";
            _slowButton.UseVisualStyleBackColor = true;
            // 
            // _backwardButton
            // 
            _backwardButton.IconChar = FontAwesome.Sharp.IconChar.Backward;
            _backwardButton.IconColor = Color.Black;
            _backwardButton.IconFont = FontAwesome.Sharp.IconFont.Auto;
            _backwardButton.Location = new System.Drawing.Point(284, 12);
            _backwardButton.Name = "_backwardButton";
            _backwardButton.Size = new Size(75, 23);
            _backwardButton.TabIndex = 9;
            _backwardButton.Text = "iconButton1";
            _backwardButton.UseVisualStyleBackColor = true;
            // 
            // _pauseButton
            // 
            _pauseButton.IconChar = FontAwesome.Sharp.IconChar.Play;
            _pauseButton.IconColor = Color.Black;
            _pauseButton.IconFont = FontAwesome.Sharp.IconFont.Auto;
            _pauseButton.Location = new System.Drawing.Point(365, 12);
            _pauseButton.Name = "_pauseButton";
            _pauseButton.Size = new Size(75, 23);
            _pauseButton.TabIndex = 10;
            _pauseButton.Text = "iconButton1";
            _pauseButton.UseVisualStyleBackColor = true;
            // 
            // _forwardButton
            // 
            _forwardButton.IconChar = FontAwesome.Sharp.IconChar.Forward;
            _forwardButton.IconColor = Color.Black;
            _forwardButton.IconFont = FontAwesome.Sharp.IconFont.Auto;
            _forwardButton.Location = new System.Drawing.Point(446, 12);
            _forwardButton.Name = "_forwardButton";
            _forwardButton.Size = new Size(75, 23);
            _forwardButton.TabIndex = 11;
            _forwardButton.Text = "iconButton1";
            _forwardButton.UseVisualStyleBackColor = true;
            // 
            // _recordButton
            // 
            _recordButton.BackColor = Color.Transparent;
            _recordButton.Font = new Font("맑은 고딕", 9F, FontStyle.Regular, GraphicsUnit.Point, 129);
            _recordButton.Location = new System.Drawing.Point(93, 147);
            _recordButton.Name = "_recordButton";
            _recordButton.OffColor = Color.FromArgb(160, 160, 160);
            _recordButton.Size = new Size(258, 30);
            _recordButton.TabIndex = 12;
            _recordButton.Text = "toggleSwitch1";
            _recordButton.TextFont = new Font("맑은 고딕", 9F);
            // 
            // MainForm
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(800, 450);
            Controls.Add(_recordButton);
            Controls.Add(_forwardButton);
            Controls.Add(_pauseButton);
            Controls.Add(_backwardButton);
            Controls.Add(_slowButton);
            Controls.Add(_delaySlider);
            Controls.Add(_startButton);
            Controls.Add(_delayTextbox);
            Controls.Add(_closeButton);
            Name = "MainForm";
            Text = "MainForm";
            Load += MainForm_Load;
            ((System.ComponentModel.ISupportInitialize)_delaySlider).EndInit();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private RoundButton _closeButton;
        private TextBox _delayTextbox;
        private RoundButton _startButton;
        private CustomTrackBar _delaySlider;
        private RoundButton _slowButton;
        private FontAwesome.Sharp.IconButton _backwardButton;
        private FontAwesome.Sharp.IconButton _pauseButton;
        private FontAwesome.Sharp.IconButton _forwardButton;
        private ToggleSwitch _recordButton;
    }
}
