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
        public double HeightPx { get; set; }
        public double HeightMm { get; set; }
        public double Slope { get; set; }
        public double CTop { get; set; }
        public double CBot { get; set; }
        public double AngleDegree { get; set; }
        public double AppliedMmPerPixel { get; set; }
        public double CenterY { get; set; } // ← 【追加】実際の緑線の中心Y座標
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

            roiX = Math.Max(0, Math.Min(roiX, frameGray.Cols - 1));
            roiY = Math.Max(0, Math.Min(roiY, frameGray.Rows - 1));
            roiWidthPx = Math.Max(1, Math.Min(roiWidthPx, frameGray.Cols - roiX));
            roiHeightPx = Math.Max(1, Math.Min(roiHeightPx, frameGray.Rows - roiY));

            Rect roiRect = new Rect(roiX, roiY, roiWidthPx, roiHeightPx);
            Cv2.Rectangle(drawMat, roiRect, Scalar.Blue, 2);

            using Mat roiGray = new Mat(frameGray, roiRect);
            using Mat blurred = new Mat();
            Cv2.GaussianBlur(roiGray, blurred, new Size(5, 5), 0);

            using Mat thresh = new Mat();
            Cv2.Threshold(blurred, thresh, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

            Cv2.FindContours(thresh, out Point[][] contours, out HierarchyIndex[] hierarchy, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            if (contours.Length == 0) return result;

            var maxContour = contours.OrderByDescending(c => Cv2.ContourArea(c)).First();
            if (Cv2.ContourArea(maxContour) < 5000) return result;

            var hull = Cv2.ConvexHull(maxContour);

            using Mat mask = Mat.Zeros(roiGray.Size(), MatType.CV_8UC1);
            Cv2.DrawContours(mask, new[] { hull }, -1, Scalar.White, -1);

            List<int> validX = new List<int>();
            List<int> topY = new List<int>();
            List<int> botY = new List<int>();
            List<int> heights = new List<int>();

            var indexer = mask.GetGenericIndexer<byte>();
            for (int x = 0; x < mask.Cols; x++)
            {
                int t = -1, b = -1;
                for (int y = 0; y < mask.Rows; y++)
                {
                    if (indexer[y, x] > 0)
                    {
                        if (t == -1) t = y;
                        b = y;
                    }
                }
                if (t != -1 && b != -1)
                {
                    validX.Add(x);
                    topY.Add(t);
                    botY.Add(b);
                    heights.Add(b - t + 1);
                }
            }

            if (heights.Count < 10) return result;

            int trimCount = (int)(heights.Count * 0.3);
            var sortedIndices = heights.Select((h, index) => new { h, index })
                                       .OrderBy(x => x.h).Select(x => x.index).ToList();

            List<int> filteredIndices = (trimCount * 2 >= heights.Count) ? sortedIndices
                : sortedIndices.Skip(trimCount).Take(heights.Count - trimCount * 2).ToList();

            double[] fx = filteredIndices.Select(i => (double)validX[i]).ToArray();
            double[] fty = filteredIndices.Select(i => (double)topY[i]).ToArray();
            double[] fby = filteredIndices.Select(i => (double)botY[i]).ToArray();

            FitLine(fx, fty, out double mTop, out double cTop);
            FitLine(fx, fby, out double mBot, out double cBot);

            double mAvg = (mTop + mBot) / 2.0;
            double meanX = fx.Average(), meanTy = fty.Average(), meanBy = fby.Average();

            double cTopAdj = meanTy - mAvg * meanX;
            double cBotAdj = meanBy - mAvg * meanX;

            double distancePx = Math.Abs(cBotAdj - cTopAdj) / Math.Sqrt(mAvg * mAvg + 1);

            double currentY = ((meanTy + meanBy) / 2.0) + roiY;

            var calibPoints = new List<(double Y, double Ratio)>
            {
                (yTop, mmTop),
                (yMid, mmMid),
                (yBot, mmBot)
            }.OrderBy(p => p.Y).ToList();

            var p0 = calibPoints[0];
            var p1 = calibPoints[1];
            var p2 = calibPoints[2];

            double appliedMmPerPixel = p1.Ratio;

            if (currentY <= p1.Y)
            {
                if (Math.Abs(p1.Y - p0.Y) > 1e-5)
                {
                    double t = (currentY - p0.Y) / (p1.Y - p0.Y);
                    t = Math.Max(0.0, Math.Min(1.0, t));
                    appliedMmPerPixel = p0.Ratio + t * (p1.Ratio - p0.Ratio);
                }
                else { appliedMmPerPixel = p0.Ratio; }
            }
            else
            {
                if (Math.Abs(p2.Y - p1.Y) > 1e-5)
                {
                    double t = (currentY - p1.Y) / (p2.Y - p1.Y);
                    t = Math.Max(0.0, Math.Min(1.0, t));
                    appliedMmPerPixel = p1.Ratio + t * (p2.Ratio - p1.Ratio);
                }
                else { appliedMmPerPixel = p1.Ratio; }
            }

            result.IsValid = true;
            result.HeightPx = distancePx;
            result.HeightMm = distancePx * appliedMmPerPixel;
            result.Slope = mAvg;
            result.CTop = cTopAdj;
            result.CBot = cBotAdj;
            result.AngleDegree = Math.Atan(mAvg) * (180.0 / Math.PI);
            result.AppliedMmPerPixel = appliedMmPerPixel;
            result.CenterY = currentY; // ← 【追加】実際のY座標をFormに渡す

            int x1 = roiX;
            int yTopLeft = (int)(mAvg * 0 + cTopAdj) + roiY;
            int yTopRight = (int)(mAvg * roiWidthPx + cTopAdj) + roiY;
            int yBotLeft = (int)(mAvg * 0 + cBotAdj) + roiY;
            int yBotRight = (int)(mAvg * roiWidthPx + cBotAdj) + roiY;

            Cv2.Line(drawMat, new Point(x1, yTopLeft), new Point(x1 + roiWidthPx, yTopRight), Scalar.Lime, 2);
            Cv2.Line(drawMat, new Point(x1, yBotLeft), new Point(x1 + roiWidthPx, yBotRight), Scalar.Lime, 2);

            return result;
        }

        private static void FitLine(double[] x, double[] y, out double m, out double c)
        {
            double sumX = x.Sum(), sumY = y.Sum();
            double sumXx = x.Zip(x, (a, b) => a * b).Sum();
            double sumXy = x.Zip(y, (a, b) => a * b).Sum();
            int n = x.Length;
            double denom = n * sumXx - sumX * sumX;
            if (Math.Abs(denom) < 1e-10) { m = 0; c = sumY / n; }
            else { m = (n * sumXy - sumX * sumY) / denom; c = (sumY * sumXx - sumX * sumXy) / denom; }
        }
    }
}