using System;
using OpenCvSharp;

namespace MeasurementSystem
{
    public interface ICamera : IDisposable
    {
        bool Initialize();
        void StartCapture();
        void StopCapture();
        void Terminate();
        event EventHandler<Mat> OnFrameCaptured;
    }
}
