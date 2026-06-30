using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using OpenCvSharp;
using OpenCvSharp.Extensions;

// 名前の衝突を防ぐためのエイリアス定義
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;
using Timer = System.Windows.Forms.Timer;
using CvPoint = OpenCvSharp.Point;

namespace MeasurementSystem
{
    public partial class Form1 : Form
    {
        private ICamera cameraL;
        private ICamera cameraR;
        private MeasurementEngine _engineL = new MeasurementEngine();
        private MeasurementEngine _engineR = new MeasurementEngine();

        private double _currentValL = 0, _lastPxL = 0, _lastCenterYL = 1024;
        private double _currentValR = 0, _lastPxR = 0, _lastCenterYR = 1024;
        private bool _isOkL = false, _isOkR = false;

        private double _mmPerPixelTopL = 0.176, _mmPerPixelMidL = 0.176, _mmPerPixelBotL = 0.176;
        private double _refYTopL = 400.0, _refYMidL = 1024.0, _refYBotL = 1600.0;
        private double _mmPerPixelTopR = 0.176, _mmPerPixelMidR = 0.176, _mmPerPixelBotR = 0.176;
        private double _refYTopR = 400.0, _refYMidR = 1024.0, _refYBotR = 1600.0;

        private const string AdminPassword = "admin";

        private TabControl mainTabControl;
        private TabPage tabPageMain, tabPageSettings;

        private Label lblBigResult;
        private TextBox txtSavePath;
        private TableLayoutPanel pnlCameraViews;
        private PictureBox picLeft, picRight;

        // UIコントロール
        private NumericUpDown numTarget, numTolPlus, numTolMinus, numRoiWidth, numRoiHeight;
        private NumericUpDown numMaxAngle, numUpdateInterval, numLogKeepDays;
        private NumericUpDown numThreshold; // 二値化閾値
        private TrackBar trackBarRoiX, trackBarRoiY;
        private ComboBox cmbSaveImageMode, cmbSaveScale, cmbTargetCamera;
        private Label lblCalibStatus;

        private DateTime _lastTextUpdate = DateTime.MinValue;
        private Bitmap _bmpL, _bmpR;
        private readonly object _bmpLock = new object();

        public Form1()
        {
            InitializeCustomUI();
            LoadAppConfig();

            try
            {
                cameraL = new TeliCamera(0);
                cameraR = new TeliCamera(1);
            }
            catch { MessageBox.Show("カメラの初期化（2台接続）でエラーが発生しました。"); }

            this.KeyPreview = true;
            this.KeyDown += (s, e) => { if (e.KeyCode == Keys.Space) SaveData(); };
            this.Load += (s, e) => {
                if (cameraL != null && cameraL.Initialize())
                {
                    cameraL.OnFrameCaptured += CameraL_OnFrameCaptured;
                    cameraL.StartCapture();
                }
                if (cameraR != null && cameraR.Initialize())
                {
                    cameraR.OnFrameCaptured += CameraR_OnFrameCaptured;
                    cameraR.StartCapture();
                }
            };
            this.FormClosing += (s, e) => {
                SaveAppConfig();
                cameraL?.Terminate();
                cameraR?.Terminate();
            };
        }

        private void InitializeCustomUI()
        {
            this.Text = "FA AI Inspection System - Dual Camera Model";
            this.Size = new Size(1600, 950);

            mainTabControl = new TabControl { Dock = DockStyle.Fill, Appearance = TabAppearance.FlatButtons, ItemSize = new Size(0, 1), SizeMode = TabSizeMode.Fixed };
            this.Controls.Add(mainTabControl);

            tabPageMain = new TabPage { Text = "Main" };
            mainTabControl.TabPages.Add(tabPageMain);

            Panel pnlOperator = new Panel { Dock = DockStyle.Right, Width = 280, BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.White };
            tabPageMain.Controls.Add(pnlOperator);

            Panel pnlBottomSave = new Panel { Dock = DockStyle.Bottom, Height = 120 };
            Button btnSaveLog = new Button { Text = "💾 記録 (SAVE)", Size = new Size(240, 90), Location = new Point(20, 10), BackColor = Color.FromArgb(0, 122, 204), FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 16, FontStyle.Bold) };
            btnSaveLog.Click += (s, e) => SaveData();
            pnlBottomSave.Controls.Add(btnSaveLog);
            pnlOperator.Controls.Add(pnlBottomSave);

