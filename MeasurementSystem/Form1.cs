using System;
using System.Drawing;
using System.Windows.Forms;
using System.IO;
using System.Text;
using OpenCvSharp;
using OpenCvSharp.Extensions;

using DrawingPoint = System.Drawing.Point;
using DrawingSize = System.Drawing.Size;

namespace MeasurementSystem
{
    public partial class Form1 : Form
    {
        private ICamera camera;
        private MeasurementEngine engine = new MeasurementEngine();

        private PictureBox picMain;
        private TabControl mainTabControl;
        private TabPage tabPageMain, tabPageSettings;

        // UIコントロール
        private Label lblBigResult, lblPitch, lblLeftDia, lblRightDia;
        private CheckBox chkShowBinary;
        private CheckBox chkShowBinarySettings;

        private NumericUpDown numTarget, numTol, numThreshold;
        private NumericUpDown numRoiX, numRoiY, numRoiW, numRoiH;
        private Label lblConfigStatus;

        // 校正データ
        private double _mmPerPixelTop = 0.176, _mmPerPixelMid = 0.176, _mmPerPixelBot = 0.176;
        private double _refYTop = 400.0, _refYMid = 1000.0, _refYBot = 1600.0;
        private double _lastPx = 0, _lastCenterY = 1000;

        // カメラスレッドがUIを介さずに安全に読み出すためのパラメータキャッシュ
        private int _cacheRoiX, _cacheRoiY, _cacheRoiW, _cacheRoiH, _cacheThreshold;
        private double _cacheTarget, _cacheTol;
        private bool _cacheShowBin;
        private readonly object _paramLock = new object();

        private const string AdminPassword = "admin";

        public Form1()
        {
            InitializeComponentLayout();
            LoadAppConfig();
            UpdateParamCache();

            camera = new TeliCamera();

            // 【修正】画面のUI生成（Handle作成）が完全に終わってからカメラを起動する
            this.Shown += (s, e) => {
                if (camera.Initialize())
                {
                    camera.OnFrameCaptured += Camera_OnFrameCaptured;
                    camera.StartCapture();
                }
            };

            // 終了時にカメラとエンジンを安全に解放
            this.FormClosing += (s, e) => {
                SaveAppConfig();
                camera.Terminate();
                engine?.Dispose();
            };
        }

