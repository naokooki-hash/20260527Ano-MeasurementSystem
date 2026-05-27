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

namespace MeasurementSystem
{
    public partial class Form1 : Form
    {
        // ==========================================
        // 1. 変数定義
        // ==========================================
        private ICamera camera;
        private bool _isCapturing = false;
        private bool _isLoadingConfig = false;

        private readonly object _bmpLock = new object();
        private readonly object _dataLock = new object();

        private Bitmap _currentDisplayBmp = null;
        private Mat _lastDispMat = null;
        private MeasurementResult _lastResult = null;
        private double _lastCenterY = 1024;
        private bool _isFirstFrame = true;

        private float _zoom = 1.0f;
        private PointF _offset = new PointF(0, 0);
        private bool _isDragging = false;
        private Point _lastMousePos;

        private string _savePath = @"C:\InspectionData";
        private int _saveMode = 1;
        private int _resizeMode = 0;
        private int _deleteDays = 30;
        private bool _isAdmin = false;
        private const string ADMIN_PASS = "admin";

        private int _roiX = 427, _roiY = 703, _roiWidth = 1320, _roiHeight = 800;
        private double _mmPerPixelTop = 0.176, _mmPerPixelMid = 0.176, _mmPerPixelBot = 0.176;
        private double _refYTop = 400.0, _refYMid = 1024.0, _refYBot = 1600.0;

        private double _targetPitch = 100.0, _tolPlus = 0.5, _tolMinus = 0.5, _tolAngle = 1.0;

        private int _processIntervalMs = 100;
        private int _updateIntervalMs = 500;
        private DateTime _lastProcessTime = DateTime.MinValue;
        private DateTime _lastUiUpdate = DateTime.MinValue;

        private PictureBox pictureBox1;
        private Label lblBigResult, lblPitch, lblDiameterL, lblDiameterR, lblAngle;
        private TabControl mainTabControl;
        private TabPage tabPageMain, tabPageSettings;

        private Panel pnlAdminControls;
        private Button btnAdminLock;
        private TextBox txtSavePath;
        private ComboBox cmbSaveMode, cmbResizeMode;
        private NumericUpDown numDeleteDays;
        private NumericUpDown numMmTop, numMmMid, numMmBot, numYTop, numYMid, numYBot;
        private TrackBar trackBarRoiX, trackBarRoiY;
        private NumericUpDown numRoiX, numRoiY, numRoiWidth, numRoiHeight;
        private NumericUpDown numTargetPitch, numTolPlus, numTolMinus, numTolAngle, numUpdateInterval, numProcessInterval;

