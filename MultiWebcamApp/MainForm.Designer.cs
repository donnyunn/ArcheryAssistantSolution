namespace MultiWebcamApp
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
            _toggleDelayButton = new RoundButton();
            _delaySlider = new CustomTrackBar();
            ((System.ComponentModel.ISupportInitialize)_delaySlider).BeginInit();
            SuspendLayout();
            // 
            // _closeButton
            // 
            _closeButton.FlatAppearance.BorderSize = 0;
            _closeButton.FlatStyle = FlatStyle.Flat;
            _closeButton.Location = new Point(12, 12);
            _closeButton.Name = "_closeButton";
            _closeButton.Size = new Size(75, 23);
            _closeButton.TabIndex = 0;
            _closeButton.Text = "roundButton1";
            _closeButton.UseVisualStyleBackColor = true;
            // 
            // _delayTextbox
            // 
            _delayTextbox.Location = new Point(93, 2);
            _delayTextbox.Name = "_delayTextbox";
            _delayTextbox.Size = new Size(100, 23);
            _delayTextbox.TabIndex = 1;
            // 
            // _toggleDelayButton
            // 
            _toggleDelayButton.FlatAppearance.BorderSize = 0;
            _toggleDelayButton.FlatStyle = FlatStyle.Flat;
            _toggleDelayButton.Location = new Point(203, 12);
            _toggleDelayButton.Name = "_toggleDelayButton";
            _toggleDelayButton.Size = new Size(75, 23);
            _toggleDelayButton.TabIndex = 3;
            _toggleDelayButton.Text = "roundButton1";
            _toggleDelayButton.UseVisualStyleBackColor = true;
            // 
            // _delaySlider
            // 
            _delaySlider.Location = new Point(93, 31);
            _delaySlider.Name = "_delaySlider";
            _delaySlider.Size = new Size(104, 45);
            _delaySlider.TabIndex = 4;
            // 
            // MainForm
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(800, 450);
            Controls.Add(_delaySlider);
            Controls.Add(_toggleDelayButton);
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
        private RoundButton _toggleDelayButton;
        private CustomTrackBar _delaySlider;
    }
}
