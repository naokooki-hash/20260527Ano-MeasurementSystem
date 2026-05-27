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

namespace MeasurementSystem // ※ご自身のプロジェクト名に合わせてください
{
    public partial class Form1 : Form
    {
        // ==========================================
        // 1. 変数定義
        // ==========================================
        private ICamera camera;
        private bool _isCapturing = false;
        private bool _isLoadingConfig = false;

        // キャリブレーション用の直近データ保持
        private double _lastPitchPx = 0;
        private double _lastCenterY = 1024;

        // ROI初期値
        private int _roiX = 427;
        private int _roiY = 703;
        private int _roiWidth = 1320;
        private int _roiHeight = 800;

        // 3段構えキャリブレーションパラメータ
        private double _mmPerPixelTop = 0.176;
        private double _mmPerPixelMid = 0.176;
        private double _mmPerPixelBot = 0.176;
        private double _refYTop = 400.0;
        private double _refYMid = 1024.0;
        private double _refYBot = 1600.0;

        // 判定・更新パラメータ
        private double _targetPitch = 100.0;
        private double _tolPlus = 0.5;
        private double _tolMinus = 0.5;
        private int _updateIntervalMs = 500;
        private DateTime _lastUiUpdate = DateTime.MinValue;

        // UIコントロール
        private TabControl mainTabControl;
        private TabPage tabPageMain, tabPageSettings;
        private PictureBox pictureBox1;
        private Label lblBigResult;
        private Label lblPitch;
        private Label lblDiameterL;
        private Label lblDiameterR;

        // 設定用コントロール
        private NumericUpDown numMmTop, numMmMid, numMmBot;
        private NumericUpDown numYTop, numYMid, numYBot;
        private TrackBar trackBarRoiX, trackBarRoiY;
        private NumericUpDown numRoiX, numRoiY, numRoiWidth, numRoiHeight;
        private NumericUpDown numTargetPitch, numTolPlus, numTolMinus, numUpdateInterval;