        public Form1()
        {
            InitializeCustomUI();
            LoadConfig();
            DeleteOldLogs();

            camera = new TeliCamera();
            this.Load += (s, e) => {
                if (camera.Initialize())
                {
                    camera.OnFrameCaptured += Camera_OnFrameCaptured;
                    _isCapturing = true;
                    camera.StartCapture();
                }
                else
                {
                    MessageBox.Show("カメラの初期化に失敗しました。USB接続を確認してください。", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
        }

        // ==========================================
        // 2. UIの動的生成 と マウスイベント設定
        // ==========================================
        private void InitializeCustomUI()
        {
            this.Text = "光学検査システム (ソフトウェアトリガー搭載版)";
            this.Size = new Size(1500, 950);
            this.FormClosing += Form1_FormClosing;

            pictureBox1 = new PictureBox { Location = new Point(10, 10), Size = new Size(1100, 850), BorderStyle = BorderStyle.FixedSingle, BackColor = Color.Black };
            pictureBox1.Paint += PictureBox1_Paint;
            pictureBox1.MouseDown += PictureBox1_MouseDown;
            pictureBox1.MouseMove += PictureBox1_MouseMove;
            pictureBox1.MouseUp += PictureBox1_MouseUp;
            pictureBox1.MouseWheel += PictureBox1_MouseWheel;
            pictureBox1.MouseEnter += (s, e) => pictureBox1.Focus();
            pictureBox1.DoubleClick += (s, e) => ResetZoom();
            this.Controls.Add(pictureBox1);

            mainTabControl = new TabControl { Location = new Point(1120, 10), Size = new Size(350, 850) };
            this.Controls.Add(mainTabControl);
            tabPageMain = new TabPage("メイン"); tabPageSettings = new TabPage("設定");
            mainTabControl.TabPages.Add(tabPageMain); mainTabControl.TabPages.Add(tabPageSettings);

            // --- メインタブ ---
            lblBigResult = new Label { Text = "WAIT", Font = new Font("Segoe UI", 56, FontStyle.Bold), Location = new Point(10, 15), Size = new Size(320, 100), TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Gray, ForeColor = Color.White };
            tabPageMain.Controls.Add(lblBigResult);

            Font resultFont = new Font("Segoe UI", 20, FontStyle.Bold);
            lblPitch = new Label { Location = new Point(10, 140), Size = new Size(320, 40), Font = resultFont };
            lblDiameterL = new Label { Location = new Point(10, 190), Size = new Size(320, 40), Font = resultFont };
            lblDiameterR = new Label { Location = new Point(10, 240), Size = new Size(320, 40), Font = resultFont };
            lblAngle = new Label { Location = new Point(10, 290), Size = new Size(320, 40), Font = resultFont };
            tabPageMain.Controls.Add(lblPitch); tabPageMain.Controls.Add(lblDiameterL); tabPageMain.Controls.Add(lblDiameterR); tabPageMain.Controls.Add(lblAngle);

            Button btnStart = new Button { Text = "Start", Location = new Point(10, 340), Size = new Size(150, 50), Font = new Font("Segoe UI", 12, FontStyle.Bold), BackColor = Color.LightBlue };
            btnStart.Click += (s, e) => { _isCapturing = true; camera?.StartCapture(); }; tabPageMain.Controls.Add(btnStart);

            Button btnStop = new Button { Text = "Stop", Location = new Point(170, 340), Size = new Size(150, 50), Font = new Font("Segoe UI", 12, FontStyle.Bold), BackColor = Color.LightCoral };
            btnStop.Click += (s, e) => { _isCapturing = false; camera?.StopCapture(); }; tabPageMain.Controls.Add(btnStop);

            Button btnRecord = new Button { Text = "保存 (Spaceキー)", Location = new Point(10, 410), Size = new Size(310, 60), Font = new Font("Segoe UI", 16, FontStyle.Bold), BackColor = Color.LightGreen };
            btnRecord.Click += (s, e) => SaveMeasurementData(); tabPageMain.Controls.Add(btnRecord);

            // --- 設定タブ ---
            btnAdminLock = new Button { Text = "管理者ロック解除", Location = new Point(10, 10), Size = new Size(315, 40), Font = new Font("Segoe UI", 12, FontStyle.Bold), BackColor = Color.Orange };
            btnAdminLock.Click += BtnAdminLock_Click; tabPageSettings.Controls.Add(btnAdminLock);
            pnlAdminControls = new Panel { Location = new Point(0, 60), Size = new Size(340, 750), AutoScroll = true, Enabled = false };
            tabPageSettings.Controls.Add(pnlAdminControls);

            int yPos = 0;
            AddLabel(pnlAdminControls, "■ 保存・ログ設定", yPos, true); yPos += 25;
            AddLabel(pnlAdminControls, "保存先フォルダ:", yPos); yPos += 20;
            txtSavePath = new TextBox { Location = new Point(15, yPos), Width = 230, Text = _savePath };
            Button btnBrowse = new Button { Text = "参照", Location = new Point(250, yPos - 1), Width = 50 };
            btnBrowse.Click += (s, e) => { using (var fbd = new FolderBrowserDialog()) { if (fbd.ShowDialog() == DialogResult.OK) { txtSavePath.Text = fbd.SelectedPath; Settings_ValueChanged(null, null); } } };
            pnlAdminControls.Controls.Add(txtSavePath); pnlAdminControls.Controls.Add(btnBrowse); yPos += 30;

            cmbSaveMode = AddCombo(pnlAdminControls, "画像保存モード:", ref yPos, new[] { "全て保存", "NGのみ保存", "保存しない" }, _saveMode);
            cmbResizeMode = AddCombo(pnlAdminControls, "画像サイズ縮小:", ref yPos, new[] { "100% (原寸)", "50% (軽量)", "25% (最小)" }, _resizeMode);
            numDeleteDays = AddNum(pnlAdminControls, "自動削除 (日経過):", ref yPos, _deleteDays, 0, 1);

            yPos += 10;
            AddLabel(pnlAdminControls, "■ 公差・更新設定", yPos, true); yPos += 25;
            numTargetPitch = AddNum(pnlAdminControls, "ピッチ目標値 (mm):", ref yPos, (decimal)_targetPitch, 2, 0.1m);
            numTolPlus = AddNum(pnlAdminControls, "上限公差 (+mm):", ref yPos, (decimal)_tolPlus, 2, 0.1m);
            numTolMinus = AddNum(pnlAdminControls, "下限公差 (-mm):", ref yPos, (decimal)_tolMinus, 2, 0.1m);
            numTolAngle = AddNum(pnlAdminControls, "許容角度 (±度):", ref yPos, (decimal)_tolAngle, 2, 0.1m);
            numProcessInterval = AddNum(pnlAdminControls, "画像処理間隔(ms) [100=10fps]:", ref yPos, _processIntervalMs, 0, 10m);
            numUpdateInterval = AddNum(pnlAdminControls, "文字更新間隔 (ms):", ref yPos, _updateIntervalMs, 0, 100m);

            yPos += 10; AddLabel(pnlAdminControls, "■ キャリブレーション", yPos, true); yPos += 25;
            numMmTop = AddNum(pnlAdminControls, "上段 mm/Pixel:", ref yPos, (decimal)_mmPerPixelTop, 5, 0.0001m);
            numYTop = AddNum(pnlAdminControls, "上段 基準Y:", ref yPos, (decimal)_refYTop, 1, 10m);
            Button btnCalibTop = new Button { Text = "↑ 上段キャリブレーション", Location = new Point(15, yPos), Width = 280, Height = 35 };
            btnCalibTop.Click += (s, e) => ExecuteCalibration(numMmTop, numYTop); pnlAdminControls.Controls.Add(btnCalibTop); yPos += 45;
            numMmMid = AddNum(pnlAdminControls, "中段 mm/Pixel:", ref yPos, (decimal)_mmPerPixelMid, 5, 0.0001m);
            numYMid = AddNum(pnlAdminControls, "中段 基準Y:", ref yPos, (decimal)_refYMid, 1, 10m);
            Button btnCalibMid = new Button { Text = "− 中段キャリブレーション", Location = new Point(15, yPos), Width = 280, Height = 35 };
            btnCalibMid.Click += (s, e) => ExecuteCalibration(numMmMid, numYMid); pnlAdminControls.Controls.Add(btnCalibMid); yPos += 45;
            numMmBot = AddNum(pnlAdminControls, "下段 mm/Pixel:", ref yPos, (decimal)_mmPerPixelBot, 5, 0.0001m);
            numYBot = AddNum(pnlAdminControls, "下段 基準Y:", ref yPos, (decimal)_refYBot, 1, 10m);
            Button btnCalibBot = new Button { Text = "↓ 下段キャリブレーション", Location = new Point(15, yPos), Width = 280, Height = 35 };
            btnCalibBot.Click += (s, e) => ExecuteCalibration(numMmBot, numYBot); pnlAdminControls.Controls.Add(btnCalibBot); yPos += 45;

            yPos += 10; AddLabel(pnlAdminControls, "■ 視野設定 (ROI)", yPos, true); yPos += 25;
            AddLabel(pnlAdminControls, "ROI X (横位置):", yPos); yPos += 20;
            trackBarRoiX = new TrackBar { Location = new Point(10, yPos), Width = 200, Maximum = 2448, TickStyle = TickStyle.None, Value = Math.Min(_roiX, 2448) };
            numRoiX = new NumericUpDown { Location = new Point(215, yPos), Width = 80, Maximum = 4000, Value = _roiX };
            trackBarRoiX.Scroll += (s, e) => { numRoiX.Value = trackBarRoiX.Value; Settings_ValueChanged(null, null); };
            numRoiX.ValueChanged += (s, e) => { if (numRoiX.Value <= trackBarRoiX.Maximum) trackBarRoiX.Value = (int)numRoiX.Value; Settings_ValueChanged(null, null); };
            pnlAdminControls.Controls.Add(trackBarRoiX); pnlAdminControls.Controls.Add(numRoiX); yPos += 40;
            AddLabel(pnlAdminControls, "ROI Y (縦位置):", yPos); yPos += 20;
            trackBarRoiY = new TrackBar { Location = new Point(10, yPos), Width = 200, Maximum = 2048, TickStyle = TickStyle.None, Value = Math.Min(_roiY, 2048) };
            numRoiY = new NumericUpDown { Location = new Point(215, yPos), Width = 80, Maximum = 4000, Value = _roiY };
            trackBarRoiY.Scroll += (s, e) => { numRoiY.Value = trackBarRoiY.Value; Settings_ValueChanged(null, null); };
            numRoiY.ValueChanged += (s, e) => { if (numRoiY.Value <= trackBarRoiY.Maximum) trackBarRoiY.Value = (int)numRoiY.Value; Settings_ValueChanged(null, null); };
            pnlAdminControls.Controls.Add(trackBarRoiY); pnlAdminControls.Controls.Add(numRoiY); yPos += 40;

            numRoiWidth = AddNum(pnlAdminControls, "ROI 幅:", ref yPos, _roiWidth, 0, 1);
            numRoiHeight = AddNum(pnlAdminControls, "ROI 高さ:", ref yPos, _roiHeight, 0, 1);

            txtSavePath.TextChanged += Settings_ValueChanged; cmbSaveMode.SelectedIndexChanged += Settings_ValueChanged; cmbResizeMode.SelectedIndexChanged += Settings_ValueChanged;
        }

        // --- ヘルパー関数 ---
        private void AddLabel(Panel p, string t, int y, bool b = false) { Label l = new Label { Text = t, Location = new Point(10, y), AutoSize = true }; if (b) l.Font = new Font("Segoe UI", 10, FontStyle.Bold); p.Controls.Add(l); }
        private NumericUpDown AddNum(Panel p, string t, ref int y, decimal v, int d, decimal i) { AddLabel(p, t, y); var n = new NumericUpDown { Location = new Point(15, y + 20), Width = 280, DecimalPlaces = d, Increment = i, Maximum = 10000, Value = v }; n.ValueChanged += Settings_ValueChanged; p.Controls.Add(n); y += 45; return n; }
        private ComboBox AddCombo(Panel p, string t, ref int y, string[] items, int selIdx) { AddLabel(p, t, y); ComboBox c = new ComboBox { Location = new Point(15, y + 20), Width = 280, DropDownStyle = ComboBoxStyle.DropDownList }; c.Items.AddRange(items); c.SelectedIndex = selIdx; p.Controls.Add(c); y += 45; return c; }

        public static string InputBox(string title, string promptText, string value, bool isPassword = false)
        {
            Form form = new Form { Text = title, ClientSize = new Size(396, 120), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent };
            Label label = new Label { Text = promptText, Bounds = new Rectangle(10, 20, 360, 13), AutoSize = true };
            TextBox textBox = new TextBox { Text = value, Bounds = new Rectangle(12, 40, 360, 20) };
            if (isPassword) textBox.PasswordChar = '*';
            Button buttonOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Bounds = new Rectangle(210, 75, 75, 30) };
            Button buttonCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(295, 75, 75, 30) };
            form.Controls.AddRange(new Control[] { label, textBox, buttonOk, buttonCancel });
            form.AcceptButton = buttonOk; form.CancelButton = buttonCancel;
            return form.ShowDialog() == DialogResult.OK ? textBox.Text : "";
        }

        // ==========================================
        // 3. マウス操作 (ズーム・パン・描画)
        // ==========================================
        private void ResetZoom()
        {
            lock (_bmpLock)
            {
                if (_currentDisplayBmp == null || pictureBox1.Width <= 0 || pictureBox1.Height <= 0) return;
                float scaleX = (float)pictureBox1.Width / _currentDisplayBmp.Width;
                float scaleY = (float)pictureBox1.Height / _currentDisplayBmp.Height;
                _zoom = Math.Max(0.01f, Math.Min(scaleX, scaleY));
                float cx = (pictureBox1.Width - _currentDisplayBmp.Width * _zoom) / 2f;
                float cy = (pictureBox1.Height - _currentDisplayBmp.Height * _zoom) / 2f;
                _offset = new PointF(cx, cy);
            }
            pictureBox1.Invalidate();
        }

        private void PictureBox1_Paint(object sender, PaintEventArgs e)
        {
            lock (_bmpLock)
            {
                if (_currentDisplayBmp == null) return;
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.TranslateTransform(_offset.X, _offset.Y);
                e.Graphics.ScaleTransform(_zoom, _zoom);
                e.Graphics.DrawImage(_currentDisplayBmp, new PointF(0, 0));
            }
        }

        private void PictureBox1_MouseWheel(object sender, MouseEventArgs e)
        {
            float oldZoom = _zoom;
            if (e.Delta > 0) _zoom *= 1.15f;
            else _zoom /= 1.15f;
            _zoom = Math.Max(0.05f, Math.Min(_zoom, 20.0f));

            PointF mousePos = new PointF(e.X, e.Y);
            _offset.X = mousePos.X - (mousePos.X - _offset.X) * (_zoom / oldZoom);
            _offset.Y = mousePos.Y - (mousePos.Y - _offset.Y) * (_zoom / oldZoom);
            pictureBox1.Invalidate();
        }

        private void PictureBox1_MouseDown(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Middle) { _isDragging = true; _lastMousePos = e.Location; } }
        private void PictureBox1_MouseMove(object sender, MouseEventArgs e) { if (_isDragging) { _offset.X += (e.X - _lastMousePos.X); _offset.Y += (e.Y - _lastMousePos.Y); _lastMousePos = e.Location; pictureBox1.Invalidate(); } }
        private void PictureBox1_MouseUp(object sender, MouseEventArgs e) { _isDragging = false; }

