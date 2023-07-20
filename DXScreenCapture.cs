using System;
using DXGI = SharpDX.DXGI;
using D3D11 = SharpDX.Direct3D11;
using D2D = SharpDX.Direct2D1;
using WIC = SharpDX.WIC;
using Interop = SharpDX.Mathematics.Interop;
using SharpDX.DXGI;
using System.IO;
using System.Drawing;
using System.Threading;
using System.Diagnostics;

namespace DesktopDuplication.Demo
{
    class DXScreenCapture:IDisposable
    {
        private D3D11.Device d3dDevice;
        private OutputDuplication outputDuplication;
        private MemoryStream cachedStream;
        private D2D.Device d2dDevice;
        private D2D.DeviceContext frameDc;
        private D2D.DeviceContext textureDc;

        private Thread captureThread;
        private bool disposedValue;
        private int capture = 0;
        private int acquire = 0;
        private double scale = 0.5;
        public void Init(double scale = 0.5, int screen = 0, int device = 0)
        {
            this.scale = scale;
            var adapterIndex = device; // adapter index
            var outputIndex = screen; // output index
            using (var dxgiFactory = new DXGI.Factory1())
            using (var dxgiAdapter = dxgiFactory.GetAdapter1(adapterIndex))
            using (var output = dxgiAdapter.GetOutput(outputIndex))
            using (var dxgiOutput = output.QueryInterface<DXGI.Output1>())
            {
                this.d3dDevice = new D3D11.Device(dxgiAdapter,
#if DEBUG
                    D3D11.DeviceCreationFlags.Debug |
#endif
                    D3D11.DeviceCreationFlags.BgraSupport); // for D2D support

                outputDuplication = dxgiOutput.DuplicateOutput(this.d3dDevice);
            }

            using (var dxgiDevice = this.d3dDevice.QueryInterface<DXGI.Device>())
            using (var d2dFactory = new D2D.Factory1())
            d2dDevice = new D2D.Device(d2dFactory, dxgiDevice);
            frameDc = new D2D.DeviceContext(d2dDevice, D2D.DeviceContextOptions.EnableMultithreadedOptimizations);
            textureDc = new D2D.DeviceContext(d2dDevice, D2D.DeviceContextOptions.EnableMultithreadedOptimizations); // create a D2D device context
        }

