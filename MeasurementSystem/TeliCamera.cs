using System;
using System.Threading;
using OpenCvSharp;
using Teli.TeliCamAPI.NET;
using Teli.TeliCamAPI.NET.Utility;

namespace MeasurementSystem
{
    public class TeliCamera : ICamera
    {
        private CameraSystem? camSystem;
        private CameraDevice? camDevice;
        private AutoResetEvent imageReceivedEvent = new AutoResetEvent(false);
        private int maxPayloadSize = 0;
        private volatile bool keepCapturing = false;
        private Thread? captureThread;

        public event EventHandler<Mat>? OnFrameCaptured;

        public bool Initialize()
        {
            camSystem = new CameraSystem();
            if (camSystem.Initialize(CameraType.TypeU3v | CameraType.TypeGev) != CamApiStatus.Success) return false;
            int camNum;
            camSystem.GetNumOfCameras(out camNum);
            if (camNum == 0) return false;
            camSystem.CreateDeviceObject(0, ref camDevice);
            if (camDevice!.Open() != CamApiStatus.Success) return false;
            if (camDevice.camStream.Open(imageReceivedEvent, 16, 0, out maxPayloadSize) != CamApiStatus.Success) return false;
            if (camDevice.camStream.Start() != CamApiStatus.Success) return false;
            return true;
        }

        public void StartCapture()
        {
            if (keepCapturing) return;
            keepCapturing = true;
            captureThread = new Thread(CaptureLoop);
            captureThread.Start();
        }

        public void StopCapture()
        {
            keepCapturing = false;
            captureThread?.Join(500);
        }

        private void CaptureLoop()
        {
            CameraImageInfo? imageInfo = null;
            int bufferIndex;
            while (keepCapturing)
            {
                if (imageReceivedEvent.WaitOne(1000))
                {
                    if (camDevice!.camStream.GetCurrentBufferIndex(out bufferIndex) == CamApiStatus.Success)
                    {
                        camDevice.camStream.LockBuffer(bufferIndex, ref imageInfo);
                        if (imageInfo != null && imageInfo.BufferPointer != IntPtr.Zero)
                        {
                            try
                            {
                                int rows = (int)imageInfo.SizeY;
                                int cols = (int)imageInfo.SizeX;
                                using (Mat colorMat = new Mat(rows, cols, MatType.CV_8UC3))
                                {
                                    // Stride（ズレ）問題を解決するため BGR24 へ変換
                                    CameraUtility.ConvertImage(DstPixelFormat.BGR24, imageInfo.PixelFormat, true, colorMat.Data, imageInfo.BufferPointer, imageInfo.SizeX, imageInfo.SizeY);
                                    Mat grayMat = new Mat();
                                    Cv2.CvtColor(colorMat, grayMat, ColorConversionCodes.BGR2GRAY);
                                    OnFrameCaptured?.Invoke(this, grayMat);
                                }
                            }
                            catch { }
                        }
                        camDevice.camStream.UnlockBuffer(bufferIndex);
                    }
                }
            }
        }

        public void Terminate()
        {
            StopCapture();
            if (camDevice != null) { camDevice.camStream.Stop(); camDevice.camStream.Close(); camDevice.Close(); }
            camSystem?.Terminate();
        }

        public void Dispose() => Terminate();
    }
}