        public Form1()
        {
            InitializeCustomUI();
            LoadConfig();

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
            this.Text = "光学検査システム (穴サイズ・ピッチ計測)";
            this.Size = new Size(1500, 950);
            this.FormClosing += Form1_FormClosing;

            pictureBox1 = new PictureBox
            {
                Location = new Point(10, 10),
                Size = new Size(1100, 850),
                BorderStyle = BorderStyle.FixedSingle,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black
            };
            this.Controls.Add(pictureBox1);

            mainTabControl = new TabControl
            {
                Location = new Point(1120, 10),
                Size = new Size(350, 850)
            };
            this.Controls.Add(mainTabControl);

            tabPageMain = new TabPage("メイン");
            tabPageSettings = new TabPage("設定");
            mainTabControl.TabPages.Add(tabPageMain);
            mainTabControl.TabPages.Add(tabPageSettings);

            // --- メインタブの構成 ---
            lblBigResult = new Label
            {
                Text = "WAIT",
                Font = new Font("Segoe UI", 56, FontStyle.Bold),
                Location = new Point(10, 15),
                Size = new Size(320, 100),
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Gray,
                ForeColor = Color.White
            };
            tabPageMain.Controls.Add(lblBigResult);

            Font resultFont = new Font("Segoe UI", 20, FontStyle.Bold);
            lblPitch = new Label { Location = new Point(10, 140), Size = new Size(320, 45), Font = resultFont };
            lblDiameterL = new Label { Location = new Point(10, 195), Size = new Size(320, 45), Font = resultFont };
            lblDiameterR = new Label { Location = new Point(10, 250), Size = new Size(320, 45), Font = resultFont };
            tabPageMain.Controls.Add(lblPitch);
            tabPageMain.Controls.Add(lblDiameterL);
            tabPageMain.Controls.Add(lblDiameterR);

            Button btnStart = new Button { Text = "Start", Location = new Point(10, 330), Size = new Size(150, 60), Font = new Font("Segoe UI", 14, FontStyle.Bold), BackColor = Color.LightBlue };
            btnStart.Click += (s, e) => { _isCapturing = true; camera?.StartCapture(); };
            tabPageMain.Controls.Add(btnStart);

            Button btnStop = new Button { Text = "Stop", Location = new Point(170, 330), Size = new Size(150, 60), Font = new Font("Segoe UI", 14, FontStyle.Bold), BackColor = Color.LightCoral };
            btnStop.Click += (s, e) => { _isCapturing = false; camera?.StopCapture(); };
            tabPageMain.Controls.Add(btnStop);

            // --- 設定タブの構成 ---
            Panel pnlSettings = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            tabPageSettings.Controls.Add(pnlSettings);

            int yPos = 10;
            AddLabel(pnlSettings, "■ 公差・更新設定", yPos, true); yPos += 30;
            numTargetPitch = AddNum(pnlSettings, "ピッチ目標値 (mm):", ref yPos, (decimal)_targetPitch, 2, 0.1m);
            numTolPlus = AddNum(pnlSettings, "上限公差 (+mm):", ref yPos, (decimal)_tolPlus, 2, 0.1m);
            numTolMinus = AddNum(pnlSettings, "下限公差 (-mm):", ref yPos, (decimal)_tolMinus, 2, 0.1m);
            numUpdateInterval = AddNum(pnlSettings, "数値更新間隔 (ms):", ref yPos, _updateIntervalMs, 0, 100m);

            yPos += 10;
            AddLabel(pnlSettings, "■ 3段キャリブレーション設定", yPos, true); yPos += 30;

            // ★ キャリブレーション取得ボタンの追加（係数の小数桁を5桁に拡張）
            numMmTop = AddNum(pnlSettings, "上段 mm/Pixel:", ref yPos, (decimal)_mmPerPixelTop, 5, 0.0001m);
            numYTop = AddNum(pnlSettings, "上段 基準Y座標:", ref yPos, (decimal)_refYTop, 1, 10m);
            Button btnCalibTop = new Button { Text = "↑ 現在位置で上段をキャリブレーション", Location = new Point(15, yPos), Width = 280, Height = 35 };
            btnCalibTop.Click += (s, e) => ExecuteCalibration(numMmTop, numYTop);
            pnlSettings.Controls.Add(btnCalibTop); yPos += 50;

            numMmMid = AddNum(pnlSettings, "中段 mm/Pixel:", ref yPos, (decimal)_mmPerPixelMid, 5, 0.0001m);
            numYMid = AddNum(pnlSettings, "中段 基準Y座標:", ref yPos, (decimal)_refYMid, 1, 10m);
            Button btnCalibMid = new Button { Text = "− 現在位置で中段をキャリブレーション", Location = new Point(15, yPos), Width = 280, Height = 35 };
            btnCalibMid.Click += (s, e) => ExecuteCalibration(numMmMid, numYMid);
            pnlSettings.Controls.Add(btnCalibMid); yPos += 50;

            numMmBot = AddNum(pnlSettings, "下段 mm/Pixel:", ref yPos, (decimal)_mmPerPixelBot, 5, 0.0001m);
            numYBot = AddNum(pnlSettings, "下段 基準Y座標:", ref yPos, (decimal)_refYBot, 1, 10m);
            Button btnCalibBot = new Button { Text = "↓ 現在位置で下段をキャリブレーション", Location = new Point(15, yPos), Width = 280, Height = 35 };
            btnCalibBot.Click += (s, e) => ExecuteCalibration(numMmBot, numYBot);
            pnlSettings.Controls.Add(btnCalibBot); yPos += 50;

            yPos += 10;
            AddLabel(pnlSettings, "■ 視野設定 (ROI)", yPos, true); yPos += 30;

            AddLabel(pnlSettings, "ROI X (横位置):", yPos); yPos += 20;
            trackBarRoiX = new TrackBar { Location = new Point(10, yPos), Width = 200, Minimum = 0, Maximum = 2448, TickStyle = TickStyle.None, Value = Math.Min(_roiX, 2448) };
            numRoiX = new NumericUpDown { Location = new Point(215, yPos), Width = 80, Minimum = 0, Maximum = 4000, Value = _roiX };
            trackBarRoiX.Scroll += (s, e) => { numRoiX.Value = trackBarRoiX.Value; Settings_ValueChanged(null, null); };
            numRoiX.ValueChanged += (s, e) => { if (numRoiX.Value <= trackBarRoiX.Maximum) trackBarRoiX.Value = (int)numRoiX.Value; Settings_ValueChanged(null, null); };
            pnlSettings.Controls.Add(trackBarRoiX); pnlSettings.Controls.Add(numRoiX); yPos += 40;

            AddLabel(pnlSettings, "ROI Y (縦位置):", yPos); yPos += 20;
            trackBarRoiY = new TrackBar { Location = new Point(10, yPos), Width = 200, Minimum = 0, Maximum = 2048, TickStyle = TickStyle.None, Value = Math.Min(_roiY, 2048) };
            numRoiY = new NumericUpDown { Location = new Point(215, yPos), Width = 80, Minimum = 0, Maximum = 4000, Value = _roiY };
            trackBarRoiY.Scroll += (s, e) => { numRoiY.Value = trackBarRoiY.Value; Settings_ValueChanged(null, null); };
            numRoiY.ValueChanged += (s, e) => { if (numRoiY.Value <= trackBarRoiY.Maximum) trackBarRoiY.Value = (int)numRoiY.Value; Settings_ValueChanged(null, null); };
            pnlSettings.Controls.Add(trackBarRoiY); pnlSettings.Controls.Add(numRoiY); yPos += 40;

            numRoiWidth = AddNum(pnlSettings, "ROI 幅:", ref yPos, _roiWidth, 0, 1);
            numRoiHeight = AddNum(pnlSettings, "ROI 高さ:", ref yPos, _roiHeight, 0, 1);
        }