        // ==========================================
        // 4. 管理者ロック・保存・自動削除・設定同期
        // ==========================================
        private void BtnAdminLock_Click(object sender, EventArgs e)
        {
            if (_isAdmin) { _isAdmin = false; pnlAdminControls.Enabled = false; btnAdminLock.Text = "管理者ロック解除"; btnAdminLock.BackColor = Color.Orange; }
            else { if (InputBox("管理者ログイン", "パスワード:", "", true) == ADMIN_PASS) { _isAdmin = true; pnlAdminControls.Enabled = true; btnAdminLock.Text = "ロック中 (クリックで施錠)"; btnAdminLock.BackColor = Color.LimeGreen; } else MessageBox.Show("パスワードが違います。", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) { if (keyData == Keys.Space) { SaveMeasurementData(); return true; } return base.ProcessCmdKey(ref msg, keyData); }

        private void SaveMeasurementData()
        {
            MeasurementResult resToSave = null;
            Mat matToSave = null;

            lock (_dataLock)
            {
                if (_lastResult == null || !_lastResult.IsValid) return;
                resToSave = _lastResult;
                if (_lastDispMat != null && !_lastDispMat.IsDisposed)
                {
                    matToSave = _lastDispMat.Clone();
                }
            }

            bool isPitchOk = (resToSave.PitchMm >= _targetPitch - _tolMinus) && (resToSave.PitchMm <= _targetPitch + _tolPlus);
            bool isAngleOk = Math.Abs(resToSave.AngleDegree) <= _tolAngle;
            bool isOk = isPitchOk && isAngleOk;

            if (_saveMode == 2) { matToSave?.Dispose(); return; }
            if (_saveMode == 1 && isOk) { matToSave?.Dispose(); return; }

            Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(_savePath)) Directory.CreateDirectory(_savePath);
                    string folderPath = Path.Combine(_savePath, DateTime.Now.ToString("yyyyMMdd"));
                    if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);

                    string timeStr = DateTime.Now.ToString("HHmmss");
                    string judgeStr = isOk ? "OK" : "NG";

                    string csvPath = Path.Combine(folderPath, "InspectionLog.csv");
                    bool isNew = !File.Exists(csvPath);
                    using (StreamWriter sw = new StreamWriter(csvPath, true, Encoding.UTF8))
                    {
                        if (isNew) sw.WriteLine("時間,判定,ピッチ(mm),左穴径(mm),右穴径(mm),角度(度)");
                        sw.WriteLine($"{timeStr},{judgeStr},{resToSave.PitchMm:F2},{resToSave.DiameterLeftMm:F2},{resToSave.DiameterRightMm:F2},{resToSave.AngleDegree:F2}");
                    }

                    if (matToSave != null && !matToSave.IsDisposed)
                    {
                        using (Mat saveMat = new Mat())
                        {
                            double scale = _resizeMode == 1 ? 0.5 : (_resizeMode == 2 ? 0.25 : 1.0);
                            if (scale < 1.0) Cv2.Resize(matToSave, saveMat, new OpenCvSharp.Size(0, 0), scale, scale);
                            Cv2.ImWrite(Path.Combine(folderPath, $"{timeStr}_{judgeStr}.jpg"), scale < 1.0 ? saveMat : matToSave);
                        }
                    }
                }
                catch (Exception ex) { Console.WriteLine("保存エラー: " + ex.Message); }
                finally { if (matToSave != null && !matToSave.IsDisposed) matToSave.Dispose(); }
            });
        }

        private void DeleteOldLogs()
        {
            try { if (!Directory.Exists(_savePath)) return; DateTime threshold = DateTime.Now.AddDays(-_deleteDays); foreach (var d in Directory.GetDirectories(_savePath)) { string fName = Path.GetFileName(d); if (fName.Length == 8 && int.TryParse(fName, out _)) { if (new DateTime(int.Parse(fName.Substring(0, 4)), int.Parse(fName.Substring(4, 2)), int.Parse(fName.Substring(6, 2))) < threshold) Directory.Delete(d, true); } } } catch { }
        }

        private void ExecuteCalibration(NumericUpDown numMm, NumericUpDown numY)
        {
            double px = 0; double cy = 0;
            lock (_dataLock)
            {
                if (_lastResult == null || !_lastResult.IsValid) { MessageBox.Show("有効な測定結果がありません。", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                px = _lastResult.PitchPx; cy = _lastCenterY;
            }

            string input = InputBox("実寸入力", $"検出ピッチ: {px:F2} px\n実際の寸法(mm)を入力:", "100.0");
            if (double.TryParse(input, out double actualMm) && actualMm > 0)
            {
                double newMmPerPx = actualMm / px;
                if (newMmPerPx >= (double)numMm.Minimum && newMmPerPx <= (double)numMm.Maximum) numMm.Value = (decimal)newMmPerPx;
                if (cy >= (double)numY.Minimum && cy <= (double)numY.Maximum) numY.Value = (decimal)cy;
                Settings_ValueChanged(null, null);
                MessageBox.Show($"完了しました。\n\n係数: {newMmPerPx:F5} mm/px", "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void Settings_ValueChanged(object sender, EventArgs e)
        {
            if (_isLoadingConfig) return;
            _savePath = txtSavePath.Text; _saveMode = cmbSaveMode.SelectedIndex; _resizeMode = cmbResizeMode.SelectedIndex; _deleteDays = (int)numDeleteDays.Value;
            _targetPitch = (double)numTargetPitch.Value; _tolPlus = (double)numTolPlus.Value; _tolMinus = (double)numTolMinus.Value; _tolAngle = (double)numTolAngle.Value;
            _processIntervalMs = (int)numProcessInterval.Value; _updateIntervalMs = (int)numUpdateInterval.Value;
            _mmPerPixelTop = (double)numMmTop.Value; _refYTop = (double)numYTop.Value; _mmPerPixelMid = (double)numMmMid.Value; _refYMid = (double)numYMid.Value; _mmPerPixelBot = (double)numMmBot.Value; _refYBot = (double)numYBot.Value;
            _roiX = (int)numRoiX.Value; _roiY = (int)numRoiY.Value; _roiWidth = (int)numRoiWidth.Value; _roiHeight = (int)numRoiHeight.Value;
        }

        private void SaveConfig()
        {
            try { string path = Path.Combine(Application.StartupPath, "config.txt"); using (StreamWriter sw = new StreamWriter(path, false, Encoding.UTF8)) { sw.WriteLine($"SavePath={_savePath}\nSaveMode={_saveMode}\nResizeMode={_resizeMode}\nDeleteDays={_deleteDays}"); sw.WriteLine($"TargetPitch={_targetPitch}\nTolPlus={_tolPlus}\nTolMinus={_tolMinus}\nTolAngle={_tolAngle}\nProcessInterval={_processIntervalMs}\nUpdateInterval={_updateIntervalMs}"); sw.WriteLine($"MmPerPixelTop={_mmPerPixelTop}\nRefYTop={_refYTop}\nMmPerPixelMid={_mmPerPixelMid}\nRefYMid={_refYMid}\nMmPerPixelBot={_mmPerPixelBot}\nRefYBot={_refYBot}"); sw.WriteLine($"RoiX={_roiX}\nRoiY={_roiY}\nRoiWidth={_roiWidth}\nRoiHeight={_roiHeight}"); } } catch { }
        }

        private void LoadConfig()
        {
            _isLoadingConfig = true;
            try { string path = Path.Combine(Application.StartupPath, "config.txt"); if (File.Exists(path)) { foreach (string line in File.ReadAllLines(path)) { var parts = line.Split('='); if (parts.Length < 2) continue; string key = parts[0].Trim(), val = parts[1].Trim(); if (key == "SavePath") txtSavePath.Text = val; if (key == "SaveMode" && int.TryParse(val, out int i0)) cmbSaveMode.SelectedIndex = i0; if (key == "ResizeMode" && int.TryParse(val, out int i1)) cmbResizeMode.SelectedIndex = i1; if (key == "DeleteDays" && int.TryParse(val, out int i2)) numDeleteDays.Value = i2; if (key == "TargetPitch" && double.TryParse(val, out double d2)) numTargetPitch.Value = (decimal)d2; if (key == "TolPlus" && double.TryParse(val, out double d3)) numTolPlus.Value = (decimal)d3; if (key == "TolMinus" && double.TryParse(val, out double d4)) numTolMinus.Value = (decimal)d4; if (key == "TolAngle" && double.TryParse(val, out double d5)) numTolAngle.Value = (decimal)d5; if (key == "ProcessInterval" && int.TryParse(val, out int i6)) numProcessInterval.Value = i6; if (key == "UpdateInterval" && int.TryParse(val, out int i3)) numUpdateInterval.Value = i3; if (key == "MmPerPixelTop" && double.TryParse(val, out double mt)) numMmTop.Value = (decimal)mt; if (key == "RefYTop" && double.TryParse(val, out double yt)) numYTop.Value = (decimal)yt; if (key == "MmPerPixelMid" && double.TryParse(val, out double mm)) numMmMid.Value = (decimal)mm; if (key == "RefYMid" && double.TryParse(val, out double ym)) numYMid.Value = (decimal)ym; if (key == "MmPerPixelBot" && double.TryParse(val, out double mb)) numMmBot.Value = (decimal)mb; if (key == "RefYBot" && double.TryParse(val, out double yb)) numYBot.Value = (decimal)yb; if (key == "RoiX" && int.TryParse(val, out int r1)) { numRoiX.Value = r1; if (r1 <= trackBarRoiX.Maximum) trackBarRoiX.Value = r1; } if (key == "RoiY" && int.TryParse(val, out int r2)) { numRoiY.Value = r2; if (r2 <= trackBarRoiY.Maximum) trackBarRoiY.Value = r2; } if (key == "RoiWidth" && int.TryParse(val, out int r3)) numRoiWidth.Value = r3; if (key == "RoiHeight" && int.TryParse(val, out int r4)) numRoiHeight.Value = r4; } } } catch { } finally { _isLoadingConfig = false; Settings_ValueChanged(null, null); }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveConfig(); camera?.Terminate();
            if (_lastDispMat != null && !_lastDispMat.IsDisposed) _lastDispMat.Dispose();
            if (_currentDisplayBmp != null) _currentDisplayBmp.Dispose();
        }

        // ==========================================
        // 5. 画像処理メイン: カメラからの映像受信
        // ==========================================
        private void Camera_OnFrameCaptured(object sender, Mat frame)
        {
            try
            {
                if (!_isCapturing || frame == null || frame.IsDisposed) return;

                if ((DateTime.Now - _lastProcessTime).TotalMilliseconds < _processIntervalMs)
                {
                    return;
                }
                _lastProcessTime = DateTime.Now;

                using (Mat dispMat = new Mat())
                using (Mat frameGray = new Mat())
                {
                    if (frame.Channels() == 3) { frame.CopyTo(dispMat); Cv2.CvtColor(frame, frameGray, ColorConversionCodes.BGR2GRAY); }
                    else { Cv2.CvtColor(frame, dispMat, ColorConversionCodes.GRAY2BGR); frame.CopyTo(frameGray); }

                    var result = MeasurementCore.ProcessFrame(frameGray, dispMat, _roiX, _roiY, _roiWidth, _roiHeight, _mmPerPixelTop, _mmPerPixelMid, _mmPerPixelBot, _refYTop, _refYMid, _refYBot);

                    Bitmap nextBmp = BitmapConverter.ToBitmap(dispMat);
                    lock (_bmpLock)
                    {
                        var oldBmp = _currentDisplayBmp;
                        _currentDisplayBmp = nextBmp;
                        if (oldBmp != null) oldBmp.Dispose();
                    }

                    if (_isFirstFrame) { _isFirstFrame = false; this.BeginInvoke(new Action(ResetZoom)); }
                    else { pictureBox1.BeginInvoke(new Action(() => pictureBox1.Invalidate())); }

                    if (result.HasProduct)
                    {
                        lock (_dataLock)
                        {
                            var oldMat = _lastDispMat;
                            _lastDispMat = dispMat.Clone();
                            if (oldMat != null && !oldMat.IsDisposed) oldMat.Dispose();

                            _lastResult = result;
                            _lastCenterY = (result.CenterLeft.Y + result.CenterRight.Y) / 2.0;
                        }
                    }

                    if ((DateTime.Now - _lastUiUpdate).TotalMilliseconds >= _updateIntervalMs)
                    {
                        _lastUiUpdate = DateTime.Now;

                        this.BeginInvoke(new Action(() =>
                        {
                            // ★ ソフトウェアトリガー（HasProduct）によるWAIT表示の切り替え
                            if (!result.HasProduct)
                            {
                                lblPitch.Text = "ピッチ: --- mm"; lblDiameterL.Text = "左穴径: --- mm"; lblDiameterR.Text = "右穴径: --- mm"; lblAngle.Text = "角度: --- 度";
                                lblBigResult.Text = "WAIT"; lblBigResult.BackColor = Color.Gray;
                            }
                            else if (result.IsValid)
                            {
                                double pMm = result.PitchMm;
                                double dL = result.DiameterLeftMm;
                                double dR = result.DiameterRightMm;
                                double ang = result.AngleDegree;

                                lblPitch.Text = $"ピッチ: {pMm:F2} mm"; lblDiameterL.Text = $"左穴径: {dL:F2} mm"; lblDiameterR.Text = $"右穴径: {dR:F2} mm"; lblAngle.Text = $"角度: {ang:F2} 度";

                                bool isPitchOk = (pMm >= _targetPitch - _tolMinus) && (pMm <= _targetPitch + _tolPlus);
                                bool isAngleOk = Math.Abs(ang) <= _tolAngle;
                                bool isAllOk = isPitchOk && isAngleOk;

                                lblBigResult.Text = isAllOk ? "OK" : "NG"; lblBigResult.BackColor = isAllOk ? Color.LimeGreen : Color.Red;
                            }
                            else
                            {
                                lblPitch.Text = "ピッチ: --- mm"; lblDiameterL.Text = "左穴径: --- mm"; lblDiameterR.Text = "右穴径: --- mm"; lblAngle.Text = "角度: --- 度";
                                lblBigResult.Text = "NG"; lblBigResult.BackColor = Color.Red;
                            }
                        }));
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine("処理エラー: " + ex.Message); }
            finally { if (frame != null && !frame.IsDisposed) frame.Dispose(); }
        }
    }
}