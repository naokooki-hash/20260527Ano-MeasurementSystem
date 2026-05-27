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

        // 保存用の一時保持変数
        private Mat _lastDispMat = null;
        private MeasurementResult _lastResult = null;
        private double _lastCenterY = 1024;

        // --- 新機能：保存・管理用変数 ---
        private string _savePath = @"C:\InspectionData";
        private int _saveMode = 1;      // 0:全て保存, 1:NGのみ, 2:保存しない
        private int _resizeMode = 0;    // 0:100%, 1:50%, 2:25%
        private int _deleteDays = 30;   // 自動削除の日数
        private bool _isAdmin = false;  // 管理者ロック状態
        private const string ADMIN_PASS = "admin"; // 管理者パスワード

        // ROI初期値
        private int _roiX = 427, _roiY = 703, _roiWidth = 1320, _roiHeight = 800;

        // 3段構えキャリブレーションパラメータ
        private double _mmPerPixelTop = 0.176, _mmPerPixelMid = 0.176, _mmPerPixelBot = 0.176;
        private double _refYTop = 400.0, _refYMid = 1024.0, _refYBot = 1600.0;

        // 判定・更新パラメータ
        private double _targetPitch = 100.0, _tolPlus = 0.5, _tolMinus = 0.5;
        private int _updateIntervalMs = 500;
        private DateTime _lastUiUpdate = DateTime.MinValue;

        // UIコントロール
        private PictureBox pictureBox1;
        private Label lblBigResult, lblPitch, lblDiameterL, lblDiameterR;
        private TabControl mainTabControl;
        private TabPage tabPageMain, tabPageSettings;

        // 設定用コントロール
        private Panel pnlAdminControls;
        private Button btnAdminLock;
        private TextBox txtSavePath;
        private ComboBox cmbSaveMode, cmbResizeMode;
        private NumericUpDown numDeleteDays;
        private NumericUpDown numMmTop, numMmMid, numMmBot, numYTop, numYMid, numYBot;
        private TrackBar trackBarRoiX, trackBarRoiY;
        private NumericUpDown numRoiX, numRoiY, numRoiWidth, numRoiHeight;
        private NumericUpDown numTargetPitch, numTolPlus, numTolMinus, numUpdateInterval;

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
                    MessageBox.Show("カメラの初期化に失敗しました。USB接続等を確認してください。", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
        }

        // ==========================================
        // 2. UIの動的生成
        // ==========================================
        private void InitializeCustomUI()
        {
            this.Text = "光学検査システム (第1段階: 保存・ロック機能)";
            this.Size = new Size(1500, 950);
            this.FormClosing += Form1_FormClosing;

            pictureBox1 = new PictureBox { Location = new Point(10, 10), Size = new Size(1100, 850), BorderStyle = BorderStyle.FixedSingle, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
            this.Controls.Add(pictureBox1);

            mainTabControl = new TabControl { Location = new Point(1120, 10), Size = new Size(350, 850) };
            this.Controls.Add(mainTabControl);

            tabPageMain = new TabPage("メイン");
            tabPageSettings = new TabPage("設定");
            mainTabControl.TabPages.Add(tabPageMain);
            mainTabControl.TabPages.Add(tabPageSettings);

            // --- メインタブ ---
            lblBigResult = new Label { Text = "WAIT", Font = new Font("Segoe UI", 56, FontStyle.Bold), Location = new Point(10, 15), Size = new Size(320, 100), TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Gray, ForeColor = Color.White };
            tabPageMain.Controls.Add(lblBigResult);

            Font resultFont = new Font("Segoe UI", 20, FontStyle.Bold);
            lblPitch = new Label { Location = new Point(10, 140), Size = new Size(320, 45), Font = resultFont };
            lblDiameterL = new Label { Location = new Point(10, 195), Size = new Size(320, 45), Font = resultFont };
            lblDiameterR = new Label { Location = new Point(10, 250), Size = new Size(320, 45), Font = resultFont };
            tabPageMain.Controls.Add(lblPitch); tabPageMain.Controls.Add(lblDiameterL); tabPageMain.Controls.Add(lblDiameterR);

            Button btnStart = new Button { Text = "Start", Location = new Point(10, 310), Size = new Size(150, 50), Font = new Font("Segoe UI", 12, FontStyle.Bold), BackColor = Color.LightBlue };
            btnStart.Click += (s, e) => { _isCapturing = true; camera?.StartCapture(); };
            tabPageMain.Controls.Add(btnStart);

            Button btnStop = new Button { Text = "Stop", Location = new Point(170, 310), Size = new Size(150, 50), Font = new Font("Segoe UI", 12, FontStyle.Bold), BackColor = Color.LightCoral };
            btnStop.Click += (s, e) => { _isCapturing = false; camera?.StopCapture(); };
            tabPageMain.Controls.Add(btnStop);

            Button btnRecord = new Button { Text = "保存 (Spaceキー)", Location = new Point(10, 380), Size = new Size(310, 60), Font = new Font("Segoe UI", 16, FontStyle.Bold), BackColor = Color.LightGreen };
            btnRecord.Click += (s, e) => SaveMeasurementData();
            tabPageMain.Controls.Add(btnRecord);

            // --- 設定タブ ---
            btnAdminLock = new Button { Text = "管理者ロック解除", Location = new Point(10, 10), Size = new Size(315, 40), Font = new Font("Segoe UI", 12, FontStyle.Bold), BackColor = Color.Orange };
            btnAdminLock.Click += BtnAdminLock_Click;
            tabPageSettings.Controls.Add(btnAdminLock);

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
            numUpdateInterval = AddNum(pnlAdminControls, "数値更新間隔 (ms):", ref yPos, _updateIntervalMs, 0, 100m);

            yPos += 10;
            AddLabel(pnlAdminControls, "■ キャリブレーション", yPos, true); yPos += 25;
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

            yPos += 10;
            AddLabel(pnlAdminControls, "■ 視野設定 (ROI)", yPos, true); yPos += 25;
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

            txtSavePath.TextChanged += Settings_ValueChanged;
            cmbSaveMode.SelectedIndexChanged += Settings_ValueChanged;
            cmbResizeMode.SelectedIndexChanged += Settings_ValueChanged;
        }

        // --- ヘルパー関数 ---
        private void AddLabel(Panel p, string t, int y, bool b = false) { Label l = new Label { Text = t, Location = new Point(10, y), AutoSize = true }; if (b) l.Font = new Font("Segoe UI", 10, FontStyle.Bold); p.Controls.Add(l); }
        private NumericUpDown AddNum(Panel p, string t, ref int y, decimal v, int d, decimal i) { AddLabel(p, t, y); var n = new NumericUpDown { Location = new Point(15, y + 20), Width = 280, DecimalPlaces = d, Increment = i, Maximum = 10000, Value = v }; n.ValueChanged += Settings_ValueChanged; p.Controls.Add(n); y += 45; return n; }
        private ComboBox AddCombo(Panel p, string t, ref int y, string[] items, int selIdx) { AddLabel(p, t, y); ComboBox c = new ComboBox { Location = new Point(15, y + 20), Width = 280, DropDownStyle = ComboBoxStyle.DropDownList }; c.Items.AddRange(items); c.SelectedIndex = selIdx; p.Controls.Add(c); y += 45; return c; }

        // ★修正済みの InputBox (SetBoundsエラー対応)
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
        // 3. 管理者ロック・保存・自動削除ロジック
        // ==========================================
        private void BtnAdminLock_Click(object sender, EventArgs e)
        {
            if (_isAdmin)
            {
                _isAdmin = false;
                pnlAdminControls.Enabled = false;
                btnAdminLock.Text = "管理者ロック解除";
                btnAdminLock.BackColor = Color.Orange;
            }
            else
            {
                string pass = InputBox("管理者ログイン", "パスワードを入力してください:", "", true);
                if (pass == ADMIN_PASS)
                {
                    _isAdmin = true;
                    pnlAdminControls.Enabled = true;
                    btnAdminLock.Text = "ロック中 (クリックで施錠)";
                    btnAdminLock.BackColor = Color.LimeGreen;
                }
                else if (!string.IsNullOrEmpty(pass))
                {
                    MessageBox.Show("パスワードが違います。", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Space)
            {
                SaveMeasurementData();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void SaveMeasurementData()
        {
            if (_lastResult == null || !_lastResult.IsValid) return;

            bool isOk = (_lastResult.PitchMm >= _targetPitch - _tolMinus) && (_lastResult.PitchMm <= _targetPitch + _tolPlus);
            if (_saveMode == 2) return;
            if (_saveMode == 1 && isOk) return;

            Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(_savePath)) Directory.CreateDirectory(_savePath);
                    string dateStr = DateTime.Now.ToString("yyyyMMdd");
                    string folderPath = Path.Combine(_savePath, dateStr);
                    if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);

                    string timeStr = DateTime.Now.ToString("HHmmss");
                    string judgeStr = isOk ? "OK" : "NG";

                    string csvPath = Path.Combine(folderPath, "InspectionLog.csv");
                    bool isNew = !File.Exists(csvPath);
                    using (StreamWriter sw = new StreamWriter(csvPath, true, Encoding.UTF8))
                    {
                        if (isNew) sw.WriteLine("時間,判定,ピッチ(mm),左穴径(mm),右穴径(mm)");
                        sw.WriteLine($"{timeStr},{judgeStr},{_lastResult.PitchMm:F2},{_lastResult.DiameterLeftMm:F2},{_lastResult.DiameterRightMm:F2}");
                    }

                    if (_lastDispMat != null && !_lastDispMat.IsDisposed)
                    {
                        string imgPath = Path.Combine(folderPath, $"{timeStr}_{judgeStr}.jpg");
                        using (Mat saveMat = new Mat())
                        {
                            double scale = 1.0;
                            if (_resizeMode == 1) scale = 0.5;
                            else if (_resizeMode == 2) scale = 0.25;

                            if (scale < 1.0)
                            {
                                Cv2.Resize(_lastDispMat, saveMat, new OpenCvSharp.Size(0, 0), scale, scale);
                                Cv2.ImWrite(imgPath, saveMat);
                            }
                            else
                            {
                                Cv2.ImWrite(imgPath, _lastDispMat);
                            }
                        }
                    }
                }
                catch (Exception ex) { Console.WriteLine("保存エラー: " + ex.Message); }
            });
        }

        private void DeleteOldLogs()
        {
            try
            {
                if (!Directory.Exists(_savePath)) return;
                var dirs = Directory.GetDirectories(_savePath);
                DateTime threshold = DateTime.Now.AddDays(-_deleteDays);

                foreach (var d in dirs)
                {
                    string folderName = Path.GetFileName(d);
                    if (folderName.Length == 8 && int.TryParse(folderName, out _))
                    {
                        DateTime folderDate = new DateTime(int.Parse(folderName.Substring(0, 4)), int.Parse(folderName.Substring(4, 2)), int.Parse(folderName.Substring(6, 2)));
                        if (folderDate < threshold) Directory.Delete(d, true);
                    }
                }
            }
            catch { }
        }

        private void ExecuteCalibration(NumericUpDown numMm, NumericUpDown numY)
        {
            if (_lastResult == null || !_lastResult.IsValid) { MessageBox.Show("有効な測定結果がありません。", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            string input = InputBox("実寸入力", $"検出されたピッチ幅: {_lastResult.PitchPx:F2} px\n実際の寸法(mm)を入力してください:", "100.0");
            if (double.TryParse(input, out double actualMm) && actualMm > 0)
            {
                double newMmPerPx = actualMm / _lastResult.PitchPx;
                if (newMmPerPx >= (double)numMm.Minimum && newMmPerPx <= (double)numMm.Maximum) numMm.Value = (decimal)newMmPerPx;
                if (_lastCenterY >= (double)numY.Minimum && _lastCenterY <= (double)numY.Maximum) numY.Value = (decimal)_lastCenterY;
                Settings_ValueChanged(null, null);
                MessageBox.Show($"完了しました。\n\n係数: {newMmPerPx:F5} mm/px\n基準Y: {_lastCenterY:F1}", "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        // ==========================================
        // 4. 設定の保存と読み込み
        // ==========================================
        private void Settings_ValueChanged(object sender, EventArgs e)
        {
            if (_isLoadingConfig) return;
            _savePath = txtSavePath.Text; _saveMode = cmbSaveMode.SelectedIndex; _resizeMode = cmbResizeMode.SelectedIndex; _deleteDays = (int)numDeleteDays.Value;
            _targetPitch = (double)numTargetPitch.Value; _tolPlus = (double)numTolPlus.Value; _tolMinus = (double)numTolMinus.Value; _updateIntervalMs = (int)numUpdateInterval.Value;
            _mmPerPixelTop = (double)numMmTop.Value; _refYTop = (double)numYTop.Value; _mmPerPixelMid = (double)numMmMid.Value; _refYMid = (double)numYMid.Value; _mmPerPixelBot = (double)numMmBot.Value; _refYBot = (double)numYBot.Value;
            _roiX = (int)numRoiX.Value; _roiY = (int)numRoiY.Value; _roiWidth = (int)numRoiWidth.Value; _roiHeight = (int)numRoiHeight.Value;
        }

        private void SaveConfig()
        {
            try
            {
                string path = Path.Combine(Application.StartupPath, "config.txt");
                using (StreamWriter sw = new StreamWriter(path, false, Encoding.UTF8))
                {
                    sw.WriteLine($"SavePath={_savePath}"); sw.WriteLine($"SaveMode={_saveMode}"); sw.WriteLine($"ResizeMode={_resizeMode}"); sw.WriteLine($"DeleteDays={_deleteDays}");
                    sw.WriteLine($"TargetPitch={_targetPitch}"); sw.WriteLine($"TolPlus={_tolPlus}"); sw.WriteLine($"TolMinus={_tolMinus}"); sw.WriteLine($"UpdateInterval={_updateIntervalMs}");
                    sw.WriteLine($"MmPerPixelTop={_mmPerPixelTop}"); sw.WriteLine($"RefYTop={_refYTop}"); sw.WriteLine($"MmPerPixelMid={_mmPerPixelMid}"); sw.WriteLine($"RefYMid={_refYMid}"); sw.WriteLine($"MmPerPixelBot={_mmPerPixelBot}"); sw.WriteLine($"RefYBot={_refYBot}");
                    sw.WriteLine($"RoiX={_roiX}"); sw.WriteLine($"RoiY={_roiY}"); sw.WriteLine($"RoiWidth={_roiWidth}"); sw.WriteLine($"RoiHeight={_roiHeight}");
                }
            }
            catch { }
        }

        private void LoadConfig()
        {
            _isLoadingConfig = true;
            try
            {
                string path = Path.Combine(Application.StartupPath, "config.txt");
                if (File.Exists(path))
                {
                    foreach (string line in File.ReadAllLines(path))
                    {
                        var parts = line.Split('='); if (parts.Length < 2) continue;
                        string key = parts[0].Trim(), val = parts[1].Trim();
                        if (key == "SavePath") txtSavePath.Text = val;
                        if (key == "SaveMode" && int.TryParse(val, out int i0)) cmbSaveMode.SelectedIndex = i0;
                        if (key == "ResizeMode" && int.TryParse(val, out int i1)) cmbResizeMode.SelectedIndex = i1;
                        if (key == "DeleteDays" && int.TryParse(val, out int i2)) numDeleteDays.Value = i2;

                        if (key == "TargetPitch" && double.TryParse(val, out double d2)) numTargetPitch.Value = (decimal)d2;
                        if (key == "TolPlus" && double.TryParse(val, out double d3)) numTolPlus.Value = (decimal)d3;
                        if (key == "TolMinus" && double.TryParse(val, out double d4)) numTolMinus.Value = (decimal)d4;
                        if (key == "UpdateInterval" && int.TryParse(val, out int i3)) numUpdateInterval.Value = i3;

                        if (key == "MmPerPixelTop" && double.TryParse(val, out double mt)) numMmTop.Value = (decimal)mt;
                        if (key == "RefYTop" && double.TryParse(val, out double yt)) numYTop.Value = (decimal)yt;
                        if (key == "MmPerPixelMid" && double.TryParse(val, out double mm)) numMmMid.Value = (decimal)mm;
                        if (key == "RefYMid" && double.TryParse(val, out double ym)) numYMid.Value = (decimal)ym;
                        if (key == "MmPerPixelBot" && double.TryParse(val, out double mb)) numMmBot.Value = (decimal)mb;
                        if (key == "RefYBot" && double.TryParse(val, out double yb)) numYBot.Value = (decimal)yb;

                        if (key == "RoiX" && int.TryParse(val, out int r1)) { numRoiX.Value = r1; if (r1 <= trackBarRoiX.Maximum) trackBarRoiX.Value = r1; }
                        if (key == "RoiY" && int.TryParse(val, out int r2)) { numRoiY.Value = r2; if (r2 <= trackBarRoiY.Maximum) trackBarRoiY.Value = r2; }
                        if (key == "RoiWidth" && int.TryParse(val, out int r3)) numRoiWidth.Value = r3;
                        if (key == "RoiHeight" && int.TryParse(val, out int r4)) numRoiHeight.Value = r4;
                    }
                }
            }
            catch { }
            finally { _isLoadingConfig = false; Settings_ValueChanged(null, null); }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveConfig();
            camera?.Terminate();
            if (_lastDispMat != null && !_lastDispMat.IsDisposed) _lastDispMat.Dispose();
        }

        // ==========================================
        // 5. 画像処理メイン: カメラからの映像受信
        // ==========================================
        // ★修正済み：ROI突入時のメモリ解放(ObjectDisposed)エラー対策
        private void Camera_OnFrameCaptured(object sender, Mat frame)
        {
            try
            {
                if (!_isCapturing || frame == null || frame.IsDisposed) return;
                using (Mat dispMat = new Mat())
                using (Mat frameGray = new Mat())
                {
                    if (frame.Channels() == 3) { frame.CopyTo(dispMat); Cv2.CvtColor(frame, frameGray, ColorConversionCodes.BGR2GRAY); }
                    else { Cv2.CvtColor(frame, dispMat, ColorConversionCodes.GRAY2BGR); frame.CopyTo(frameGray); }

                    var result = MeasurementCore.ProcessFrame(frameGray, dispMat, _roiX, _roiY, _roiWidth, _roiHeight, _mmPerPixelTop, _mmPerPixelMid, _mmPerPixelBot, _refYTop, _refYMid, _refYBot);
                    Bitmap displayBmp = BitmapConverter.ToBitmap(dispMat);

                    // 非同期で描画する前に、保存用の複製を作成しておく
                    Mat cloneForSave = null;
                    if (result.IsValid)
                    {
                        cloneForSave = dispMat.Clone();
                    }

                    this.BeginInvoke(new Action(() =>
                    {
                        var oldImage = pictureBox1.Image;
                        pictureBox1.Image = displayBmp;
                        if (oldImage != null) oldImage.Dispose();

                        // 複製した保存用データを安全にUIスレッドで保持
                        if (result.IsValid && cloneForSave != null)
                        {
                            if (_lastDispMat != null && !_lastDispMat.IsDisposed) _lastDispMat.Dispose();
                            _lastDispMat = cloneForSave;
                            _lastResult = result;
                            _lastCenterY = (result.CenterLeft.Y + result.CenterRight.Y) / 2.0;
                        }

                        if ((DateTime.Now - _lastUiUpdate).TotalMilliseconds >= _updateIntervalMs)
                        {
                            if (result.IsValid)
                            {
                                lblPitch.Text = $"ピッチ: {result.PitchMm:F2} mm"; lblDiameterL.Text = $"左穴径: {result.DiameterLeftMm:F2} mm"; lblDiameterR.Text = $"右穴径: {result.DiameterRightMm:F2} mm";
                                bool isPitchOk = (result.PitchMm >= _targetPitch - _tolMinus) && (result.PitchMm <= _targetPitch + _tolPlus);
                                lblBigResult.Text = isPitchOk ? "OK" : "NG"; lblBigResult.BackColor = isPitchOk ? Color.LimeGreen : Color.Red;
                            }
                            else
                            {
                                lblPitch.Text = "ピッチ: --- mm"; lblDiameterL.Text = "左穴径: --- mm"; lblDiameterR.Text = "右穴径: --- mm";
                                lblBigResult.Text = "NG"; lblBigResult.BackColor = Color.Red;
                            }
                            _lastUiUpdate = DateTime.Now;
                        }
                    }));
                }
            }
            catch (Exception ex) { Console.WriteLine(ex.Message); }
            finally { if (frame != null && !frame.IsDisposed) frame.Dispose(); }
        }
    }
}