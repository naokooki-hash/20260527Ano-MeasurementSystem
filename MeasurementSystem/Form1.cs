using System;
using System.Drawing; // System.Drawing.Pointなど用
using System.Windows.Forms;
using System.IO;
using System.Text;
using OpenCvSharp;    // OpenCvSharp.Pointなど用
using OpenCvSharp.Extensions;

// 曖昧さを解消するための別名定義（エイリアス）
using DrawingPoint = System.Drawing.Point;
using DrawingSize = System.Drawing.Size;
using CvPoint = OpenCvSharp.Point;
using CvSize = OpenCvSharp.Size;
using CvRect = OpenCvSharp.Rect;

namespace MeasurementSystem
{
    public partial class Form1 : Form
    {
        private ICamera camera;
        private MeasurementEngine engine = new MeasurementEngine();

        private PictureBox picMain;
        private Label lblResult, lblPitch, lblLeftDia, lblRightDia;
        private NumericUpDown numTarget, numTol, numThreshold;
        private TrackBar tbRoiX, tbRoiY, tbRoiW, tbRoiH;
        private CheckBox chkShowBinary;
        private TextBox txtSavePath;

        private Bitmap _currentBmp;
        private readonly object _bmpLock = new object();
        private double _mmPerPixel = 0.176;