        private void AddLabel(Panel p, string t, int y, bool b = false)
        {
            Label l = new Label { Text = t, Location = new Point(10, y), AutoSize = true };
            if (b) l.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            p.Controls.Add(l);
        }

        private NumericUpDown AddNum(Panel p, string t, ref int y, decimal v, int d, decimal i = 1)
        {
            AddLabel(p, t, y);
            var n = new NumericUpDown { Location = new Point(15, y + 20), Width = 280, DecimalPlaces = d, Increment = i, Minimum = 0, Maximum = 10000 };
            n.Value = v;
            n.ValueChanged += Settings_ValueChanged;
            p.Controls.Add(n);
            y += 50;
            return n;
        }

        // ==========================================
        // 3. キャリブレーション実行ロジック (復元)
        // ==========================================
        public static string InputBox(string title, string promptText, string value)
        {
            Form form = new Form();
            Label label = new Label();
            TextBox textBox = new TextBox();
            Button buttonOk = new Button();
            Button buttonCancel = new Button();

            form.Text = title;
            label.Text = promptText;
            textBox.Text = value;

            buttonOk.Text = "OK";
            buttonCancel.Text = "キャンセル";
            buttonOk.DialogResult = DialogResult.OK;
            buttonCancel.DialogResult = DialogResult.Cancel;

            label.SetBounds(10, 20, 360, 13);
            textBox.SetBounds(12, 40, 360, 20);
            buttonOk.SetBounds(210, 75, 75, 30);
            buttonCancel.SetBounds(295, 75, 75, 30);
            label.AutoSize = true;

            form.ClientSize = new Size(396, 120);
            form.Controls.AddRange(new Control[] { label, textBox, buttonOk, buttonCancel });
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.StartPosition = FormStartPosition.CenterParent;
            form.MinimizeBox = false;
            form.MaximizeBox = false;
            form.AcceptButton = buttonOk;
            form.CancelButton = buttonCancel;

            DialogResult dialogResult = form.ShowDialog();
            return dialogResult == DialogResult.OK ? textBox.Text : "";
        }

