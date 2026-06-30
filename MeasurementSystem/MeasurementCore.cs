using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

namespace MeasurementSystem
{
    public class MeasurementResult
    {
        public bool IsValid { get; set; }
        public double MeasuredValuePx { get; set; } // 穴ピッチのピクセル距離
        public double MeasuredValueMm { get; set; } // 穴ピッチの物理距離(mm)
        public double AngleDegree { get; set; }     // 左右の穴を結んだ線の傾き
        public double AppliedMmPerPixel { get; set; }
        public double CenterY { get; set; }         // 2つの穴の中間Y座標（補正用）
        public Point2f LeftHoleCenter { get; set; }
        public Point2f RightHoleCenter { get; set; }
        public double LeftHoleDiaMm { get; set; }   // 左穴径 (エラー原因だった変数名)
        public double RightHoleDiaMm { get; set; }  // 右穴径
    }

    public class MeasurementEngine
    {
        public MeasurementResult ProcessFrame(
            Mat frameGray, Mat drawMat,
            int roiX, int roiY, int roiWidthPx, int roiHeightPx,
            double mmTop, double mmMid, double mmBot,
            double yTop, double yMid, double yBot,
            int thresholdValue) // 二値化の固定閾値
        {
            var result = new MeasurementResult { IsValid = false };

            // ROIのクリッピング処理
            roiX = Math.Max(0, Math.Min(roiX, frameGray.Cols - 1));
            roiY = Math.Max(0, Math.Min(roiY, frameGray.Rows - 1));
            roiWidthPx = Math.Max(1, Math.Min(roiWidthPx, frameGray.Cols - roiX));
            roiHeightPx = Math.Max(1, Math.Min(roiHeightPx, frameGray.Rows - roiY));

            Rect roiRect = new Rect(roiX, roiY, roiWidthPx, roiHeightPx);
            Cv2.Rectangle(drawMat, roiRect, Scalar.Blue, 2);

            using Mat roiGray = new Mat(frameGray, roiRect);
            using Mat thresh = new Mat();

            // 固定閾値で完全に二値化（透過照明用：穴が白、金属が黒）
            Cv2.Threshold(roiGray, thresh, thresholdValue, 255, ThresholdTypes.Binary);

            // デバッグ表示用に二値化結果を画面にうっすら合成
            using (Mat threshColor = new Mat())
            {
                Cv2.CvtColor(thresh, threshColor, ColorConversionCodes.GRAY2BGR);
                Mat roiDraw = new Mat(drawMat, roiRect);
                Cv2.AddWeighted(roiDraw, 0.7, threshColor, 0.3, 0, roiDraw);
            }

            // 輪郭抽出
            Cv2.FindContours(thresh, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            List<RotatedRect> holes = new List<RotatedRect>();

            foreach (var contour in contours)
            {
                double area = Cv2.ContourArea(contour);
                // ノイズと巨大すぎる外枠を除外
                if (area < 1000 || area > 100000) continue;

                // 楕円近似には5点以上必要
                if (contour.Length >= 5)
                {
                    RotatedRect ellipse = Cv2.FitEllipse(contour);

                    // アスペクト比で真円に近いものだけを残す（極端な長細いゴミを排除）
                    double aspect = Math.Min(ellipse.Size.Width, ellipse.Size.Height) / Math.Max(ellipse.Size.Width, ellipse.Size.Height);
                    if (aspect > 0.5)
                    {
                        holes.Add(ellipse);
                    }
                }
            }

            // 見つかった穴をX座標（左右）でソート
            holes = holes.OrderBy(h => h.Center.X).ToList();

            // 穴が2つ以上見つかった場合、左右の端にある2つを測定対象とする
            if (holes.Count >= 2)
            {
                var leftHole = holes.First();
                var rightHole = holes.Last();

                // ROI内のローカル座標から、画像全体のグローバル座標へ変換
                Point2f cLeft = new Point2f(leftHole.Center.X + roiX, leftHole.Center.Y + roiY);
                Point2f cRight = new Point2f(rightHole.Center.X + roiX, rightHole.Center.Y + roiY);

                double distancePx = cLeft.DistanceTo(cRight);
                double currentY = (cLeft.Y + cRight.Y) / 2.0;

                // 3点キャリブレーション係数の算出
                var calibPoints = new List<(double Y, double Ratio)> { (yTop, mmTop), (yMid, mmMid), (yBot, mmBot) }.OrderBy(p => p.Y).ToList();
                double appliedMmPerPixel = calibPoints[1].Ratio;

                if (currentY <= calibPoints[1].Y)
                {
                    if (Math.Abs(calibPoints[1].Y - calibPoints[0].Y) > 1e-5)
                    {
                        double t = Math.Max(0.0, Math.Min(1.0, (currentY - calibPoints[0].Y) / (calibPoints[1].Y - calibPoints[0].Y)));
                        appliedMmPerPixel = calibPoints[0].Ratio + t * (calibPoints[1].Ratio - calibPoints[0].Ratio);
                    }
                    else appliedMmPerPixel = calibPoints[0].Ratio;
                }
                else
                {
                    if (Math.Abs(calibPoints[2].Y - calibPoints[1].Y) > 1e-5)
                    {
                        double t = Math.Max(0.0, Math.Min(1.0, (currentY - calibPoints[1].Y) / (calibPoints[2].Y - calibPoints[1].Y)));
                        appliedMmPerPixel = calibPoints[1].Ratio + t * (calibPoints[2].Ratio - calibPoints[1].Ratio);
                    }
                    else appliedMmPerPixel = calibPoints[1].Ratio;
                }

                // 結果の格納
                result.IsValid = true;
                result.MeasuredValuePx = distancePx;
                result.MeasuredValueMm = distancePx * appliedMmPerPixel;
                result.AngleDegree = Math.Atan2(cRight.Y - cLeft.Y, cRight.X - cLeft.X) * (180.0 / Math.PI);
                result.AppliedMmPerPixel = appliedMmPerPixel;
                result.CenterY = currentY;
                result.LeftHoleCenter = cLeft;
                result.RightHoleCenter = cRight;

                // ここで計算した直径をプロパティにセット
                result.LeftHoleDiaMm = ((leftHole.Size.Width + leftHole.Size.Height) / 2.0) * appliedMmPerPixel;
                result.RightHoleDiaMm = ((rightHole.Size.Width + rightHole.Size.Height) / 2.0) * appliedMmPerPixel;

                // 描画処理 (円とピッチの線)
                Cv2.Ellipse(drawMat, new RotatedRect(cLeft, leftHole.Size, leftHole.Angle), Scalar.Magenta, 3);
                Cv2.Ellipse(drawMat, new RotatedRect(cRight, rightHole.Size, rightHole.Angle), Scalar.Magenta, 3);
                Cv2.Line(drawMat, (Point)cLeft, (Point)cRight, Scalar.Lime, 2);
                Cv2.Circle(drawMat, (Point)cLeft, 5, Scalar.Red, -1);
                Cv2.Circle(drawMat, (Point)cRight, 5, Scalar.Red, -1);
            }

            return result;
        }
    }
}