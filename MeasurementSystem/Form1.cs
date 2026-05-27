using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using OpenCvSharp;
using OpenCvSharp.Extensions;

using Point = System.Drawing.Point;
using Size = System.Drawing.Size;
using Timer = System.Windows.Forms.Timer;
using CvPoint = OpenCvSharp.Point;

namespace MeasurementSystem // ※ご自身のプロジェクト名に合わせて変更してください
{
    public partial class Form1 : Form
    {
        private ICamera camera;
        private double _currentVal = 0;
        private double _lastPx = 0;
        private double _lastCenterY = 1024;

        private double _mmPerPixelTop = 0.176;
        private double _mmPerPixelMid = 0.176;
        private double _mmPerPixelBot = 0.176;
        private double _refYTop = 400.0;
        private double _refYMid = 1024.0;
        private double _refYBot = 1600.0;

        private const string AdminPassword = "admin";

        private TabControl mainTabControl;
        private TabPage tabPageMain, tabPageSettings;

        private Label lblBigResult;
        private TextBox txtSavePath;
        private PictureBox pictureBox1;

        private NumericUpDown numTarget, numTolPlus, numTolMinus, numRoiWidth, numRoiHeight;
        private NumericUpDown numMaxAngle, numUpdateInterval, numLogKeepDays;
        private TrackBar trackBarRoiX, trackBarRoiY;
        private ComboBox cmbSaveImageMode, cmbSaveScale; // 【追加】縮小率のコンボボックス
        private Label lblCalibStatus;

        private DateTime _lastTextUpdate = DateTime.MinValue;
        private string _displayText = "";
        private Scalar _displayColor = Scalar.Gray;

        private float _zoom = 1.0f;
        private PointF _offset = new PointF(0, 0);
        private Point _lastPos;
        private bool _isDragging = false;
        private Bitmap _bmp;
        private readonly object _bmpLock = new object();
        private bool _isFirst = true;

        public Form1()
        {
            InitializeCustomUI();
            LoadAppConfig();

            camera = new TeliCamera();
            this.KeyPreview = true;
            this.KeyDown += (s, e) => { if (e.KeyCode == Keys.Space) SaveData(); };

            this.Load += (s, e) => {
                if (camera.Initialize())
                {
                    camera.OnFrameCaptured += Camera_OnFrameCaptured;
                    camera.StartCapture();
                }
            };
            this.FormClosing += (s, e) => {
                SaveAppConfig();
                camera?.Terminate();
            };
        }

        private void InitializeCustomUI()
        {
            this.Text = "FA AI Inspection System - Production Model";
            this.Size = new Size(1400, 950);

            mainTabControl = new TabControl { Dock = DockStyle.Fill, Appearance = TabAppearance.FlatButtons, ItemSize = new Size(0, 1), SizeMode = TabSizeMode.Fixed };
            this.Controls.Add(mainTabControl);

            // ==========================================
            // 1. オペレーターメイン画面
            // ==========================================
            tabPageMain = new TabPage { Text = "Main" };
            mainTabControl.TabPages.Add(tabPageMain);

            Panel pnlOperator = new Panel { Dock = DockStyle.Right, Width = 280, BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.White };
            tabPageMain.Controls.Add(pnlOperator);

            Panel pnlBottomSave = new Panel { Dock = DockStyle.Bottom, Height = 120 };
            Button btnSaveLog = new Button
            {
                Text = "💾 記録 (SAVE LOG)",
                Size = new Size(240, 90),
                Location = new Point(20, 10),
                BackColor = Color.FromArgb(0, 122, 204),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 16, FontStyle.Bold)
            };
            btnSaveLog.Click += (s, e) => SaveData();
            pnlBottomSave.Controls.Add(btnSaveLog);
            pnlOperator.Controls.Add(pnlBottomSave);

            pictureBox1 = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.Black };
            tabPageMain.Controls.Add(pictureBox1);

            int opY = 15;
            lblBigResult = new Label
            {
                Text = "READY",
                Location = new Point(15, opY),
                Width = 240,
                Height = 70,
                Font = new Font("Segoe UI", 26, FontStyle.Bold),
                BackColor = Color.FromArgb(28, 28, 28),
                ForeColor = Color.Gray,
                TextAlign = ContentAlignment.MiddleCenter
            };
            pnlOperator.Controls.Add(lblBigResult); opY += 95;

