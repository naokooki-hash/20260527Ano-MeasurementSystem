using System;
using System.Threading;
using System.Windows.Forms;
using OpenCvSharp;
using Teli.TeliCamAPI.NET;
using Teli.TeliCamAPI.NET.Utility;

namespace MeasurementSystem
{
    public class TeliCamera : ICamera
    {
        private static CameraSystem? sharedCamSystem = null;
        private static readonly object sysLock = new object();

        private CameraDevice? camDevice;
        private AutoResetEvent imageReceivedEvent = new AutoResetEvent(false);
        private int maxPayloadSize = 0;
        private volatile bool keepCapturing = false;
        private Thread? captureThread;

        // ★修正点1：int に戻す
        private int _cameraIndex;

        // ★修正点2：引数も int に戻す
        public TeliCamera(int cameraIndex = 0)
        {
            _cameraIndex = cameraIndex;
        }

        public event EventHandler<Mat>? OnFrameCaptured;

        public bool Initialize()
        {
            lock (sysLock)
            {
                if (sharedCamSystem == null)
                {
                    sharedCamSystem = new CameraSystem();
                    if (sharedCamSystem.Initialize(CameraType.TypeU3v | CameraType.TypeGev) != CamApiStatus.Success)
                    {
                        MessageBox.Show("カメラシステムの初期化に失敗しました。");
                        return false;
                    }
                }
            }

            int camNum;
            sharedCamSystem.GetNumOfCameras(out camNum);
            if (camNum <= _cameraIndex)
            {
                MessageBox.Show($"カメラが {_cameraIndex} 番目に見つかりません。\n認識台数: {camNum}台", "認識エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            // ★修正点3：(uint) のキャストを外し、int のまま素直に渡す
            if (sharedCamSystem.CreateDeviceObject(_cameraIndex, ref camDevice) != CamApiStatus.Success) return false;

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
            if (camDevice != null)
            {
                camDevice.camStream.Stop();
                camDevice.camStream.Close();
                camDevice.Close();
            }
        }

        public void Dispose() => Terminate();
    }
}