        private void InitializeComponentLayout()
        {
            this.Text = "FA PanchMetal Inspection System";
            this.WindowState = FormWindowState.Maximized;
            this.BackColor = Color.FromArgb(238, 238, 238);

            picMain = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
            this.Controls.Add(picMain);

            mainTabControl = new TabControl { Dock = DockStyle.Right, Width = 380, Appearance = TabAppearance.FlatButtons, ItemSize = new DrawingSize(0, 1), SizeMode = TabSizeMode.Fixed };
            this.Controls.Add(mainTabControl);

            // ◆ メイン画面
            tabPageMain = new TabPage { Text = "Main", BackColor = Color.FromArgb(240, 240, 240) };
            mainTabControl.TabPages.Add(tabPageMain);

            int opY = 15;
            lblBigResult = new Label { Text = "READY", Location = new DrawingPoint(15, opY), Size = new DrawingSize(340, 100), BackColor = Color.Gray, ForeColor = Color.White, Font = new Font("メイリオ", 42, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter };
            tabPageMain.Controls.Add(lblBigResult); opY += 120;

            lblPitch = new Label { Text = "ピッチ: --- mm", Location = new DrawingPoint(15, opY), AutoSize = true, Font = new Font("メイリオ", 18, FontStyle.Bold), ForeColor = Color.Black }; tabPageMain.Controls.Add(lblPitch); opY += 45;
            lblLeftDia = new Label { Text = "左穴径: --- mm", Location = new DrawingPoint(15, opY), AutoSize = true, Font = new Font("メイリオ", 18, FontStyle.Bold), ForeColor = Color.Black }; tabPageMain.Controls.Add(lblLeftDia); opY += 45;
            lblRightDia = new Label { Text = "右穴径: --- mm", Location = new DrawingPoint(15, opY), AutoSize = true, Font = new Font("メイリオ", 18, FontStyle.Bold), ForeColor = Color.Black }; tabPageMain.Controls.Add(lblRightDia); opY += 70;

            chkShowBinary = new CheckBox { Text = "二値化プレビュー表示", Location = new DrawingPoint(15, opY), AutoSize = true, Font = new Font("メイリオ", 11, FontStyle.Bold), ForeColor = Color.DarkBlue };
            tabPageMain.Controls.Add(chkShowBinary); opY += 60;

            Button btnGoAdmin = new Button { Text = "管理者設定メニュー ⚙", Location = new DrawingPoint(15, opY), Size = new DrawingSize(340, 50), BackColor = Color.FromArgb(70, 70, 74), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 12, FontStyle.Bold) };
            btnGoAdmin.Click += btnGoAdmin_Click;
            tabPageMain.Controls.Add(btnGoAdmin);

            // ◆ 設定画面
            tabPageSettings = new TabPage { Text = "Settings", BackColor = Color.FromArgb(45, 45, 48) };

            int setY = 15;
            AddLabel(tabPageSettings, "■ 計測・公差設定", setY, true); setY += 25;
            numTarget = AddNum(tabPageSettings, "目標ピッチ (mm):", setY, 175.00m, 2, 0.1m, 1000m); setY += 40;
            numTol = AddNum(tabPageSettings, "許容公差 (±mm):", setY, 0.30m, 2, 0.01m, 10m); setY += 55;

            AddLabel(tabPageSettings, "■ 画像処理パラメータ", setY, true); setY += 25;
            numThreshold = AddNum(tabPageSettings, "二値化閾値 (0-255):", setY, 128m, 0, 1m, 255m); setY += 35;

            chkShowBinarySettings = new CheckBox { Text = "二値化プレビュー表示", Location = new DrawingPoint(20, setY), AutoSize = true, Font = new Font("メイリオ", 10, FontStyle.Bold), ForeColor = Color.LightSkyBlue };
            tabPageSettings.Controls.Add(chkShowBinarySettings); setY += 35;

            AddLabel(tabPageSettings, "■ 検査枠 (ROI) 精密設定 (pixel)", setY, true); setY += 25;
            numRoiX = AddNum(tabPageSettings, "開始座標 X:", setY, 60m, 0, 10m, 5000m); setY += 40;
            numRoiY = AddNum(tabPageSettings, "開始座標 Y:", setY, 390m, 0, 10m, 5000m); setY += 40;
            numRoiW = AddNum(tabPageSettings, "枠 横幅 (Width):", setY, 2330m, 0, 10m, 5000m); setY += 40;
            numRoiH = AddNum(tabPageSettings, "枠 高さ (Height):", setY, 1450m, 0, 10m, 5000m); setY += 55;

            lblConfigStatus = new Label { Location = new DrawingPoint(15, setY), Size = new DrawingSize(340, 130), Font = new Font("Consolas", 10), BackColor = Color.Black, ForeColor = Color.Lime, Text = "【リアルタイム計測状態】\n待機中..." };
            tabPageSettings.Controls.Add(lblConfigStatus); setY += 140;

            Button btnCalib = new Button { Text = "現在の位置でキャリブレーション実行", Location = new DrawingPoint(15, setY), Size = new DrawingSize(340, 40), BackColor = Color.DarkOrange, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("メイリオ", 10, FontStyle.Bold) };
            btnCalib.Click += (s, e) => ExecuteCalib();
            tabPageSettings.Controls.Add(btnCalib); setY += 60;

            Button btnCloseAdmin = new Button { Text = "◀ 設定を確定して検査に戻る", Location = new DrawingPoint(15, setY), Size = new DrawingSize(340, 50), BackColor = Color.FromArgb(16, 124, 65), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("メイリオ", 11, FontStyle.Bold) };
            btnCloseAdmin.Click += btnCloseAdmin_Click;
            tabPageSettings.Controls.Add(btnCloseAdmin);

            // UI変更時にキャッシュへ反映
            numTarget.ValueChanged += (s, e) => UpdateParamCache();
            numTol.ValueChanged += (s, e) => UpdateParamCache();
            numThreshold.ValueChanged += (s, e) => UpdateParamCache();
            numRoiX.ValueChanged += (s, e) => UpdateParamCache();
            numRoiY.ValueChanged += (s, e) => UpdateParamCache();
            numRoiW.ValueChanged += (s, e) => UpdateParamCache();
            numRoiH.ValueChanged += (s, e) => UpdateParamCache();
            chkShowBinary.CheckedChanged += (s, e) => UpdateParamCache();
            chkShowBinarySettings.CheckedChanged += (s, e) => UpdateParamCache();
        }