        private void ExecuteCalibration(NumericUpDown numMm, NumericUpDown numY)
        {
            if (_lastPitchPx <= 0)
            {
                MessageBox.Show("有効な測定結果がありません。画面にピッチ（緑線）が安定して表示されている状態で実行してください。", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string input = InputBox("実寸入力", $"検出されたピッチ幅: {_lastPitchPx:F2} px\n測定した箇所の実際の寸法(mm)を入力してください:", "100.0");

            if (double.TryParse(input, out double actualMm) && actualMm > 0)
            {
                double newMmPerPx = actualMm / _lastPitchPx;

                // UIコントロールの範囲内に収めて適用する
                if (newMmPerPx >= (double)numMm.Minimum && newMmPerPx <= (double)numMm.Maximum)
                {
                    numMm.Value = (decimal)newMmPerPx;
                }

                if (_lastCenterY >= (double)numY.Minimum && _lastCenterY <= (double)numY.Maximum)
                {
                    numY.Value = (decimal)_lastCenterY;
                }

                Settings_ValueChanged(null, null);
                MessageBox.Show($"キャリブレーションを完了しました。\n\n新しい係数: {newMmPerPx:F5} mm/px\n基準Y座標: {_lastCenterY:F1}", "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        // ==========================================
        // 4. 設定値の反映と保存・読み込み
        // ==========================================
        private void Settings_ValueChanged(object sender, EventArgs e)
        {
            if (_isLoadingConfig) return;
            _targetPitch = (double)numTargetPitch.Value;
            _tolPlus = (double)numTolPlus.Value;
            _tolMinus = (double)numTolMinus.Value;
            _updateIntervalMs = (int)numUpdateInterval.Value;

            _mmPerPixelTop = (double)numMmTop.Value;
            _refYTop = (double)numYTop.Value;
            _mmPerPixelMid = (double)numMmMid.Value;
            _refYMid = (double)numYMid.Value;
            _mmPerPixelBot = (double)numMmBot.Value;
            _refYBot = (double)numYBot.Value;

            _roiX = (int)numRoiX.Value;
            _roiY = (int)numRoiY.Value;
            _roiWidth = (int)numRoiWidth.Value;
            _roiHeight = (int)numRoiHeight.Value;
        }

        private void SaveConfig()
        {
            try
            {
                string path = Path.Combine(Application.StartupPath, "config.txt");
                using (StreamWriter sw = new StreamWriter(path, false, Encoding.UTF8))
                {
                    sw.WriteLine($"TargetPitch={_targetPitch}");
                    sw.WriteLine($"TolPlus={_tolPlus}");
                    sw.WriteLine($"TolMinus={_tolMinus}");
                    sw.WriteLine($"UpdateInterval={_updateIntervalMs}");
                    sw.WriteLine($"MmPerPixelTop={_mmPerPixelTop}");
                    sw.WriteLine($"RefYTop={_refYTop}");
                    sw.WriteLine($"MmPerPixelMid={_mmPerPixelMid}");
                    sw.WriteLine($"RefYMid={_refYMid}");
                    sw.WriteLine($"MmPerPixelBot={_mmPerPixelBot}");
                    sw.WriteLine($"RefYBot={_refYBot}");
                    sw.WriteLine($"RoiX={_roiX}");
                    sw.WriteLine($"RoiY={_roiY}");
                    sw.WriteLine($"RoiWidth={_roiWidth}");
                    sw.WriteLine($"RoiHeight={_roiHeight}");
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
                    string[] lines = File.ReadAllLines(path);
                    foreach (string line in lines)
                    {
                        var parts = line.Split('=');
                        if (parts.Length < 2) continue;
                        string key = parts[0].Trim();
                        string val = parts[1].Trim();

                        if (key == "TargetPitch" && double.TryParse(val, out double d2)) numTargetPitch.Value = (decimal)d2;
                        if (key == "TolPlus" && double.TryParse(val, out double d3)) numTolPlus.Value = (decimal)d3;
                        if (key == "TolMinus" && double.TryParse(val, out double d4)) numTolMinus.Value = (decimal)d4;
                        if (key == "UpdateInterval" && int.TryParse(val, out int i0)) numUpdateInterval.Value = i0;

                        if (key == "MmPerPixelTop" && double.TryParse(val, out double mt)) numMmTop.Value = (decimal)mt;
                        if (key == "RefYTop" && double.TryParse(val, out double yt)) numYTop.Value = (decimal)yt;
                        if (key == "MmPerPixelMid" && double.TryParse(val, out double mm)) numMmMid.Value = (decimal)mm;
                        if (key == "RefYMid" && double.TryParse(val, out double ym)) numYMid.Value = (decimal)ym;
                        if (key == "MmPerPixelBot" && double.TryParse(val, out double mb)) numMmBot.Value = (decimal)mb;
                        if (key == "RefYBot" && double.TryParse(val, out double yb)) numYBot.Value = (decimal)yb;

                        if (key == "RoiX" && int.TryParse(val, out int i1)) { numRoiX.Value = i1; if (i1 <= trackBarRoiX.Maximum) trackBarRoiX.Value = i1; }
                        if (key == "RoiY" && int.TryParse(val, out int i2)) { numRoiY.Value = i2; if (i2 <= trackBarRoiY.Maximum) trackBarRoiY.Value = i2; }
                        if (key == "RoiWidth" && int.TryParse(val, out int i3)) numRoiWidth.Value = i3;
                        if (key == "RoiHeight" && int.TryParse(val, out int i4)) numRoiHeight.Value = i4;
                    }
                }
            }
            catch { }
            finally
            {
                _isLoadingConfig = false;
                Settings_ValueChanged(null, null);
            }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveConfig();
            camera?.Terminate();
        }

        // ==========================================
        // 5. 画像処理メイン: カメラからの映像受信
        // ==========================================
        private void Camera_OnFrameCaptured(object sender, Mat frame)
        {
            try
            {
                if (!_isCapturing || frame == null || frame.IsDisposed) return;

                using (Mat dispMat = new Mat())
                using (Mat frameGray = new Mat())
                {
                    if (frame.Channels() == 3)
                    {
                        frame.CopyTo(dispMat);
                        Cv2.CvtColor(frame, frameGray, ColorConversionCodes.BGR2GRAY);
                    }
                    else
                    {
                        Cv2.CvtColor(frame, dispMat, ColorConversionCodes.GRAY2BGR);
                        frame.CopyTo(frameGray);
                    }

                    var result = MeasurementCore.ProcessFrame(
                        frameGray, dispMat,
                        _roiX, _roiY, _roiWidth, _roiHeight,
                        _mmPerPixelTop, _mmPerPixelMid, _mmPerPixelBot,
                        _refYTop, _refYMid, _refYBot);

                    Bitmap displayBmp = BitmapConverter.ToBitmap(dispMat);

                    this.BeginInvoke(new Action(() =>
                    {
                        var oldImage = pictureBox1.Image;
                        pictureBox1.Image = displayBmp;
                        if (oldImage != null) oldImage.Dispose();

                        // ★ キャリブレーション用に最新のピクセル値とY座標を保持
                        if (result.IsValid)
                        {
                            _lastPitchPx = result.PitchPx;
                            _lastCenterY = (result.CenterLeft.Y + result.CenterRight.Y) / 2.0;
                        }

                        if ((DateTime.Now - _lastUiUpdate).TotalMilliseconds >= _updateIntervalMs)
                        {
                            if (result.IsValid)
                            {
                                lblPitch.Text = $"ピッチ: {result.PitchMm:F2} mm";
                                lblDiameterL.Text = $"左穴径: {result.DiameterLeftMm:F2} mm";
                                lblDiameterR.Text = $"右穴径: {result.DiameterRightMm:F2} mm";

                                bool isPitchOk = (result.PitchMm >= _targetPitch - _tolMinus) &&
                                                 (result.PitchMm <= _targetPitch + _tolPlus);

                                lblBigResult.Text = isPitchOk ? "OK" : "NG";
                                lblBigResult.BackColor = isPitchOk ? Color.LimeGreen : Color.Red;
                            }
                            else
                            {
                                lblPitch.Text = "ピッチ: --- mm";
                                lblDiameterL.Text = "左穴径: --- mm";
                                lblDiameterR.Text = "右穴径: --- mm";

                                lblBigResult.Text = "NG";
                                lblBigResult.BackColor = Color.Red;
                            }

                            _lastUiUpdate = DateTime.Now;
                        }
                    }));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            finally
            {
                if (frame != null && !frame.IsDisposed)
                {
                    frame.Dispose();
                }
            }
        }
    }
}