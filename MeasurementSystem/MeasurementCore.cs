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
        public double MeasuredValuePx { get; set; }
        public double MeasuredValueMm { get; set; }
        public double LeftHoleDiaMm { get; set; }
        public double RightHoleDiaMm { get; set; }
    }

    public class MeasurementEngine
    {
        public MeasurementResult ProcessFrame(
            Mat frameGray, Mat drawMat,
            int roiX, int roiY, int roiWidthPx, int roiHeightPx,
            double mmPerPixel, int thresholdValue)
        {
            var result = new MeasurementResult { IsValid = false };

            roiX = Math.Max(0, Math.Min(roiX, frameGray.Cols - 1));
            roiY = Math.Max(0, Math.Min(roiY, frameGray.Rows - 1));
            roiWidthPx = Math.Max(1, Math.Min(roiWidthPx, frameGray.Cols - roiX));
            roiHeightPx = Math.Max(1, Math.Min(roiHeightPx, frameGray.Rows - roiY));

            Rect roiRect = new Rect(roiX, roiY, roiWidthPx, roiHeightPx);
            Cv2.Rectangle(drawMat, roiRect, Scalar.Blue, 2);

            using Mat roiGray = new Mat(frameGray, roiRect);
            using Mat thresh = new Mat();

            // ★ここで指定した閾値で二値化（白黒化）
            Cv2.Threshold(roiGray, thresh, thresholdValue, 255, ThresholdTypes.Binary);

            // デバッグ表示用：二値化の結果を青色でうっすら画面に重ねる
            using (Mat threshColor = new Mat())
            {
                Cv2.CvtColor(thresh, threshColor, ColorConversionCodes.GRAY2BGR);
                Mat roiDraw = new Mat(drawMat, roiRect);
                Cv2.AddWeighted(roiDraw, 0.7, threshColor, 0.3, 0, roiDraw);
            }

            Cv2.FindContours(thresh, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            List<RotatedRect> holes = new List<RotatedRect>();

            foreach (var contour in contours)
            {
                double area = Cv2.ContourArea(contour);
                if (area < 1000 || area > 100000) continue;

                if (contour.Length >= 5)
                {
                    RotatedRect ellipse = Cv2.FitEllipse(contour);
                    double aspect = Math.Min(ellipse.Size.Width, ellipse.Size.Height) / Math.Max(ellipse.Size.Width, ellipse.Size.Height);
                    if (aspect > 0.5) holes.Add(ellipse);
                }
            }

            holes = holes.OrderBy(h => h.Center.X).ToList();

            if (holes.Count >= 2)
            {
                var leftHole = holes.First();
                var rightHole = holes.Last();

                Point2f cLeft = new Point2f(leftHole.Center.X + roiX, leftHole.Center.Y + roiY);
                Point2f cRight = new Point2f(rightHole.Center.X + roiX, rightHole.Center.Y + roiY);

                double distancePx = cLeft.DistanceTo(cRight);

                result.IsValid = true;
                result.MeasuredValuePx = distancePx;
                result.MeasuredValueMm = distancePx * mmPerPixel;
                result.LeftHoleDiaMm = ((leftHole.Size.Width + leftHole.Size.Height) / 2.0) * mmPerPixel;
                result.RightHoleDiaMm = ((rightHole.Size.Width + rightHole.Size.Height) / 2.0) * mmPerPixel;

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