            pnlCameraViews = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Black };
            pnlCameraViews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            pnlCameraViews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

            picLeft = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom };
            picRight = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom };
            pnlCameraViews.Controls.Add(picLeft, 0, 0);
            pnlCameraViews.Controls.Add(picRight, 1, 0);
            tabPageMain.Controls.Add(pnlCameraViews);

            int opY = 15;
            lblBigResult = new Label { Text = "READY", Location = new Point(15, opY), Width = 240, Height = 70, Font = new Font("Segoe UI", 24, FontStyle.Bold), BackColor = Color.FromArgb(28, 28, 28), ForeColor = Color.Gray, TextAlign = ContentAlignment.MiddleCenter };
            pnlOperator.Controls.Add(lblBigResult); opY += 95;

            Button btnGoAdmin = new Button { Text = "管理者設定メニュー ⚙", Location = new Point(15, opY), Width = 240, Height = 45, BackColor = Color.FromArgb(70, 70, 74), FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 11, FontStyle.Bold) };
            btnGoAdmin.Click += btnGoAdmin_Click;
            pnlOperator.Controls.Add(btnGoAdmin); opY += 70;

            AddLabel(pnlOperator, "■ 保存先 (ルートフォルダ)", opY, true); opY += 30;
            txtSavePath = new TextBox { Location = new Point(15, opY), Width = 175, Text = AppDomain.CurrentDomain.BaseDirectory };
            pnlOperator.Controls.Add(txtSavePath);
            Button btnBrowse = new Button { Text = "...", Location = new Point(195, opY - 1), Width = 50, BackColor = Color.Gray };
            btnBrowse.Click += (s, e) => { using (var f = new FolderBrowserDialog()) if (f.ShowDialog() == DialogResult.OK) txtSavePath.Text = f.SelectedPath; };
            pnlOperator.Controls.Add(btnBrowse); opY += 45;

            tabPageSettings = new TabPage { Text = "Settings", BackColor = Color.FromArgb(30, 30, 30) };
            Panel pnlSettingsLeft = new Panel { Dock = DockStyle.Left, Width = 460, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.White };
            Panel pnlSettingsRight = new Panel { Dock = DockStyle.Right, Width = 320, BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.White, AutoScroll = true };
            tabPageSettings.Controls.Add(pnlSettingsLeft);
            tabPageSettings.Controls.Add(pnlSettingsRight);

            int calY = 20;
            AddLabel(pnlSettingsLeft, "■ レンズ収差補正（カメラ選択）", calY, true); calY += 30;
            cmbTargetCamera = AddCombo(pnlSettingsLeft, "校正対象カメラ:", calY, new[] { "Left (左カメラ)", "Right (右カメラ)" }, 0);
            cmbTargetCamera.SelectedIndexChanged += (s, e) => UpdateCalibLabel();
            calY += 60;

            lblCalibStatus = new Label { Location = new Point(15, calY), Width = 430, Height = 120, Font = new Font("Consolas", 10, FontStyle.Regular), BackColor = Color.Black, ForeColor = Color.Lime, Text = "校正データ未読込" };
            pnlSettingsLeft.Controls.Add(lblCalibStatus); calY += 130;

            Button btnCalibTop = new Button { Text = "1. 上部エリア校正実行 (Upper)", Location = new Point(15, calY), Width = 400, Height = 40, BackColor = Color.FromArgb(0, 122, 204), FlatStyle = FlatStyle.Flat };
            btnCalibTop.Click += (s, e) => Execute3PointCalib(0);
            pnlSettingsLeft.Controls.Add(btnCalibTop); calY += 50;
            Button btnCalibMid = new Button { Text = "2. 中央エリア校正実行 (Middle)", Location = new Point(15, calY), Width = 400, Height = 40, BackColor = Color.FromArgb(0, 122, 204), FlatStyle = FlatStyle.Flat };
            btnCalibMid.Click += (s, e) => Execute3PointCalib(1);
            pnlSettingsLeft.Controls.Add(btnCalibMid); calY += 50;
            Button btnCalibBot = new Button { Text = "3. 下部エリア校正実行 (Lower)", Location = new Point(15, calY), Width = 400, Height = 40, BackColor = Color.FromArgb(0, 122, 204), FlatStyle = FlatStyle.Flat };
            btnCalibBot.Click += (s, e) => Execute3PointCalib(2);
            pnlSettingsLeft.Controls.Add(btnCalibBot); calY += 70;
            Button btnCloseAdmin = new Button { Text = "◀ 設定を確定して戻る", Location = new Point(15, calY), Width = 400, Height = 55, BackColor = Color.FromArgb(16, 124, 65), FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 12, FontStyle.Bold) };
            btnCloseAdmin.Click += btnCloseAdmin_Click;
            pnlSettingsLeft.Controls.Add(btnCloseAdmin);

            int setY = 15;
            AddLabel(pnlSettingsRight, "■ 計測・公差設定", setY, true); setY += 30;
            numTarget = AddNum(pnlSettingsRight, "目標値 (ピッチmm):", setY, 180.00m, 2, 0.01m); setY += 65;
            numTolPlus = AddNum(pnlSettingsRight, "上限公差 (+mm):", setY, 0.30m, 2, 0.01m); setY += 65;
            numTolMinus = AddNum(pnlSettingsRight, "下限公差 (-mm):", setY, 0.30m, 2, 0.01m); setY += 80;

            AddLabel(pnlSettingsRight, "■ 画像処理設定", setY, true); setY += 30;
            numThreshold = AddNum(pnlSettingsRight, "二値化閾値 (0-255):", setY, 128m, 0, 1m); numThreshold.Maximum = 255; setY += 65;
            numMaxAngle = AddNum(pnlSettingsRight, "許容角度 (±度):", setY, 2.0m, 1, 0.1m); setY += 65;
            numUpdateInterval = AddNum(pnlSettingsRight, "数値更新間隔 (ms):", setY, 500m, 0, 100m); setY += 80;

            AddLabel(pnlSettingsRight, "■ 視野設定 (ROI設定)", setY, true); setY += 30;
            trackBarRoiX = new TrackBar { Location = new Point(15, setY + 22), Width = 260, Minimum = 0, Maximum = 10000, TickStyle = TickStyle.None };
            pnlSettingsRight.Controls.Add(trackBarRoiX); setY += 65;
            numRoiWidth = AddNum(pnlSettingsRight, "X計測幅 (mmサイズ):", setY, 200m, 0); setY += 65;
            trackBarRoiY = new TrackBar { Location = new Point(15, setY + 22), Width = 260, Minimum = 0, Maximum = 10000, TickStyle = TickStyle.None };
            pnlSettingsRight.Controls.Add(trackBarRoiY); setY += 65;
            numRoiHeight = AddNum(pnlSettingsRight, "Y計測高さ (mmサイズ):", setY, 100m, 0); setY += 80;

            AddLabel(pnlSettingsRight, "■ ログ・保存詳細設定", setY, true); setY += 30;
            cmbSaveImageMode = AddCombo(pnlSettingsRight, "画像保存モード:", setY, new[] { "0: 画像保存しない", "1: NGの時のみ保存", "2: すべて保存" }, 2); setY += 65;
            cmbSaveScale = AddCombo(pnlSettingsRight, "保存画像サイズ (縮小率):", setY, new[] { "0: 100% (そのまま)", "1: 50% (推奨・容量1/4)", "2: 25% (激軽・容量1/16)" }, 1); setY += 65;
            numLogKeepDays = AddNum(pnlSettingsRight, "ログ保持日数 (日):", setY, 30m, 0, 1m);
        }

        private void CameraL_OnFrameCaptured(object sender, Mat frame)
        {
            ProcessCameraFrame(frame, _engineL, true, _mmPerPixelTopL, _mmPerPixelMidL, _mmPerPixelBotL, _refYTopL, _refYMidL, _refYBotL);
        }

        private void CameraR_OnFrameCaptured(object sender, Mat frame)
        {
            ProcessCameraFrame(frame, _engineR, false, _mmPerPixelTopR, _mmPerPixelMidR, _mmPerPixelBotR, _refYTopR, _refYMidR, _refYBotR);
        }

        private void ProcessCameraFrame(Mat frame, MeasurementEngine engine, bool isLeft,
                                        double mTop, double mMid, double mBot, double yTop, double yMid, double yBot)
        {
            try
            {
                double target = 0, tolPlus = 0, tolMinus = 0, maxAngle = 1.0, intervalMs = 500;
                int rX = 0, rY = 0, rW_px = 50, rH_px = 50, threshVal = 128;

                this.Invoke(new Action(() => {
                    rW_px = Math.Max(1, (int)(numRoiWidth.Value / (decimal)mMid));
                    rH_px = Math.Max(1, (int)(numRoiHeight.Value / (decimal)mMid));
                    int maxPosX = Math.Max(0, frame.Cols - rW_px);
                    if (trackBarRoiX.Maximum != maxPosX) trackBarRoiX.Maximum = maxPosX;
                    int maxPosY = Math.Max(0, frame.Rows - rH_px);
                    if (trackBarRoiY.Maximum != maxPosY) trackBarRoiY.Maximum = maxPosY;

                    target = (double)numTarget.Value;
                    tolPlus = (double)numTolPlus.Value;
                    tolMinus = (double)numTolMinus.Value;
                    maxAngle = (double)numMaxAngle.Value;
                    intervalMs = (double)numUpdateInterval.Value;
                    threshVal = (int)numThreshold.Value;
                    rX = trackBarRoiX.Value;
                    rY = trackBarRoiY.Value;
                }));

                using (Mat drawMat = new Mat())
                {
                    Cv2.CvtColor(frame, drawMat, ColorConversionCodes.GRAY2BGR);

                    int safeW = (int)(frame.Cols * 0.66);
                    int safeH = (int)(frame.Rows * 0.66);
                    Rect safeRect = new Rect((frame.Cols - safeW) / 2, (frame.Rows - safeH) / 2, safeW, safeH);
                    Cv2.Rectangle(drawMat, safeRect, Scalar.Yellow, 3, LineTypes.AntiAlias);
                    Cv2.PutText(drawMat, "50um Guaranteed Area", new CvPoint(safeRect.X + 10, safeRect.Y + 40), HersheyFonts.HersheyComplex, 1.2, Scalar.Yellow, 2);

                    var resObj = engine.ProcessFrame(frame, drawMat, rX, rY, rW_px, rH_px, mTop, mMid, mBot, yTop, yMid, yBot, threshVal);

                    if (resObj.IsValid)
                    {
                        bool angleOk = Math.Abs(resObj.AngleDegree) <= maxAngle;
                        bool sizeOk = (resObj.MeasuredValueMm >= target - tolMinus) && (resObj.MeasuredValueMm <= target + tolPlus);
                        bool isTotalOk = angleOk && sizeOk;

                        if (isLeft) { _lastPxL = resObj.MeasuredValuePx; _currentValL = resObj.MeasuredValueMm; _lastCenterYL = resObj.CenterY; _isOkL = isTotalOk; }
                        else { _lastPxR = resObj.MeasuredValuePx; _currentValR = resObj.MeasuredValueMm; _lastCenterYR = resObj.CenterY; _isOkR = isTotalOk; }

                        if ((DateTime.Now - _lastTextUpdate).TotalMilliseconds >= intervalMs)
                        {
                            UpdateBigResultLabel();
                            _lastTextUpdate = DateTime.Now;
                        }

                        string text = $"{(isTotalOk ? "OK" : "NG")} PITCH: {resObj.MeasuredValueMm:F3}mm";
                        Scalar color = isTotalOk ? Scalar.Lime : Scalar.Red;
                        Cv2.PutText(drawMat, text, new CvPoint(50, 150), HersheyFonts.HersheyComplex, 3.0, color, 6);
                        Cv2.PutText(drawMat, $"L_DIA: {resObj.LeftHoleDiaMm:F2}mm", new CvPoint(50, 220), HersheyFonts.HersheyComplex, 1.5, Scalar.Cyan, 3);
                        Cv2.PutText(drawMat, $"R_DIA: {resObj.RightHoleDiaMm:F2}mm", new CvPoint(50, 290), HersheyFonts.HersheyComplex, 1.5, Scalar.Cyan, 3);
                    }

                    Bitmap next = BitmapConverter.ToBitmap(drawMat);
                    lock (_bmpLock)
                    {
                        if (isLeft) { var old = _bmpL; _bmpL = next; old?.Dispose(); }
                        else { var old = _bmpR; _bmpR = next; old?.Dispose(); }
                    }

                    PictureBox targetPic = isLeft ? picLeft : picRight;
                    if (targetPic.IsHandleCreated) targetPic.BeginInvoke(new Action(() => targetPic.Image = isLeft ? _bmpL : _bmpR));
                }
            }
            catch { }
            finally { frame.Dispose(); }
        }

        private void UpdateBigResultLabel()
        {
            this.BeginInvoke(new Action(() => {
                if (_isOkL && _isOkR) { lblBigResult.Text = "ALL OK"; lblBigResult.BackColor = Color.LimeGreen; lblBigResult.ForeColor = Color.White; }
                else { lblBigResult.Text = "NG DETECTED"; lblBigResult.BackColor = Color.Crimson; lblBigResult.ForeColor = Color.White; }
            }));
        }

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
                string resultStrL = _isOkL ? "OK" : "NG";
                string resultStrR = _isOkR ? "OK" : "NG";
                string finalResult = (_isOkL && _isOkR) ? "OK" : "NG";
                string timeStr = DateTime.Now.ToString("HHmmss_fff");

                string csvPath = Path.Combine(dailyDir, "InspectionLog.csv");
                bool isNew = !File.Exists(csvPath);
                using (var sw = new StreamWriter(csvPath, true, Encoding.UTF8))
                {
                    if (isNew) sw.WriteLine("Time,TargetPitch,L_Measured,L_Result,R_Measured,R_Result,Total");
                    sw.WriteLine($"{DateTime.Now:HH:mm:ss},{target:F2},{_currentValL:F3},{resultStrL},{_currentValR:F3},{resultStrR},{finalResult}");
                }

                int saveMode = cmbSaveImageMode.SelectedIndex;
                bool shouldSaveImage = (saveMode == 2) || (saveMode == 1 && finalResult == "NG");

                if (shouldSaveImage)
                {
                    string imgDir = Path.Combine(dailyDir, "Images");
                    if (!Directory.Exists(imgDir)) Directory.CreateDirectory(imgDir);

                    string imgPath = Path.Combine(imgDir, $"{timeStr}_L-{resultStrL}_R-{resultStrR}.jpg");

                    lock (_bmpLock)
                    {
                        if (_bmpL != null && _bmpR != null)
                        {
                            using (Mat matL = BitmapConverter.ToMat(_bmpL))
                            using (Mat matR = BitmapConverter.ToMat(_bmpR))
                            using (Mat combined = new Mat())
                            {
                                Cv2.HConcat(new Mat[] { matL, matR }, combined);
                                int scaleMode = cmbSaveScale.SelectedIndex;
                                float scale = scaleMode == 2 ? 0.25f : (scaleMode == 1 ? 0.5f : 1.0f);

                                if (scale < 1.0f)
                                {
                                    using (Mat resized = new Mat())
                                    {
                                        Cv2.Resize(combined, resized, new OpenCvSharp.Size((int)(combined.Width * scale), (int)(combined.Height * scale)));
                                        resized.SaveImage(imgPath);
                                    }
                                }
                                else { combined.SaveImage(imgPath); }
                            }
                        }
                    }
                }

                pnlCameraViews.BackColor = Color.White;
                Timer t = new Timer { Interval = 100 };
                t.Tick += (s, e) => { pnlCameraViews.BackColor = Color.Black; t.Stop(); };
                t.Start();

                int keepDays = (int)numLogKeepDays.Value;
                Task.Run(() => DeleteOldLogs(rootPath, keepDays));
            }
            catch (Exception ex) { MessageBox.Show($"保存エラー: {ex.Message}"); }
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
                    if (dirName.Length == 8 && int.TryParse(dirName, out _) && DateTime.TryParseExact(dirName, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out DateTime dirDate))
                    {
                        if (dirDate < limit) Directory.Delete(dir, true);
                    }
                }
            }
            catch { }
        }

        private void btnGoAdmin_Click(object sender, EventArgs e)
        {
            string pass = InputBox("認証確認", "管理者パスワード:", "");
            if (pass == AdminPassword)
            {
                UpdateCalibLabel();
                if (!mainTabControl.TabPages.Contains(tabPageSettings)) mainTabControl.TabPages.Add(tabPageSettings);
                mainTabControl.SelectedTab = tabPageSettings;
                pnlCameraViews.Parent = tabPageSettings;
                pnlCameraViews.BringToFront();
            }
            else if (!string.IsNullOrEmpty(pass)) { MessageBox.Show("パスワード不一致"); }
        }

        private void btnCloseAdmin_Click(object sender, EventArgs e)
        {
            SaveAppConfig();
            mainTabControl.SelectedTab = tabPageMain;
            if (mainTabControl.TabPages.Contains(tabPageSettings)) mainTabControl.TabPages.Remove(tabPageSettings);
            pnlCameraViews.Parent = tabPageMain;
            pnlCameraViews.BringToFront();
        }

        private void Execute3PointCalib(int type)
        {
            bool isLeft = cmbTargetCamera.SelectedIndex == 0;
            double targetPx = isLeft ? _lastPxL : _lastPxR;
            double targetY = isLeft ? _lastCenterYL : _lastCenterYR;
            if (targetPx <= 0) { MessageBox.Show("計測データが取得できていません。"); return; }
            string v = InputBox("3点位置校正", $"現在のY座標({targetY:F0}px)での実寸(mm):", "");
            if (double.TryParse(v, out double mm) && mm > 0)
            {
                double ratio = mm / targetPx;
                if (isLeft)
                {
                    if (type == 0) { _mmPerPixelTopL = ratio; _refYTopL = targetY; }
                    else if (type == 1) { _mmPerPixelMidL = ratio; _refYMidL = targetY; }
                    else if (type == 2) { _mmPerPixelBotL = ratio; _refYBotL = targetY; }
                }
                else
                {
                    if (type == 0) { _mmPerPixelTopR = ratio; _refYTopR = targetY; }
                    else if (type == 1) { _mmPerPixelMidR = ratio; _refYMidR = targetY; }
                    else if (type == 2) { _mmPerPixelBotR = ratio; _refYBotR = targetY; }
                }
                SaveAppConfig();
                UpdateCalibLabel(); MessageBox.Show("校正値を更新しました。");
            }
        }

        private void UpdateCalibLabel()
        {
            bool isLeft = cmbTargetCamera.SelectedIndex == 0;
            if (isLeft) lblCalibStatus.Text = $"【左カメラ 補正ステータス】\n上部: 基準Y={_refYTopL:F0} px -> 係数={_mmPerPixelTopL:F6}\n中央: 基準Y={_refYMidL:F0} px -> 係数={_mmPerPixelMidL:F6}\n下部: 基準Y={_refYBotL:F0} px -> 係数={_mmPerPixelBotL:F6}";
            else lblCalibStatus.Text = $"【右カメラ 補正ステータス】\n上部: 基準Y={_refYTopR:F0} px -> 係数={_mmPerPixelTopR:F6}\n中央: 基準Y={_refYMidR:F0} px -> 係数={_mmPerPixelMidR:F6}\n下部: 基準Y={_refYBotR:F0} px -> 係数={_mmPerPixelBotR:F6}";
        }

        private void SaveAppConfig()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt");
                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"{_mmPerPixelTopL},{_mmPerPixelMidL},{_mmPerPixelBotL},{_refYTopL},{_refYMidL},{_refYBotL}");
                sb.AppendLine($"{_mmPerPixelTopR},{_mmPerPixelMidR},{_mmPerPixelBotR},{_refYTopR},{_refYMidR},{_refYBotR}");
                sb.AppendLine($"{trackBarRoiX.Value},{trackBarRoiY.Value},{numRoiWidth.Value},{numRoiHeight.Value}");
                sb.AppendLine($"{numTarget.Value},{numTolPlus.Value},{numTolMinus.Value},{numMaxAngle.Value},{numUpdateInterval.Value},{numThreshold.Value}");
                sb.AppendLine($"{cmbSaveImageMode.SelectedIndex},{numLogKeepDays.Value},{cmbSaveScale.SelectedIndex}");
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
                    if (lines.Length >= 4)
                    {
                        var l1 = lines[0].Split(','); _mmPerPixelTopL = double.Parse(l1[0]); _mmPerPixelMidL = double.Parse(l1[1]); _mmPerPixelBotL = double.Parse(l1[2]); _refYTopL = double.Parse(l1[3]); _refYMidL = double.Parse(l1[4]); _refYBotL = double.Parse(l1[5]);
                        var l2 = lines[1].Split(','); _mmPerPixelTopR = double.Parse(l2[0]); _mmPerPixelMidR = double.Parse(l2[1]); _mmPerPixelBotR = double.Parse(l2[2]); _refYTopR = double.Parse(l2[3]); _refYMidR = double.Parse(l2[4]); _refYBotR = double.Parse(l2[5]);
                        var l3 = lines[2].Split(','); trackBarRoiX.Value = int.Parse(l3[0]); trackBarRoiY.Value = int.Parse(l3[1]); numRoiWidth.Value = decimal.Parse(l3[2]); numRoiHeight.Value = decimal.Parse(l3[3]);

                        var l4 = lines[3].Split(',');
                        numTarget.Value = decimal.Parse(l4[0]); numTolPlus.Value = decimal.Parse(l4[1]); numTolMinus.Value = decimal.Parse(l4[2]); numMaxAngle.Value = decimal.Parse(l4[3]); numUpdateInterval.Value = decimal.Parse(l4[4]);
                        if (l4.Length > 5) numThreshold.Value = decimal.Parse(l4[5]);

                        var l5 = lines[4].Split(','); cmbSaveImageMode.SelectedIndex = int.Parse(l5[0]); numLogKeepDays.Value = decimal.Parse(l5[1]); cmbSaveScale.SelectedIndex = int.Parse(l5[2]);
                    }
                }
                UpdateCalibLabel();
            }
            catch { }
        }

        private void AddLabel(Panel p, string t, int y, bool b = false) { Label l = new Label { Text = t, Location = new Point(10, y), AutoSize = true }; if (b) l.Font = new Font("Segoe UI", 10, FontStyle.Bold); p.Controls.Add(l); }
        private NumericUpDown AddNum(Panel p, string t, int y, decimal v, int d, decimal i = 1) { AddLabel(p, t, y); var n = new NumericUpDown { Location = new Point(15, y + 20), Width = 230, DecimalPlaces = d, Increment = i, Minimum = 0, Maximum = 10000 }; n.Value = v; p.Controls.Add(n); return n; }
        private ComboBox AddCombo(Panel p, string t, int y, string[] items, int selectedIndex) { AddLabel(p, t, y); ComboBox cmb = new ComboBox { Location = new Point(15, y + 20), Width = 230, DropDownStyle = ComboBoxStyle.DropDownList }; cmb.Items.AddRange(items); if (items.Length > selectedIndex) cmb.SelectedIndex = selectedIndex; p.Controls.Add(cmb); return cmb; }
        public static string InputBox(string t, string p, string v) { Form f = new Form { Text = t, Width = 300, Height = 150, StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog }; Label l = new Label { Text = p, Left = 10, Top = 10, AutoSize = true }; TextBox tx = new TextBox { Text = v, Left = 10, Top = 30, Width = 250 }; Button b = new Button { Text = "OK", Left = 180, Top = 70, DialogResult = DialogResult.OK }; f.Controls.AddRange(new Control[] { l, tx, b }); return f.ShowDialog() == DialogResult.OK ? tx.Text : ""; }
    }
}