        private void UpdateParamCache()
        {
            lock (_paramLock)
            {
                _cacheRoiX = (int)numRoiX.Value;
                _cacheRoiY = (int)numRoiY.Value;
                _cacheRoiW = (int)numRoiW.Value;
                _cacheRoiH = (int)numRoiH.Value;
                _cacheThreshold = (int)numThreshold.Value;
                _cacheTarget = (double)numTarget.Value;
                _cacheTol = (double)numTol.Value;
                _cacheShowBin = chkShowBinary.Checked || chkShowBinarySettings.Checked;
            }
        }

        private void Camera_OnFrameCaptured(object sender, Mat frame)
        {
            // 空フレームによるクラッシュ防止
            if (frame == null || frame.Empty()) return;

            try
            {
                int rX, rY, rW, rH, thresh;
                double target, tol;
                bool showBin;

                // キャッシュから高速読込 (UIスレッドをブロックしない)
                lock (_paramLock)
                {
                    rX = _cacheRoiX; rY = _cacheRoiY;
                    rW = _cacheRoiW; rH = _cacheRoiH;
                    thresh = _cacheThreshold;
                    target = _cacheTarget;
                    tol = _cacheTol;
                    showBin = _cacheShowBin;
                }

                using (Mat drawMat = new Mat())
                {
                    var res = engine.ProcessFrame(frame, drawMat, rX, rY, rW, rH,
                        _mmPerPixelTop, _mmPerPixelMid, _mmPerPixelBot,
                        _refYTop, _refYMid, _refYBot,
                        thresh, showBin);

                    if (res.IsValid)
                    {
                        bool isOk = Math.Abs(res.MeasuredValueMm - target) <= tol;
                        _lastPx = res.MeasuredValuePx;
                        _lastCenterY = res.CenterY;

                        this.BeginInvoke(new Action(() => {
                            lblBigResult.Text = isOk ? "OK" : "NG";
                            lblBigResult.BackColor = isOk ? Color.LimeGreen : Color.Crimson;
                            lblPitch.Text = $"ピッチ: {res.MeasuredValueMm:F2} mm";
                            lblLeftDia.Text = $"左穴径: {res.LeftHoleDiaMm:F2} mm";
                            lblRightDia.Text = $"右穴径: {res.RightHoleDiaMm:F2} mm";
                            lblConfigStatus.Text = $"【リアルタイム計測状態】\nピッチ: {res.MeasuredValueMm:F3} mm\n左穴: {res.LeftHoleDiaMm:F2} mm / 右穴: {res.RightHoleDiaMm:F2} mm\nPixel距離: {res.MeasuredValuePx:F1} px\nY座標: {res.CenterY:F1}";
                        }));
                    }
                    else
                    {
                        this.BeginInvoke(new Action(() => {
                            lblBigResult.Text = "NG";
                            lblBigResult.BackColor = Color.Crimson;
                            lblPitch.Text = "ピッチ: 検出エラー";
                            lblLeftDia.Text = "左穴径: --- mm";
                            lblRightDia.Text = "右穴径: --- mm";
                            lblConfigStatus.Text = "【リアルタイム計測状態】\n検出エラー：穴が見つかりません\n(閾値やROIを調整してください)";
                        }));
                    }

                    // 画像更新（古いBitmapの破棄を確実に行う）
                    Bitmap nextBmp = BitmapConverter.ToBitmap(drawMat);
                    if (picMain.IsHandleCreated)
                    {
                        picMain.BeginInvoke(new Action(() => {
                            var oldBmp = picMain.Image;
                            picMain.Image = nextBmp;
                            oldBmp?.Dispose();
                        }));
                    }
                    else
                    {
                        nextBmp.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                // ここでエラーが出た場合はVisual Studioの「出力」タブに原因が表示されます
                System.Diagnostics.Debug.WriteLine($"【画像処理エラー】: {ex.Message}");
            }
            finally
            {
                frame?.Dispose();
            }
        }

        private void ExecuteCalib()
        {
            if (_lastPx <= 0) { MessageBox.Show("計測データがありません。"); return; }
            string targetStr = InputBox("校正", "3点のうち、どこの校正を行いますか？\n(0:上部, 1:中央, 2:下部)", "1");
            if (!int.TryParse(targetStr, out int type) || type < 0 || type > 2) return;

            string v = InputBox("校正", $"現在のY座標({_lastCenterY:F0}px)での実寸(mm):", "");
            if (double.TryParse(v, out double mm) && mm > 0)
            {
                double ratio = mm / _lastPx;
                if (type == 0) { _mmPerPixelTop = ratio; _refYTop = _lastCenterY; }
                else if (type == 1) { _mmPerPixelMid = ratio; _refYMid = _lastCenterY; }
                else if (type == 2) { _mmPerPixelBot = ratio; _refYBot = _lastCenterY; }

                SaveAppConfig();
                MessageBox.Show($"校正値を更新しました。\n係数: {ratio:F6}");
            }
        }

        private void btnGoAdmin_Click(object sender, EventArgs e)
        {
            string pass = InputBox("認証", "管理者パスワードを入力してください:", "");
            if (pass == AdminPassword)
            {
                if (!mainTabControl.TabPages.Contains(tabPageSettings)) mainTabControl.TabPages.Add(tabPageSettings);
                mainTabControl.SelectedTab = tabPageSettings;
            }
            else if (!string.IsNullOrEmpty(pass)) { MessageBox.Show("パスワードが一致しません。"); }
        }

        private void btnCloseAdmin_Click(object sender, EventArgs e)
        {
            SaveAppConfig();
            mainTabControl.SelectedTab = tabPageMain;
            if (mainTabControl.TabPages.Contains(tabPageSettings)) mainTabControl.TabPages.Remove(tabPageSettings);
        }

        private void SaveAppConfig()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt");
                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"{numTarget.Value},{numTol.Value},{numThreshold.Value}");
                sb.AppendLine($"{numRoiX.Value},{numRoiY.Value},{numRoiW.Value},{numRoiH.Value}");
                sb.AppendLine($"{_mmPerPixelTop},{_mmPerPixelMid},{_mmPerPixelBot},{_refYTop},{_refYMid},{_refYBot}");
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
                    if (lines.Length >= 3)
                    {
                        var p1 = lines[0].Split(',');
                        numTarget.Value = decimal.Parse(p1[0]); numTol.Value = decimal.Parse(p1[1]); numThreshold.Value = decimal.Parse(p1[2]);

                        var p2 = lines[1].Split(',');
                        numRoiX.Value = decimal.Parse(p2[0]); numRoiY.Value = decimal.Parse(p2[1]); numRoiW.Value = decimal.Parse(p2[2]); numRoiH.Value = decimal.Parse(p2[3]);

                        var p3 = lines[2].Split(',');
                        _mmPerPixelTop = double.Parse(p3[0]); _mmPerPixelMid = double.Parse(p3[1]); _mmPerPixelBot = double.Parse(p3[2]);
                        _refYTop = double.Parse(p3[3]); _refYMid = double.Parse(p3[4]); _refYBot = double.Parse(p3[5]);
                    }
                }
            }
            catch { }
        }

        private void AddLabel(TabPage p, string text, int y, bool isBold)
        {
            Label l = new Label { Text = text, Location = new DrawingPoint(15, y), AutoSize = true, ForeColor = Color.White };
            if (isBold) l.Font = new Font("メイリオ", 9, FontStyle.Bold);
            p.Controls.Add(l);
        }

        private NumericUpDown AddNum(TabPage p, string text, int y, decimal val, int dec, decimal inc, decimal max)
        {
            Label lbl = new Label { Text = text, Location = new DrawingPoint(20, y + 2), AutoSize = true, ForeColor = Color.LightGray, Font = new Font("Segoe UI", 9) };
            p.Controls.Add(lbl);

            var n = new NumericUpDown();
            n.Location = new DrawingPoint(170, y);
            n.Width = 140;
            n.DecimalPlaces = dec;
            n.Increment = inc;
            n.Minimum = 0;
            n.Maximum = max;
            n.Value = val;

            p.Controls.Add(n);
            return n;
        }

        public static string InputBox(string title, string prompt, string defaultVal)
        {
            Form f = new Form { Text = title, Width = 350, Height = 150, StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
            Label l = new Label { Text = prompt, Left = 20, Top = 15, Width = 300, AutoSize = true };
            TextBox tx = new TextBox { Text = defaultVal, Left = 20, Top = 40, Width = 290, PasswordChar = '*' };
            Button b = new Button { Text = "OK", Left = 230, Top = 75, Width = 80, DialogResult = DialogResult.OK };
            f.Controls.AddRange(new Control[] { l, tx, b }); f.AcceptButton = b;
            return f.ShowDialog() == DialogResult.OK ? tx.Text : "";
        }
    }
}