        public Bitmap Draw()
        {
            var ratio = scale; // resize ratio
            try
            {
                outputDuplication.AcquireNextFrame(10000, out var _, out DXGI.Resource frame);
                using (frame)
                {

                    // get DXGI surface/bitmap from resource
                    using (var frameSurface = frame.QueryInterface<DXGI.Surface>())
                    using (var frameBitmap = new D2D.Bitmap1(frameDc, frameSurface))
                    {
                        // create a GPU resized texture/surface/bitmap
                        var desc = new D3D11.Texture2DDescription
                        {
                            CpuAccessFlags = D3D11.CpuAccessFlags.None, // only GPU
                            BindFlags = D3D11.BindFlags.RenderTarget, // to use D2D
                            Format = DXGI.Format.B8G8R8A8_UNorm,
                            Width = (int)(frameSurface.Description.Width * ratio),
                            Height = (int)(frameSurface.Description.Height * ratio),
                            OptionFlags = D3D11.ResourceOptionFlags.None,
                            MipLevels = 1,
                            ArraySize = 1,
                            SampleDescription = { Count = 1, Quality = 0 },
                            Usage = D3D11.ResourceUsage.Default
                        };

                        using (var texture = new D3D11.Texture2D(d3dDevice, desc))
                        using (var textureSurface = texture.QueryInterface<DXGI.Surface>()) // this texture is a DXGI surface
                        using (var textureBitmap = new D2D.Bitmap1(textureDc, textureSurface)) // we can create a GPU bitmap on a DXGI surface
                        {

                            // associate the DC with the GPU texture/surface/bitmap
                            textureDc.Target = textureBitmap;

                            // this is were we draw on the GPU texture/surface
                            textureDc.BeginDraw();

                            // this will automatically resize
                            textureDc.DrawBitmap(
                                frameBitmap,
                                new Interop.RawRectangleF(0, 0, desc.Width, desc.Height),
                                1,
                                D2D.InterpolationMode.HighQualityCubic, // change this for quality vs speed
                                null,
                                null);

                            // commit draw
                            textureDc.EndDraw();


                            try
                            {
                                outputDuplication.ReleaseFrame();
                            }
                            catch { }

                            using (var wic = new WIC.ImagingFactory2())
                            using (var bmpEncoder = new WIC.BitmapEncoder(wic, WIC.ContainerFormatGuids.Bmp))
                            {
                                if (cachedStream == null)
                                    cachedStream = new MemoryStream();
                                cachedStream.Position = 0;
                                bmpEncoder.Initialize(cachedStream);
                                using (var bmpFrame = new WIC.BitmapFrameEncode(bmpEncoder))
                                {
                                    bmpFrame.Initialize();

                                    // here we use the ImageEncoder (IWICImageEncoder)
                                    // that can write any D2D bitmap directly
                                    using (var imageEncoder = new WIC.ImageEncoder(wic, d2dDevice))
                                    {
                                        imageEncoder.WriteFrame(textureBitmap, bmpFrame, new WIC.ImageParameters(
                                            new D2D.PixelFormat(desc.Format, D2D.AlphaMode.Premultiplied),
                                            textureDc.DotsPerInch.Width,
                                            textureDc.DotsPerInch.Height,
                                            0,
                                            0,
                                            desc.Width,
                                            desc.Height));
                                    }

                                    // commit
                                    bmpFrame.Commit();
                                    bmpEncoder.Commit();
                                    return new Bitmap(cachedStream);
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return null;

        }
        
        
        public void StopCapture() => Interlocked.Exchange(ref capture,0);
        public void CaptureAuto(int targetFrameRate, Action<Bitmap> onCaptured)
        {
            // is capture active?
            if (Interlocked.CompareExchange(ref capture, 0, 0) == 1)
                return;
            //is someone else waiting thread to finish?
            if (Interlocked.CompareExchange(ref acquire, 1, 0) == 1)
                return;

            if (captureThread!=null && captureThread.IsAlive)
            {
                Interlocked.Exchange(ref capture, 0);
                captureThread.Join();
            }
            Interlocked.Exchange(ref capture, 1);
#if DEBUG
            Stopwatch sw = Stopwatch.StartNew();
            int fNum = 0;
#endif
            Stopwatch measure = Stopwatch.StartNew();
            captureThread = new Thread(() =>
            {
                while (capture == 1)
                {
                    int sleep = 1000 / targetFrameRate;
                    measure.Restart();
                    var img = Draw();
                    if (img != null)
                    {
                        onCaptured?.Invoke(img);
                        measure.Stop();

                        var procTime = measure.ElapsedMilliseconds;
                        int offset = 7;
                        sleep -= (int)procTime;
                        sleep -= offset;
                        sleep = Math.Max(1, sleep);
                    }
#if DEBUG
                    // print fps
                    fNum++;
                    if (sw.ElapsedMilliseconds > 1000)
                    {
                        Console.WriteLine("Fps:" + fNum);
                        fNum = 0;
                        sw.Restart();
                    }
#endif
                    Thread.Sleep(sleep);
                }
            });
            captureThread.Start();
            Interlocked.Exchange(ref acquire, 0);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    StopCapture();
                }

                d3dDevice?.Dispose();
                outputDuplication?.Dispose();
                d2dDevice?.Dispose();
                frameDc?.Dispose();
                textureDc?.Dispose();
                disposedValue = true;
            }
        }

        ~DXScreenCapture()
        {
            Dispose(disposing: false);
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