            Button btnGoAdmin = new Button
            {
                Text = "管理者設定メニュー ⚙",
                Location = new Point(15, opY),
                Width = 240,
                Height = 45,
                BackColor = Color.FromArgb(70, 70, 74),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 11, FontStyle.Bold)
            };
            btnGoAdmin.Click += btnGoAdmin_Click;
            pnlOperator.Controls.Add(btnGoAdmin); opY += 70;

            AddLabel(pnlOperator, "■ 保存先 (ルートフォルダ)", opY, true); opY += 30;
            txtSavePath = new TextBox { Location = new Point(15, opY), Width = 175, Text = AppDomain.CurrentDomain.BaseDirectory };
            pnlOperator.Controls.Add(txtSavePath);
            Button btnBrowse = new Button { Text = "...", Location = new Point(195, opY - 1), Width = 50, BackColor = Color.Gray };
            btnBrowse.Click += (s, e) => { using (var f = new FolderBrowserDialog()) if (f.ShowDialog() == DialogResult.OK) txtSavePath.Text = f.SelectedPath; };
            pnlOperator.Controls.Add(btnBrowse); opY += 45;

            AddLabel(pnlOperator, "※ [スペースキー] または右下の\n  ボタンで画像と結果を保存", opY);

            // ==========================================
            // 2. 管理者専用設定画面
            // ==========================================
            tabPageSettings = new TabPage { Text = "Settings", BackColor = Color.FromArgb(30, 30, 30) };

            Panel pnlSettingsLeft = new Panel { Dock = DockStyle.Left, Width = 460, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.White };
            Panel pnlSettingsRight = new Panel { Dock = DockStyle.Right, Width = 320, BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.White, AutoScroll = true };
            tabPageSettings.Controls.Add(pnlSettingsLeft);
            tabPageSettings.Controls.Add(pnlSettingsRight);

            int calY = 20;
            AddLabel(pnlSettingsLeft, "■ レンズ収差補正（上・中・下 3点キャリブレーション）", calY, true); calY += 40;

            lblCalibStatus = new Label
            {
                Location = new Point(15, calY),
                Width = 430,
                Height = 120,
                Font = new Font("Consolas", 10, FontStyle.Regular),
                BackColor = Color.Black,
                ForeColor = Color.Lime,
                Text = "校正データ未読込"
            };
            pnlSettingsLeft.Controls.Add(lblCalibStatus); calY += 140;

            string infoTxt = "【正しい校正手順】\n" +
                             "1. 運用時と全く同じ広いROI（X幅・Y高さ）に設定します。\n" +
                             "2. 金属ワークを「物理的に」画面の上部へ移動させます。\n" +
                             "3. 画面の黄色い十字がワークに乗っていることを確認し、\n" +
                             " 「1. 上部」ボタンを押して実寸を入力します。";
            Label lblInfo = new Label { Text = infoTxt, Location = new Point(15, calY), Width = 430, Height = 120, ForeColor = Color.LightGray };
            pnlSettingsLeft.Controls.Add(lblInfo); calY += 110;

            Button btnCalibTop = new Button { Text = "1. 上部エリア校正実行 (Upper)", Location = new Point(15, calY), Width = 400, Height = 40, BackColor = Color.FromArgb(0, 122, 204), FlatStyle = FlatStyle.Flat };
            btnCalibTop.Click += (s, e) => Execute3PointCalib(0);
            pnlSettingsLeft.Controls.Add(btnCalibTop); calY += 50;

            Button btnCalibMid = new Button { Text = "2. 中央エリア校正実行 (Middle)", Location = new Point(15, calY), Width = 400, Height = 40, BackColor = Color.FromArgb(0, 122, 204), FlatStyle = FlatStyle.Flat };
            btnCalibMid.Click += (s, e) => Execute3PointCalib(1);
            pnlSettingsLeft.Controls.Add(btnCalibMid); calY += 50;

            Button btnCalibBot = new Button { Text = "3. 下部エリア校正実行 (Lower)", Location = new Point(15, calY), Width = 400, Height = 40, BackColor = Color.FromArgb(0, 122, 204), FlatStyle = FlatStyle.Flat };
            btnCalibBot.Click += (s, e) => Execute3PointCalib(2);
            pnlSettingsLeft.Controls.Add(btnCalibBot); calY += 70;

            Button btnCloseAdmin = new Button
            {
                Text = "◀ 設定を確定してオペレーター画面へ戻る",
                Location = new Point(15, calY),
                Width = 400,
                Height = 55,
                BackColor = Color.FromArgb(16, 124, 65),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 12, FontStyle.Bold)
            };
            btnCloseAdmin.Click += btnCloseAdmin_Click;
            pnlSettingsLeft.Controls.Add(btnCloseAdmin);

            int setY = 15;
            AddLabel(pnlSettingsRight, "■ 計測・公差設定", setY, true); setY += 30;
            numTarget = AddNum(pnlSettingsRight, "目標値 (mm):", setY, 55.50m, 2, 0.01m); setY += 65;
            numTolPlus = AddNum(pnlSettingsRight, "上限公差 (+mm):", setY, 0.30m, 2, 0.01m); setY += 65;
            numTolMinus = AddNum(pnlSettingsRight, "下限公差 (-mm):", setY, 0.30m, 2, 0.01m); setY += 80;

            AddLabel(pnlSettingsRight, "■ 拡張判定設定", setY, true); setY += 30;
            numMaxAngle = AddNum(pnlSettingsRight, "許容角度 (±度):", setY, 2.0m, 1, 0.1m); setY += 65;
            numUpdateInterval = AddNum(pnlSettingsRight, "数値更新間隔 (ms):", setY, 500m, 0, 100m); setY += 80;

            AddLabel(pnlSettingsRight, "■ 視野設定 (ROI設定)", setY, true); setY += 30;
            AddLabel(pnlSettingsRight, "X位置 (横の開始点座標):", setY);
            trackBarRoiX = new TrackBar { Location = new Point(15, setY + 22), Width = 260, Minimum = 0, Maximum = 10000, TickStyle = TickStyle.None };
            pnlSettingsRight.Controls.Add(trackBarRoiX); setY += 65;
            numRoiWidth = AddNum(pnlSettingsRight, "X計測幅 (mmサイズ):", setY, 99m, 0); setY += 65;

            AddLabel(pnlSettingsRight, "Y位置 (縦の開始点座標):", setY);
            trackBarRoiY = new TrackBar { Location = new Point(15, setY + 22), Width = 260, Minimum = 0, Maximum = 10000, TickStyle = TickStyle.None };
            pnlSettingsRight.Controls.Add(trackBarRoiY); setY += 65;
            numRoiHeight = AddNum(pnlSettingsRight, "Y計測高さ (mmサイズ):", setY, 50m, 0); setY += 80;

            // 【UI追加】画像の保存サイズ（縮小率）の選択を追加
            AddLabel(pnlSettingsRight, "■ ログ・保存詳細設定", setY, true); setY += 30;
            cmbSaveImageMode = AddCombo(pnlSettingsRight, "画像保存モード:", setY, new[] { "0: 画像保存しない", "1: NGの時のみ保存", "2: すべて保存" }, 2); setY += 65;
            cmbSaveScale = AddCombo(pnlSettingsRight, "保存画像サイズ (縮小率):", setY, new[] { "0: 100% (そのまま)", "1: 50% (推奨・容量1/4)", "2: 25% (激軽・容量1/16)" }, 1); setY += 65;
            numLogKeepDays = AddNum(pnlSettingsRight, "ログ保持日数 (日):", setY, 30m, 0, 1m);

            pictureBox1.MouseDown += (s, e) => { pictureBox1.Focus(); if (e.Button == MouseButtons.Left) { _isDragging = true; _lastPos = e.Location; } };
            pictureBox1.MouseMove += (s, e) => { if (_isDragging) { _offset.X += e.X - _lastPos.X; _offset.Y += e.Y - _lastPos.Y; _lastPos = e.Location; pictureBox1.Invalidate(); } };
            pictureBox1.MouseUp += (s, e) => _isDragging = false;
            pictureBox1.MouseWheel += (s, e) => { float o = _zoom; if (e.Delta > 0) _zoom *= 1.15f; else _zoom /= 1.15f; _zoom = Math.Max(0.001f, _zoom); _offset.X = e.X - (e.X - _offset.X) * (_zoom / o); _offset.Y = e.Y - (e.Y - _offset.Y) * (_zoom / o); pictureBox1.Invalidate(); };
            pictureBox1.Paint += (s, e) => { lock (_bmpLock) if (_bmp != null) { e.Graphics.Clear(Color.Black); e.Graphics.InterpolationMode = InterpolationMode.HighQualityBilinear; e.Graphics.DrawImage(_bmp, _offset.X, _offset.Y, _bmp.Width * _zoom, _bmp.Height * _zoom); } };

            pictureBox1.DoubleClick += (s, e) => ResetView();
            pictureBox1.Resize += (s, e) => ResetView();
        }

        private void Camera_OnFrameCaptured(object sender, Mat frame)
        {
            try
            {
                double target = 0, tolPlus = 0, tolMinus = 0, maxAngle = 1.0, intervalMs = 500;
                int rX = 0, rY = 0, rW_px = 50, rH_px = 50;

                this.Invoke(new Action(() => {
                    rW_px = Math.Max(1, (int)(numRoiWidth.Value / (decimal)_mmPerPixelMid));
                    rH_px = Math.Max(1, (int)(numRoiHeight.Value / (decimal)_mmPerPixelMid));

                    int maxPosX = Math.Max(0, frame.Cols - rW_px);
                    if (trackBarRoiX.Maximum != maxPosX) trackBarRoiX.Maximum = maxPosX;

                    int maxPosY = Math.Max(0, frame.Rows - rH_px);
                    if (trackBarRoiY.Maximum != maxPosY) trackBarRoiY.Maximum = maxPosY;

                    target = (double)numTarget.Value;
                    tolPlus = (double)numTolPlus.Value;
                    tolMinus = (double)numTolMinus.Value;
                    maxAngle = (double)numMaxAngle.Value;
                    intervalMs = (double)numUpdateInterval.Value;

                    rX = trackBarRoiX.Value;
                    rY = trackBarRoiY.Value;
                }));

                using (Mat drawMat = new Mat())
                {
                    Cv2.CvtColor(frame, drawMat, ColorConversionCodes.GRAY2BGR);

                    var resObj = MeasurementCore.ProcessFrame(
                        frame, drawMat, rX, rY, rW_px, rH_px,
                        _mmPerPixelTop, _mmPerPixelMid, _mmPerPixelBot,
                        _refYTop, _refYMid, _refYBot
                    );

                    if (resObj.IsValid)
                    {
                        _lastPx = resObj.HeightPx;
                        _currentVal = resObj.HeightMm;
                        _lastCenterY = resObj.CenterY;

                        bool angleOk = Math.Abs(resObj.AngleDegree) <= maxAngle;
                        bool sizeOk = (_currentVal >= target - tolMinus) && (_currentVal <= target + tolPlus);

                        if ((DateTime.Now - _lastTextUpdate).TotalMilliseconds >= intervalMs)
                        {
                            if (!angleOk)
                            {
                                _displayText = $"ANGLE NG ({resObj.AngleDegree:F1} deg)";
                                _displayColor = Scalar.Yellow;

                                this.BeginInvoke(new Action(() => {
                                    lblBigResult.Text = "ANGLE NG"; lblBigResult.BackColor = Color.Gold; lblBigResult.ForeColor = Color.Black;
                                }));
                            }
                            else
                            {
                                _displayText = $"{(sizeOk ? "OK" : "NG")} ({_currentVal:F2}mm) [Y:{resObj.CenterY:F0}, F:{resObj.AppliedMmPerPixel:F5}]";
                                _displayColor = sizeOk ? Scalar.Lime : Scalar.Red;

                                this.BeginInvoke(new Action(() => {
                                    lblBigResult.Text = sizeOk ? "OK" : "NG";
                                    lblBigResult.BackColor = sizeOk ? Color.LimeGreen : Color.Crimson;
                                    lblBigResult.ForeColor = Color.White;
                                }));
                            }
                            _lastTextUpdate = DateTime.Now;
                        }

                        int cx = rX + (rW_px / 2);
                        int cy = (int)_lastCenterY;
                        Cv2.Line(drawMat, new CvPoint(cx - 20, cy), new CvPoint(cx + 20, cy), Scalar.Yellow, 2);
                        Cv2.Line(drawMat, new CvPoint(cx, cy - 20), new CvPoint(cx, cy + 20), Scalar.Yellow, 2);
                        Cv2.Circle(drawMat, new CvPoint(cx, cy), 4, Scalar.Yellow, -1);

                        if (!string.IsNullOrEmpty(_displayText))
                        {
                            Cv2.PutText(drawMat, _displayText, new CvPoint(50, 150), HersheyFonts.HersheyComplex, 3.0, _displayColor, 6);
                        }
                    }

                    Bitmap next = BitmapConverter.ToBitmap(drawMat);
                    lock (_bmpLock) { var old = _bmp; _bmp = next; old?.Dispose(); }
                    if (_isFirst) { _isFirst = false; this.BeginInvoke(new Action(ResetView)); }
                    pictureBox1.BeginInvoke(new Action(() => pictureBox1.Invalidate()));
                }
            }
            catch { }
            finally { frame.Dispose(); }
        }

        // =======================================================
        // 【画像圧縮・リサイズ対応版】保存ロジック
        // =======================================================
        private void SaveData()
        {
            try
            {
                string rootPath = txtSavePath.Text;
                if (string.IsNullOrWhiteSpace(rootPath)) return;
                if (!Directory.Exists(rootPath)) Directory.CreateDirectory(rootPath);

                string dateStr = DateTime.Now.ToString("yyyyMMdd");
                string dailyDir = Path.Combine(rootPath, dateStr);
                if (!Directory.Exists(dailyDir)) Directory.CreateDirectory(dailyDir);

                double target = (double)numTarget.Value;
                bool ok = (_currentVal >= target - (double)numTolMinus.Value) && (_currentVal <= target + (double)numTolPlus.Value);
                string resultStr = ok ? "OK" : "NG";
                string timeStr = DateTime.Now.ToString("HHmmss_fff");

                string csvPath = Path.Combine(dailyDir, "InspectionLog.csv");
                bool isNew = !File.Exists(csvPath);
                using (var sw = new StreamWriter(csvPath, true, Encoding.UTF8))
                {
                    if (isNew) sw.WriteLine("Time,Target,Measured,Result");
                    sw.WriteLine($"{DateTime.Now:HH:mm:ss},{target:F2},{_currentVal:F3},{resultStr}");
                }

                int saveMode = cmbSaveImageMode.SelectedIndex;
                bool shouldSaveImage = (saveMode == 2) || (saveMode == 1 && !ok);

                if (shouldSaveImage && _bmp != null)
                {
                    string imgDir = Path.Combine(dailyDir, "Images");
                    if (!Directory.Exists(imgDir)) Directory.CreateDirectory(imgDir);

                    string imgPath = Path.Combine(imgDir, $"{timeStr}_{resultStr}.jpg");

                    // 【追加】コンボボックスの選択に合わせて縮小率を決定
                    int scaleMode = cmbSaveScale.SelectedIndex;
                    float scale = scaleMode == 2 ? 0.25f : (scaleMode == 1 ? 0.5f : 1.0f);

                    lock (_bmpLock)
                    {
                        if (scale < 1.0f)
                        {
                            // 縮小処理（容量激減）
                            int newWidth = (int)(_bmp.Width * scale);
                            int newHeight = (int)(_bmp.Height * scale);
                            using (Bitmap resizedBmp = new Bitmap(_bmp, new Size(newWidth, newHeight)))
                            {
                                resizedBmp.Save(imgPath, System.Drawing.Imaging.ImageFormat.Jpeg);
                            }
                        }
                        else
                        {
                            // 100%のまま保存
                            _bmp.Save(imgPath, System.Drawing.Imaging.ImageFormat.Jpeg);
                        }
                    }
                }

                pictureBox1.BackColor = Color.White;
                Timer t = new Timer { Interval = 100 };
                t.Tick += (s, e) => { pictureBox1.BackColor = Color.Black; t.Stop(); };
                t.Start();

                int keepDays = (int)numLogKeepDays.Value;
                Task.Run(() => DeleteOldLogs(rootPath, keepDays));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存エラー: {ex.Message}");
            }
        }

        private void DeleteOldLogs(string rootPath, int keepDays)
        {
            try
            {
                if (!Directory.Exists(rootPath)) return;
                var dirs = Directory.GetDirectories(rootPath);
                DateTime limit = DateTime.Now.Date.AddDays(-keepDays);

                foreach (var dir in dirs)
                {
                    string dirName = new DirectoryInfo(dir).Name;
                    if (dirName.Length == 8 && int.TryParse(dirName, out _))
                    {
                        if (DateTime.TryParseExact(dirName, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out DateTime dirDate))
                        {
                            if (dirDate < limit)
                            {
                                Directory.Delete(dir, true);
                            }
                        }
                    }
                }
            }
            catch { }
        }
        // =======================================================

        private void btnGoAdmin_Click(object sender, EventArgs e)
        {
            string pass = InputBox("認証確認", "管理者パスワードを入力してください:", "");
            if (pass == AdminPassword)
            {
                UpdateCalibLabel();
                if (!mainTabControl.TabPages.Contains(tabPageSettings))
                {
                    mainTabControl.TabPages.Add(tabPageSettings);
                }
                mainTabControl.SelectedTab = tabPageSettings;

                tabPageSettings.Controls.Add(pictureBox1);
                pictureBox1.BringToFront();
                ResetView();
            }
            else if (!string.IsNullOrEmpty(pass))
            {
                MessageBox.Show("パスワードが一致しません。", "認証エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void btnCloseAdmin_Click(object sender, EventArgs e)
        {
            SaveAppConfig();
            mainTabControl.SelectedTab = tabPageMain;
            if (mainTabControl.TabPages.Contains(tabPageSettings))
            {
                mainTabControl.TabPages.Remove(tabPageSettings);
            }

            tabPageMain.Controls.Add(pictureBox1);
            pictureBox1.BringToFront();
            ResetView();
        }

        private void Execute3PointCalib(int type)
        {
            if (_lastPx <= 0) { MessageBox.Show("カメラの計測データが取得できていません。"); return; }
            string v = InputBox("3点位置校正", $"現在のY座標位置({_lastCenterY:F0}px)での実寸(mm)を入力:", "");
            if (double.TryParse(v, out double mm) && mm > 0)
            {
                double computedRatio = mm / _lastPx;
                if (type == 0) { _mmPerPixelTop = computedRatio; _refYTop = _lastCenterY; }
                else if (type == 1) { _mmPerPixelMid = computedRatio; _refYMid = _lastCenterY; }
                else if (type == 2) { _mmPerPixelBot = computedRatio; _refYBot = _lastCenterY; }

                SaveAppConfig();
                UpdateCalibLabel();
                MessageBox.Show("校正値を更新・保存しました。");
            }
        }

        private void UpdateCalibLabel()
        {
            lblCalibStatus.Text = $"【現在のレンズ収差補正ステータス】\n" +
                                  $"上部 (Upper)  : 基準Y={_refYTop:F0} px -> 係数={_mmPerPixelTop:F6} mm/px\n" +
                                  $"中央 (Middle) : 基準Y={_refYMid:F0} px -> 係数={_mmPerPixelMid:F6} mm/px\n" +
                                  $"下部 (Lower)  : 基準Y={_refYBot:F0} px -> 係数={_mmPerPixelBot:F6} mm/px";
        }

        private void SaveAppConfig()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt");
                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"{_mmPerPixelTop}");
                sb.AppendLine($"{_mmPerPixelMid}");
                sb.AppendLine($"{_mmPerPixelBot}");
                sb.AppendLine($"{_refYTop}");
                sb.AppendLine($"{_refYMid}");
                sb.AppendLine($"{_refYBot}");
                sb.AppendLine($"{trackBarRoiX.Value}");
                sb.AppendLine($"{trackBarRoiY.Value}");
                sb.AppendLine($"{numRoiWidth.Value}");
                sb.AppendLine($"{numRoiHeight.Value}");
                sb.AppendLine($"{numTarget.Value}");
                sb.AppendLine($"{numTolPlus.Value}");
                sb.AppendLine($"{numTolMinus.Value}");
                sb.AppendLine($"{numMaxAngle.Value}");
                sb.AppendLine($"{numUpdateInterval.Value}");
                sb.AppendLine($"{cmbSaveImageMode.SelectedIndex}");
                sb.AppendLine($"{numLogKeepDays.Value}");
                sb.AppendLine($"{cmbSaveScale.SelectedIndex}"); // 追加: 保存サイズ
                File.WriteAllText(path, sb.ToString());
            }
            catch { }
        }

        private void LoadAppConfig()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt");
                if (File.Exists(path))
                {
                    string[] lines = File.ReadAllLines(path);
                    if (lines.Length >= 18)
                    { // 17 -> 18行に拡張
                        _mmPerPixelTop = double.Parse(lines[0]);
                        _mmPerPixelMid = double.Parse(lines[1]);
                        _mmPerPixelBot = double.Parse(lines[2]);
                        _refYTop = double.Parse(lines[3]);
                        _refYMid = double.Parse(lines[4]);
                        _refYBot = double.Parse(lines[5]);
                        trackBarRoiX.Value = int.Parse(lines[6]);
                        trackBarRoiY.Value = int.Parse(lines[7]);
                        numRoiWidth.Value = decimal.Parse(lines[8]);
                        numRoiHeight.Value = decimal.Parse(lines[9]);
                        numTarget.Value = decimal.Parse(lines[10]);
                        numTolPlus.Value = decimal.Parse(lines[11]);
                        numTolMinus.Value = decimal.Parse(lines[12]);
                        numMaxAngle.Value = decimal.Parse(lines[13]);
                        numUpdateInterval.Value = decimal.Parse(lines[14]);

                        int mode = int.Parse(lines[15]);
                        if (mode >= 0 && mode <= 2) cmbSaveImageMode.SelectedIndex = mode;
                        numLogKeepDays.Value = decimal.Parse(lines[16]);

                        int scale = int.Parse(lines[17]);
                        if (scale >= 0 && scale <= 2) cmbSaveScale.SelectedIndex = scale;
                    }
                }
                UpdateCalibLabel();
            }
            catch { }
        }

        private void ResetView()
        {
            lock (_bmpLock)
            {
                if (_bmp == null || pictureBox1.Width <= 0 || pictureBox1.Height <= 0) return;
                float r = Math.Min((float)pictureBox1.Width / _bmp.Width, (float)pictureBox1.Height / _bmp.Height);
                _zoom = r * 0.98f;
                _offset.X = (pictureBox1.Width - _bmp.Width * _zoom) / 2;
                _offset.Y = (pictureBox1.Height - _bmp.Height * _zoom) / 2;
            }
            if (pictureBox1.IsHandleCreated)
            {
                pictureBox1.BeginInvoke(new Action(() => pictureBox1.Invalidate()));
            }
        }

        private void AddLabel(Panel p, string t, int y, bool b = false) { Label l = new Label { Text = t, Location = new Point(10, y), AutoSize = true }; if (b) l.Font = new Font("Segoe UI", 10, FontStyle.Bold); p.Controls.Add(l); }
        private NumericUpDown AddNum(Panel p, string t, int y, decimal v, int d, decimal i = 1) { AddLabel(p, t, y); var n = new NumericUpDown { Location = new Point(15, y + 20), Width = 230, DecimalPlaces = d, Increment = i, Minimum = 0, Maximum = 10000 }; n.Value = v; p.Controls.Add(n); return n; }
        private ComboBox AddCombo(Panel p, string t, int y, string[] items, int selectedIndex)
        {
            AddLabel(p, t, y);
            ComboBox cmb = new ComboBox { Location = new Point(15, y + 20), Width = 230, DropDownStyle = ComboBoxStyle.DropDownList };
            cmb.Items.AddRange(items);
            if (items.Length > selectedIndex) cmb.SelectedIndex = selectedIndex;
            p.Controls.Add(cmb);
            return cmb;
        }
        public static string InputBox(string t, string p, string v) { Form f = new Form { Text = t, Width = 300, Height = 150, StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog }; Label l = new Label { Text = p, Left = 10, Top = 10, AutoSize = true }; TextBox tx = new TextBox { Text = v, Left = 10, Top = 30, Width = 250 }; Button b = new Button { Text = "OK", Left = 180, Top = 70, DialogResult = DialogResult.OK }; f.Controls.AddRange(new Control[] { l, tx, b }); return f.ShowDialog() == DialogResult.OK ? tx.Text : ""; }
    }
}