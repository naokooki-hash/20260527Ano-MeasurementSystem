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
        public double PitchPx { get; set; }
        public double PitchMm { get; set; }
        public double DiameterLeftPx { get; set; }
        public double DiameterLeftMm { get; set; }
        public double DiameterRightPx { get; set; }
        public double DiameterRightMm { get; set; }
        public Point CenterLeft { get; set; }
        public Point CenterRight { get; set; }
        public double AppliedMmPerPixel { get; set; } // 実際に適用された補正係数
    }

    public static class MeasurementCore
    {
        public static MeasurementResult ProcessFrame(
            Mat frameGray, Mat drawMat,
            int roiX, int roiY, int roiWidthPx, int roiHeightPx,
            double mmTop, double mmMid, double mmBot,
            double yTop, double yMid, double yBot)
        {
            var result = new MeasurementResult { IsValid = false };

            // ROIの安全対策
            roiX = Math.Max(0, Math.Min(roiX, frameGray.Cols - 1));
            roiY = Math.Max(0, Math.Min(roiY, frameGray.Rows - 1));
            roiWidthPx = Math.Max(1, Math.Min(roiWidthPx, frameGray.Cols - roiX));
            roiHeightPx = Math.Max(1, Math.Min(roiHeightPx, frameGray.Rows - roiY));

            Rect roiRect = new Rect(roiX, roiY, roiWidthPx, roiHeightPx);

            // 【ご要望】穴の検出成否に関わらず、ROIの範囲枠を常に描画（黄橙色）
            Cv2.Rectangle(drawMat, roiRect, new Scalar(0, 165, 255), 2);

            using (Mat roiMat = new Mat(frameGray, roiRect))
            using (Mat blurred = new Mat())
            {
                Cv2.GaussianBlur(roiMat, blurred, new Size(5, 5), 0);

                CircleSegment[] circles = Cv2.HoughCircles(
                    blurred,
                    HoughModes.Gradient,
                    dp: 1.0,
                    minDist: roiWidthPx / 3.0,
                    param1: 100,
                    param2: 30,
                    minRadius: 20,
                    maxRadius: 200
                );

                if (circles.Length >= 2)
                {
                    var sortedCircles = circles.OrderBy(c => c.Center.X).ToArray();
                    var leftCircle = sortedCircles[0];
                    var rightCircle = sortedCircles[1];

                    result.CenterLeft = new Point(leftCircle.Center.X + roiX, leftCircle.Center.Y + roiY);
                    result.CenterRight = new Point(rightCircle.Center.X + roiX, rightCircle.Center.Y + roiY);

                    // 左右の穴の中心の平均Y座標を基準として補正係数を決定
                    double currentY = (result.CenterLeft.Y + result.CenterRight.Y) / 2.0;

                    // 3段構えのキャリブレーション（線形補間ロジックの復元）
                    double appliedMmPerPixel = mmMid;
                    if (currentY <= yTop)
                    {
                        appliedMmPerPixel = mmTop;
                    }
                    else if (currentY >= yBot)
                    {
                        appliedMmPerPixel = mmBot;
                    }
                    else
                    {
                        if (currentY < yMid)
                        {
                            double t = (currentY - yTop) / (yMid - yTop);
                            appliedMmPerPixel = mmTop + t * (mmMid - mmTop);
                        }
                        else
                        {
                            double t = (currentY - yMid) / (yBot - yMid);
                            appliedMmPerPixel = mmMid + t * (mmBot - mmMid);
                        }
                    }
                    result.AppliedMmPerPixel = appliedMmPerPixel;

                    // 各種寸法計算
                    result.DiameterLeftPx = leftCircle.Radius * 2;
                    result.DiameterRightPx = rightCircle.Radius * 2;
                    result.DiameterLeftMm = result.DiameterLeftPx * appliedMmPerPixel;
                    result.DiameterRightMm = result.DiameterRightPx * appliedMmPerPixel;

                    double dx = result.CenterRight.X - result.CenterLeft.X;
                    double dy = result.CenterRight.Y - result.CenterLeft.Y;
                    result.PitchPx = Math.Sqrt(dx * dx + dy * dy);
                    result.PitchMm = result.PitchPx * appliedMmPerPixel;

                    result.IsValid = true;

                    // 計測結果の描画
                    Cv2.Circle(drawMat, result.CenterLeft, (int)leftCircle.Radius, Scalar.Blue, 2);
                    Cv2.Circle(drawMat, result.CenterLeft, 3, Scalar.Blue, -1);

                    Cv2.Circle(drawMat, result.CenterRight, (int)rightCircle.Radius, Scalar.Red, 2);
                    Cv2.Circle(drawMat, result.CenterRight, 3, Scalar.Red, -1);

                    Cv2.Line(drawMat, result.CenterLeft, result.CenterRight, Scalar.Lime, 2);
                }
            }

            return result;
        }
    }
}