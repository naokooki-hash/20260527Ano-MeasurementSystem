using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

namespace MeasurementSystem // ※ご自身のプロジェクト名に合わせてください
{
    public class MeasurementResult
    {
        public bool IsValid { get; set; }
        public bool HasProduct { get; set; }         // ★新規追加: 製品が存在するかどうかのフラグ
        public double PitchPx { get; set; }
        public double PitchMm { get; set; }
        public double DiameterLeftPx { get; set; }
        public double DiameterLeftMm { get; set; }
        public double DiameterRightPx { get; set; }
        public double DiameterRightMm { get; set; }
        public double AngleDegree { get; set; }
        public Point CenterLeft { get; set; }
        public Point CenterRight { get; set; }
        public double AppliedMmPerPixel { get; set; }
    }

    public static class MeasurementCore
    {
        public static MeasurementResult ProcessFrame(
            Mat frameGray, Mat drawMat,
            int roiX, int roiY, int roiWidthPx, int roiHeightPx,
            double mmTop, double mmMid, double mmBot,
            double yTop, double yMid, double yBot)
        {
            var result = new MeasurementResult { IsValid = false, HasProduct = false };

            roiX = Math.Max(0, Math.Min(roiX, frameGray.Cols - 1));
            roiY = Math.Max(0, Math.Min(roiY, frameGray.Rows - 1));
            roiWidthPx = Math.Max(1, Math.Min(roiWidthPx, frameGray.Cols - roiX));
            roiHeightPx = Math.Max(1, Math.Min(roiHeightPx, frameGray.Rows - roiY));

            Rect roiRect = new Rect(roiX, roiY, roiWidthPx, roiHeightPx);
            Cv2.Rectangle(drawMat, roiRect, new Scalar(0, 165, 255), 2);

            using (Mat roiMat = new Mat(frameGray, roiRect))
            {
                // =========================================================
                // ★ ソフトウェアトリガー（軽量化処理）
                // =========================================================
                using (Mat threshForTrigger = new Mat())
                {
                    // バックライト上のシルエットを検出するため、128を境界に反転二値化（黒い部分が白(255)になる）
                    Cv2.Threshold(roiMat, threshForTrigger, 128, 255, ThresholdTypes.BinaryInv);

                    // 「黒だった部分」のピクセル数をカウント
                    int blackPixelCount = Cv2.CountNonZero(threshForTrigger);
                    double blackRatio = (double)blackPixelCount / (roiRect.Width * roiRect.Height);

                    // 黒の割合が10%未満なら「製品なし」とみなして即座に終了（超低負荷）
                    if (blackRatio < 0.10)
                    {
                        return result;
                    }
                }

                // =========================================================
                // 製品がある場合のみ、以下の重い画像処理を実行する
                // =========================================================
                result.HasProduct = true;

                using (Mat blurred = new Mat())
                {
                    Cv2.GaussianBlur(roiMat, blurred, new Size(5, 5), 0);

                    // ※まだ重い場合はここの dp: 1.0 を dp: 2.0 にするとさらに軽くなります
                    CircleSegment[] circles = Cv2.HoughCircles(
                        blurred, HoughModes.Gradient,
                        dp: 1.0, minDist: roiWidthPx / 3.0,
                        param1: 100, param2: 30,
                        minRadius: 20, maxRadius: 200
                    );

                    if (circles.Length >= 2)
                    {
                        var sortedCircles = circles.OrderBy(c => c.Center.X).ToArray();
                        var leftCircle = sortedCircles[0];
                        var rightCircle = sortedCircles[1];

                        result.CenterLeft = new Point(leftCircle.Center.X + roiX, leftCircle.Center.Y + roiY);
                        result.CenterRight = new Point(rightCircle.Center.X + roiX, rightCircle.Center.Y + roiY);

                        double currentY = (result.CenterLeft.Y + result.CenterRight.Y) / 2.0;

                        double appliedMmPerPixel = mmMid;
                        if (currentY <= yTop) appliedMmPerPixel = mmTop;
                        else if (currentY >= yBot) appliedMmPerPixel = mmBot;
                        else
                        {
                            if (currentY < yMid) appliedMmPerPixel = mmTop + ((currentY - yTop) / (yMid - yTop)) * (mmMid - mmTop);
                            else appliedMmPerPixel = mmMid + ((currentY - yMid) / (yBot - yMid)) * (mmBot - mmMid);
                        }
                        result.AppliedMmPerPixel = appliedMmPerPixel;

                        result.DiameterLeftPx = leftCircle.Radius * 2;
                        result.DiameterRightPx = rightCircle.Radius * 2;
                        result.DiameterLeftMm = result.DiameterLeftPx * appliedMmPerPixel;
                        result.DiameterRightMm = result.DiameterRightPx * appliedMmPerPixel;

                        double dx = result.CenterRight.X - result.CenterLeft.X;
                        double dy = result.CenterRight.Y - result.CenterLeft.Y;
                        result.PitchPx = Math.Sqrt(dx * dx + dy * dy);
                        result.PitchMm = result.PitchPx * appliedMmPerPixel;

                        result.AngleDegree = Math.Atan2(dy, dx) * (180.0 / Math.PI);

                        result.IsValid = true;

                        Cv2.Circle(drawMat, result.CenterLeft, (int)leftCircle.Radius, Scalar.Blue, 2);
                        Cv2.Circle(drawMat, result.CenterLeft, 3, Scalar.Blue, -1);
                        Cv2.Circle(drawMat, result.CenterRight, (int)rightCircle.Radius, Scalar.Red, 2);
                        Cv2.Circle(drawMat, result.CenterRight, 3, Scalar.Red, -1);
                        Cv2.Line(drawMat, result.CenterLeft, result.CenterRight, Scalar.Lime, 2);
                    }
                }
            }

            return result;
        }
    }
}