        public Form1()
        {
            this.Text = "PanchMetal Inspection System (1-Cam)";
            this.WindowState = FormWindowState.Maximized;
            this.BackColor = Color.FromArgb(30, 30, 30);

            // 右パネル
            Panel pnlRight = new Panel { Dock = DockStyle.Right, Width = 350, BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.White, AutoScroll = true };
            this.Controls.Add(pnlRight);

            // メイン画面
            picMain = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
            this.Controls.Add(picMain);

            // UI構築
            int y = 10;
            lblResult = new Label { Text = "READY", Location = new DrawingPoint(20, y), Size = new DrawingSize(310, 80), BackColor = Color.Gray, ForeColor = Color.White, Font = new Font("Arial", 30, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter };
            pnlRight.Controls.Add(lblResult); y += 100;

            lblPitch = new Label { Text = "ピッチ: --- mm", Location = new DrawingPoint(20, y), AutoSize = true, Font = new Font("Arial", 16) }; pnlRight.Controls.Add(lblPitch); y += 40;
            lblLeftDia = new Label { Text = "左穴径: --- mm", Location = new DrawingPoint(20, y), AutoSize = true, Font = new Font("Arial", 16) }; pnlRight.Controls.Add(lblLeftDia); y += 40;
            lblRightDia = new Label { Text = "右穴径: --- mm", Location = new DrawingPoint(20, y), AutoSize = true, Font = new Font("Arial", 16) }; pnlRight.Controls.Add(lblRightDia); y += 50;

            // 数値設定
            numTarget = AddNum(pnlRight, "目標ピッチ (mm):", y, 180m, 2, 0.1m, 1000m); y += 70;
            numTol = AddNum(pnlRight, "公差 (±mm):", y, 0.3m, 2, 0.01m, 10m); y += 70;
            numThreshold = AddNum(pnlRight, "二値化閾値 (0-255):", y, 128m, 0, 1m, 255m); y += 50;
            chkShowBinary = new CheckBox { Text = "二値化プレビュー有効", Location = new DrawingPoint(20, y), AutoSize = true, Checked = true }; pnlRight.Controls.Add(chkShowBinary); y += 50;

            // ROI設定
            pnlRight.Controls.Add(new Label { Text = "--- 検査範囲 (ROI) ---", Location = new DrawingPoint(10, y), AutoSize = true }); y += 30;
            tbRoiX = new TrackBar { Location = new DrawingPoint(10, y), Width = 320, Maximum = 3000 }; pnlRight.Controls.Add(tbRoiX); y += 40;
            tbRoiY = new TrackBar { Location = new DrawingPoint(10, y), Width = 320, Maximum = 3000 }; pnlRight.Controls.Add(tbRoiY); y += 40;
            tbRoiW = new TrackBar { Location = new DrawingPoint(10, y), Width = 320, Maximum = 3000, Value = 1500 }; pnlRight.Controls.Add(tbRoiW); y += 40;
            tbRoiH = new TrackBar { Location = new DrawingPoint(10, y), Width = 320, Maximum = 3000, Value = 800 }; pnlRight.Controls.Add(tbRoiH); y += 50;

            // 保存
            pnlRight.Controls.Add(new Label { Text = "保存先:", Location = new DrawingPoint(10, y), AutoSize = true }); y += 25;
            txtSavePath = new TextBox { Location = new DrawingPoint(10, y), Width = 280, Text = AppDomain.CurrentDomain.BaseDirectory }; pnlRight.Controls.Add(txtSavePath); y += 35;
            Button btnSave = new Button { Text = "強制記録", Location = new DrawingPoint(10, y), Width = 100, Height = 40, BackColor = Color.Blue, ForeColor = Color.White };
            btnSave.Click += (s, e) => SaveData(); pnlRight.Controls.Add(btnSave);

            camera = new TeliCamera();
            this.Load += (s, e) => { if (camera.Initialize()) { camera.OnFrameCaptured += Camera_OnFrameCaptured; camera.StartCapture(); } };
            this.FormClosing += (s, e) => camera.Terminate();
        }

        private NumericUpDown AddNum(Panel p, string text, int y, decimal val, int dec, decimal inc, decimal max)
        {
            p.Controls.Add(new Label { Text = text, Location = new DrawingPoint(10, y), AutoSize = true });

            // プロパティ初期化時に Value を含めないようにします
            var n = new NumericUpDown
            {
                Location = new DrawingPoint(180, y - 2),
                Width = 100,
                DecimalPlaces = dec,
                Increment = inc,
                Minimum = 0,
                Maximum = max // 先に最大値を設定
            };

            // 最大値を設定した後に Value を入れると安全です
            n.Value = val;

            p.Controls.Add(n);
            return n;
        }

        private void Camera_OnFrameCaptured(object sender, Mat frame)
        {
            try
            {
                int rX = 0, rY = 0, rW = 100, rH = 100, thresh = 128;
                double target = 180, tol = 0.3;
                bool showBin = true;

                this.Invoke(new Action(() => {
                    rX = tbRoiX.Value; rY = tbRoiY.Value; rW = tbRoiW.Value; rH = tbRoiH.Value;
                    thresh = (int)numThreshold.Value;
                    target = (double)numTarget.Value; tol = (double)numTol.Value;
                    showBin = chkShowBinary.Checked;
                }));

                using (Mat drawMat = new Mat())
                {
                    Cv2.CvtColor(frame, drawMat, ColorConversionCodes.GRAY2BGR);

                    // ROI描画
                    CvRect roiRect = new CvRect(rX, rY, rW, rH);
                    Cv2.Rectangle(drawMat, roiRect, Scalar.Blue, 2);

                    // 計測実行
                    var res = engine.ProcessFrame(frame, drawMat, rX, rY, rW, rH, _mmPerPixel, thresh);

                    if (res.IsValid)
                    {
                        bool isOk = Math.Abs(res.MeasuredValueMm - target) <= tol;
                        this.BeginInvoke(new Action(() => {
                            lblResult.Text = isOk ? "OK" : "NG";
                            lblResult.BackColor = isOk ? Color.LimeGreen : Color.Crimson;
                            lblPitch.Text = $"ピッチ: {res.MeasuredValueMm:F2} mm";
                            lblLeftDia.Text = $"左穴径: {res.LeftHoleDiaMm:F2} mm";
                            lblRightDia.Text = $"右穴径: {res.RightHoleDiaMm:F2} mm";
                        }));
                    }

                    Bitmap next = BitmapConverter.ToBitmap(drawMat);
                    lock (_bmpLock) { var old = _currentBmp; _currentBmp = next; old?.Dispose(); }
                    if (picMain.IsHandleCreated) picMain.BeginInvoke(new Action(() => picMain.Image = _currentBmp));
                }
            }
            catch { }
            finally { frame.Dispose(); }
        }

        private void SaveData()
        {
            MessageBox.Show("記録しました");
        }
    }
}