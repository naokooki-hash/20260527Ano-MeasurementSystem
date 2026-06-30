using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

using CvPoint = OpenCvSharp.Point;
using CvSize = OpenCvSharp.Size;
using CvRect = OpenCvSharp.Rect;

namespace MeasurementSystem
{
    public class MeasurementResult
    {
        public bool IsValid { get; set; }
        public double MeasuredValuePx { get; set; }
        public double MeasuredValueMm { get; set; }
        public double LeftHoleDiaMm { get; set; }
        public double RightHoleDiaMm { get; set; }
        public double CenterY { get; set; }
    }

    // 計算された円のデータを保持する構造体
    public struct CalculatedCircle
    {
        public Point2f Center;
        public double Radius;
    }

    public class MeasurementEngine : IDisposable
    {
        private Mat _morphKernel;
        private bool _isDisposed = false;

        public MeasurementEngine()
        {
            _morphKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new CvSize(61, 61));
        }

        public MeasurementResult ProcessFrame(
            Mat frameGray, Mat drawMat,
            int roiX, int roiY, int roiWidthPx, int roiHeightPx,
            double mmTop, double mmMid, double mmBot,
            double yTop, double yMid, double yBot,
            int thresholdValue, bool showBinaryOnly)
        {
            var result = new MeasurementResult { IsValid = false };

            roiX = Math.Max(0, Math.Min(roiX, frameGray.Cols - 1));
            roiY = Math.Max(0, Math.Min(roiY, frameGray.Rows - 1));
            roiWidthPx = Math.Max(1, Math.Min(roiWidthPx, frameGray.Cols - roiX));
            roiHeightPx = Math.Max(1, Math.Min(roiHeightPx, frameGray.Rows - roiY));

            CvRect roiRect = new CvRect(roiX, roiY, roiWidthPx, roiHeightPx);

            if (showBinaryOnly)
            {
                using Mat fullThresh = new Mat();
                Cv2.Threshold(frameGray, fullThresh, thresholdValue, 255, ThresholdTypes.Binary);
                Cv2.CvtColor(fullThresh, drawMat, ColorConversionCodes.GRAY2BGR);
                Cv2.Rectangle(drawMat, roiRect, Scalar.Orange, 2);
            }
            else
            {
                Cv2.CvtColor(frameGray, drawMat, ColorConversionCodes.GRAY2BGR);
                Cv2.Rectangle(drawMat, roiRect, Scalar.Orange, 2);
            }

            using Mat roiGray = new Mat(frameGray, roiRect);
            using Mat thresh = new Mat();
            Cv2.Threshold(roiGray, thresh, thresholdValue, 255, ThresholdTypes.Binary);

            using Mat mask = new Mat();
            Cv2.MorphologyEx(thresh, mask, MorphTypes.Open, _morphKernel);

            Cv2.FindContours(mask, out CvPoint[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);

            // ★明示的なリスト名に変更
            List<(double X, CalculatedCircle CircleData)> circleDataList = new List<(double X, CalculatedCircle CircleData)>();

            foreach (var contour in contours)
            {
                double area = Cv2.ContourArea(contour);
                if (area < 5000 || area > 200000) continue;

                CvRect rect = Cv2.BoundingRect(contour);
                rect.Inflate(20, 20);
                rect.Intersect(new CvRect(0, 0, thresh.Cols, thresh.Rows));

                if (rect.Width <= 0 || rect.Height <= 0) continue;

                using Mat holeRoi = new Mat(thresh, rect);
                Cv2.FindContours(holeRoi, out CvPoint[][] localContours, out _, RetrievalModes.External, ContourApproximationModes.ApproxNone);

                if (localContours.Length > 0)
                {
                    var largestLocal = localContours.OrderByDescending(c => Cv2.ContourArea(c)).First();
                    if (largestLocal.Length >= 5)
                    {
                        if (TryCalculateCircle3Point(largestLocal, out CalculatedCircle calculatedCircle))
                        {
                            calculatedCircle.Center.X += rect.X;
                            calculatedCircle.Center.Y += rect.Y;

                            if (calculatedCircle.Radius > 10 && calculatedCircle.Radius < 500)
                            {
                                circleDataList.Add((calculatedCircle.Center.X, calculatedCircle));
                            }
                        }
                    }
                }
            }

            circleDataList = circleDataList.OrderBy(item => item.X).ToList();

            if (circleDataList.Count >= 2)
            {
                // ★左と右のデータを明確に取得
                var leftItem = circleDataList.First();
                var rightItem = circleDataList.Last();

                Point2f cLeft = new Point2f(leftItem.CircleData.Center.X + roiX, leftItem.CircleData.Center.Y + roiY);
                Point2f cRight = new Point2f(rightItem.CircleData.Center.X + roiX, rightItem.CircleData.Center.Y + roiY);

                double distancePx = cLeft.DistanceTo(cRight);
                double currentY = (cLeft.Y + cRight.Y) / 2.0;

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

                result.IsValid = true;
                result.MeasuredValuePx = distancePx;
                result.MeasuredValueMm = distancePx * appliedMmPerPixel;
                result.CenterY = currentY;
                result.LeftHoleDiaMm = (leftItem.CircleData.Radius * 2.0) * appliedMmPerPixel;
                result.RightHoleDiaMm = (rightItem.CircleData.Radius * 2.0) * appliedMmPerPixel;

                Cv2.Circle(drawMat, (CvPoint)cLeft, (int)leftItem.CircleData.Radius, Scalar.Magenta, 3);
                Cv2.Circle(drawMat, (CvPoint)cRight, (int)rightItem.CircleData.Radius, Scalar.Magenta, 3);
                Cv2.Line(drawMat, (CvPoint)cLeft, (CvPoint)cRight, Scalar.Lime, 2);
                Cv2.Circle(drawMat, (CvPoint)cLeft, 5, Scalar.Red, -1);
                Cv2.Circle(drawMat, (CvPoint)cRight, 5, Scalar.Red, -1);
            }

            return result;
        }

        private bool TryCalculateCircle3Point(CvPoint[] contour, out CalculatedCircle circle)
        {
            circle = new CalculatedCircle();
            if (contour == null || contour.Length < 3) return false;

            int minY = contour.Min(p => p.Y);
            int maxY = contour.Max(p => p.Y);
            double midY = minY + (maxY - minY) / 2.0;

            var upperPoints = contour.Where(p => p.Y < midY).ToList();
            if (upperPoints.Count < 3) return false;

            int count = upperPoints.Count;
            CvPoint p1 = upperPoints[0];
            CvPoint p2 = upperPoints[count / 2];
            CvPoint p3 = upperPoints[count - 1];

            double x1 = p1.X, y1 = p1.Y;
            double x2 = p2.X, y2 = p2.Y;
            double x3 = p3.X, y3 = p3.Y;

            double a = x1 * (y2 - y3) - y1 * (x2 - x3) + x2 * y3 - x3 * y2;
            if (Math.Abs(a) < 1e-5) return false;

            double x1_sq = x1 * x1 + y1 * y1;
            double x2_sq = x2 * x2 + y2 * y2;
            double x3_sq = x3 * x3 + y3 * y3;

            double cx = (x1_sq * (y2 - y3) + x2_sq * (y3 - y1) + x3_sq * (y1 - y2)) / (2 * a);
            double cy = (x1_sq * (x3 - x2) + x2_sq * (x1 - x3) + x3_sq * (x2 - x1)) / (2 * a);

            circle.Center = new Point2f((float)cx, (float)cy);
            circle.Radius = Math.Sqrt((x1 - cx) * (x1 - cx) + (y1 - cy) * (y1 - cy));

            return true;
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _morphKernel?.Dispose();
                _isDisposed = true;
            }
        }
    }
}