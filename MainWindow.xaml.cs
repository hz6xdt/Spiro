using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using VirtualKey = Windows.System.VirtualKey;
using WinRT.Interop;


namespace Spiro;

public sealed partial class MainWindow : Window
{
    private static readonly List<MainWindow> monitorWindows = [];
    private readonly string settingsFilePath;


    private readonly Window toolWindow;

    private const int toolWindowWidth = 1024;
    private const int toolWindowHeight = 800;



    private const double twoPi = 2.0 * Math.PI;
    private const int fallbackWindowWidth = 800;
    private const int fallbackWindowHeight = 600;

    private const int frameIntervalMilliseconds = 16;
    private const double centerMinimumRatio = 0.1d;
    private const double centerMaximumRatio = 0.9d;
    private const double secondaryRadiusMinimumRatio = 0.05d;
    private const double secondaryRadiusMaximumRatio = 0.8d;
    private const double curveClosureStartFraction = 0.1d;
    private const double curveClosureTolerance = 2d;
    private const int curveClosureInitialSteps = 10;
    private const double maximumCompleteTrace = 2000d;
    private const double minimumBackgroundLuminance = 0.01d;
    private const double maximumBackgroundLuminance = 0.03d;
    private const double minimumContrastRatio = 3d;
    private const double previewWidth = 640d;
    private const double previewHeight = 360d;



    // Cancellation token to stop the background loop when window closes
    private CancellationTokenSource? renderLoopCts;
    private DispatcherQueue? uiDispatcher;

    // Ensure loop starts only once after the window is visible/activated
    private bool renderLoopStarted;



    private int frameIndex = 0;
    private int curveIndex = 0;
    private bool curveAnimationStarted = false;
    private bool skipCurrentCurve = false;
    private bool screenPrintInProgress;
    private double tDelta = Math.PI / 100d;

    private double completeTrace = twoPi;


    private readonly Stopwatch curveStopwatch = new();
    private bool pauseInvalidationScheduled;
    private readonly List<Vector2[]> pausedCurvePoints = [];
    private readonly List<Color> pausedCurveColors = [];
    private Color pausedBackgroundColor;



    private enum CurvePhase { Drawing, PausingBeforeErase, Erasing, PausingBeforeDraw }
    private CurvePhase curvePhase = CurvePhase.Drawing;


    private double width = 1920f;
    private double height = 1080f;
    private float centerX = 1920f / 2f;
    private float centerY = 1080f / 2f;

    private readonly Random rand = new();


    private Color BackgroundColor = Color.FromArgb(255, 28, 81, 34);
    private Color CurveColor = Color.FromArgb(255, 255, 252, 228);
    private readonly List<Color> curveColors = [];

    private double ARadius = 222d;
    private double BRadius = 60d;
    private double CDistance = 88d;
    private double increments = 100d;




    public CanvasControl? CanvasControlInstance { get; private set; }



    public List<Vector2[]> CurvePointsList { get; private set; } = [];
    public int CurvesToDraw { get; set; } = 3;
    public float StrokeThickness { get; set; } = 2.0f;
    public int PauseBeforeErase { get; set; } = 8;
    public int PauseBetweenRuns { get; set; } = 1;
    public double Increments
    {
        get => increments;
        set
        {
            increments = value;
            tDelta = Math.PI / increments;
        }
    }





    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };











    public MainWindow() : this(-1, true)
    {
    }

    private MainWindow(int monitorIndex, bool createWindowOnEachMonitor)
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string dir = Path.Combine(appData, "Spiro");
        if (!Directory.Exists(dir))
        {
            try
            { Directory.CreateDirectory(dir); }
            catch { /* ignore */ }
        }
        settingsFilePath = Path.Combine(dir, "settings.json");

        // load persisted settings (if any) before UI initialize so values are applied
        LoadSettings();




        InitializeComponent();
        this.Activated += MainWindow_Activated;
        Canvas.KeyDown += Canvas_KeyDown;




        CanvasControlInstance = this.Canvas;

        int monitorCount = createWindowOnEachMonitor ? GetMonitorRects().Count : 0;
        if (monitorIndex >= 0)
        {
            MoveWindowToMonitor(this, monitorIndex);
        }
        else if (monitorCount > 0)
        {
            MoveWindowToMonitor(this, 0);
        }

        AppWindow appWindow = GetAppWindowForWindow(this);
        appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

        if (createWindowOnEachMonitor)
        {
            monitorWindows.Add(this);

            for (int index = 1; index < monitorCount; index++)
            {
                MainWindow monitorWindow = new(index, false);
                monitorWindows.Add(monitorWindow);
                monitorWindow.Activate();
            }
        }




        //toolWindow = new ToolWindow(this);
        //MoveWindowToMonitorCentered(toolWindow, 0, desiredWidthDips: toolWindowWidth, desiredHeightDips: toolWindowHeight);

        //AppWindow toolAppWindow = GetAppWindowForWindow(toolWindow);
        //if (toolAppWindow.Presenter is OverlappedPresenter overlappedPresenter)
        //{
        //    overlappedPresenter.IsAlwaysOnTop = true;
        //}

        //toolWindow.Activate();

        //// Disable resizing for the tool window so it can't be resized or maximized
        //DisableWindowResize(toolWindow);
    }


    
    private void MainWindow_Activated(object? sender, WindowActivatedEventArgs args)
    {
        // Ensure activation logic runs only once (existing field used safely).
        if (renderLoopStarted)
            return;

        renderLoopStarted = true;
        renderLoopCts = new CancellationTokenSource();

        // Capture the UI Dispatcher for marshaling Invalidate calls to the UI thread
        uiDispatcher = DispatcherQueue.GetForCurrentThread();




        // Run loop on a background task so UI thread is not blocked
        _ = Task.Run(async () =>
        {
            CancellationToken token = renderLoopCts.Token;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    // Marshal the Invalidate call to the UI thread
                    uiDispatcher?.TryEnqueue(() => CanvasControlInstance?.Invalidate());

                    // Wait without blocking the UI thread
                    await Task.Delay(TimeSpan.FromSeconds(PauseBetweenRuns), token);
                }
            }

            catch (OperationCanceledException)
            {
                // expected on cancellation
            }
        });
    }










    private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is CanvasControl canvasControl)
        {
            SelectRandomCurveColors();
            PrepareGeometry(canvasControl);
            Canvas.Focus(FocusState.Programmatic);
        }
    }


    private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        CanvasDrawingSession ds = args.DrawingSession;

        // Initialize animation on first draw
        if (!curveAnimationStarted)
        {
            curveAnimationStarted = true;
            curvePhase = CurvePhase.Drawing;

            // request another frame
            uiDispatcher?.TryEnqueue(() => sender.Invalidate());
        }


        if (curvePhase == CurvePhase.Drawing)
        {
            ds.Clear(BackgroundColor);

            for (int i = 0; i < CurvePointsList.Count; i++)
            {
                DrawPolygon(sender, ds, CurvePointsList[i], GetCurveColor(i));
            }


            frameIndex += 1; // increment frame index for each draw call

            Vector2[] points = new Vector2[frameIndex];
            double t = 0d;

            for (int i = 0; i < frameIndex; i++)
            {
                float x = centerX + X(t, ARadius, BRadius, CDistance);
                float y = centerY + Y(t, ARadius, BRadius, CDistance);

                points[i] = new Vector2(x, y);

                if (t > tDelta * curveClosureInitialSteps
                    &&
                    (points[i].X == points[0].X && points[i].Y == points[0].Y
                    ||
                    (t / completeTrace > curveClosureStartFraction
                    && Math.Abs(points[i].X - points[0].X) < curveClosureTolerance
                    && Math.Abs(points[i].Y - points[0].Y) < curveClosureTolerance)))
                {
                    t = completeTrace; // stop drawing if we loop back to the start
                }
                else
                {
                    t += tDelta;
                }
            }



            DrawPolygon(sender, ds, points, GetCurveColor(curveIndex));



            if (t >= completeTrace || skipCurrentCurve)
            {
                skipCurrentCurve = false;

                PrepareGeometry(sender);

                curveIndex += 1;

                if (curveIndex >= CurvesToDraw)
                {
                    curveIndex = 0;

                    pausedBackgroundColor = BackgroundColor;
                    pausedCurvePoints.Clear();
                    pausedCurvePoints.AddRange(CurvePointsList);
                    pausedCurvePoints.Add(points);
                    pausedCurveColors.Clear();
                    pausedCurveColors.AddRange(curveColors);
                    
                    CurvePointsList.Clear();
                    curvePhase = CurvePhase.PausingBeforeErase;
                    curveStopwatch.Restart();

                    SelectRandomCurveColors();
                }
                else
                {
                    CurvePointsList.Add(points);
                }
            }

            uiDispatcher?.TryEnqueue(() => sender.Invalidate());
        }

        else if (curvePhase == CurvePhase.PausingBeforeErase)
        {
            ds.Clear(pausedBackgroundColor);

            for (int i = 0; i < pausedCurvePoints.Count; i++)
            {
                DrawPolygon(sender, ds, pausedCurvePoints[i], GetPausedCurveColor(i));
            }

            if (curveStopwatch.Elapsed.TotalSeconds < PauseBeforeErase)
            {
                SchedulePauseInvalidation(sender);
                return;
            }

            curvePhase = CurvePhase.Erasing;
            uiDispatcher?.TryEnqueue(() => sender.Invalidate());
        }

        else if (curvePhase == CurvePhase.Erasing)
        {
            ds.Clear(pausedBackgroundColor);

            pausedCurvePoints.Clear();
            pausedCurveColors.Clear();
            
            curvePhase = CurvePhase.PausingBeforeDraw;
            curveStopwatch.Restart();

            uiDispatcher?.TryEnqueue(() => sender.Invalidate());
        }

        else // PausingBeforeDraw
        {
            ds.Clear(pausedBackgroundColor);
            if (curveStopwatch.Elapsed.TotalSeconds < PauseBetweenRuns)
            {
                SchedulePauseInvalidation(sender);
                return;
            }

            curvePhase = CurvePhase.Drawing;

            uiDispatcher?.TryEnqueue(() => sender.Invalidate());
        }
    }

    private void SchedulePauseInvalidation(CanvasControl sender)
    {
        if (pauseInvalidationScheduled)
        {
            return;
        }

        pauseInvalidationScheduled = true;

        _ = InvalidateAfterPauseAsync(sender);
    }

    private async Task InvalidateAfterPauseAsync(CanvasControl sender)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(frameIntervalMilliseconds));
        pauseInvalidationScheduled = false;
        uiDispatcher?.TryEnqueue(sender.Invalidate);
    }











    private void PrepareGeometry(CanvasControl canvasControl)
    {
        frameIndex = 0;

        width = canvasControl.ActualWidth;
        height = canvasControl.ActualHeight;

        do
        {
            centerX = (float)Math.Clamp(rand.NextDouble() * width, width * centerMinimumRatio, width * centerMaximumRatio);
            centerY = (float)Math.Clamp(rand.NextDouble() * height, height * centerMinimumRatio, height * centerMaximumRatio);

            ARadius = Math.Clamp(rand.NextDouble() * height, height * centerMinimumRatio, height * centerMaximumRatio);
            BRadius = Math.Clamp(rand.NextDouble() * ARadius, ARadius * secondaryRadiusMinimumRatio, ARadius * secondaryRadiusMaximumRatio);
            CDistance = Math.Clamp(rand.NextDouble() * ARadius, ARadius * secondaryRadiusMinimumRatio, ARadius * secondaryRadiusMaximumRatio);

            completeTrace = twoPi * BRadius / GCD((int)ARadius, (int)BRadius);
        }
        while (completeTrace > maximumCompleteTrace);
    }




    private Color GetCurveColor(int index)
    {
        return index >= 0 && index < curveColors.Count ? curveColors[index] : CurveColor;
    }

    private Color GetPausedCurveColor(int index)
    {
        return index >= 0 && index < pausedCurveColors.Count ? pausedCurveColors[index] : CurveColor;
    }

    private void SelectRandomCurveColors()
    {
        do
        {
            BackgroundColor = Color.FromArgb(255, (byte)rand.Next(256), (byte)rand.Next(256), (byte)rand.Next(256));
        }
        while (RelativeLuminance(BackgroundColor) > maximumBackgroundLuminance || RelativeLuminance(BackgroundColor) < minimumBackgroundLuminance);

        curveColors.Clear();

        for (int i = 0; i < Math.Max(0, CurvesToDraw); i++)
        {
            Color color;
            do
            {
                color = Color.FromArgb(255, (byte)rand.Next(256), (byte)rand.Next(256), (byte)rand.Next(256));
            }
            while (ContrastRatio(BackgroundColor, color) < minimumContrastRatio ||
                   //IsGreenOrBlueGreen(color) ||
                   curveColors.Contains(color));

            curveColors.Add(color);
        }
    }


    private static double ContrastRatio(Color background, Color foreground)
    {
        double backgroundLuminance = RelativeLuminance(background);
        double foregroundLuminance = RelativeLuminance(foreground);
        double lighter = Math.Max(backgroundLuminance, foregroundLuminance);
        double darker = Math.Min(backgroundLuminance, foregroundLuminance);

        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linearize(byte channel)
        {
            double value = channel / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linearize(color.R) + 0.7152 * Linearize(color.G) + 0.0722 * Linearize(color.B);
    }











    private void DrawPolygon(CanvasControl sender, CanvasDrawingSession ds, Vector2[] points, Color curveColor)
    {
        using var pathBuilder = new CanvasPathBuilder(sender);
        pathBuilder.BeginFigure(points[0]);

        for (int i = 1; i < points.Length; i++)
        {
            pathBuilder.AddLine(points[i]);
        }

        pathBuilder.EndFigure(CanvasFigureLoop.Open);

        using var pathGeometry = CanvasGeometry.CreatePath(pathBuilder);
        ds.DrawGeometry(pathGeometry, curveColor, StrokeThickness);
    }


    private static float X(double t, double a, double b, double c)
    {
        return (float)((a - b) * Math.Cos(t) + c * Math.Cos((a - b) / b * t));
    }

    private static float Y(double t, double a, double b, double c)
    {
        return (float)((a - b) * Math.Sin(t) - c * Math.Sin((a - b) / b * t));
    }







    private async void Canvas_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if ((curvePhase == CurvePhase.Drawing || curvePhase == CurvePhase.PausingBeforeErase)
            && !e.KeyStatus.WasKeyDown)
        {
            if (e.Key == VirtualKey.Right || e.Key == VirtualKey.N)
            {
                e.Handled = true;
                skipCurrentCurve = true;
            }
            else if (e.Key == VirtualKey.Down || e.Key == VirtualKey.S)
            {
                e.Handled = true;
                await SaveScreenPrintAsync();
            }
        }
    }








    private sealed record ScreenPrint(byte[] Pixels, uint PixelWidth, uint PixelHeight);

    private async Task<ScreenPrint> CaptureScreenPrintAsync()
    {
        RenderTargetBitmap renderTargetBitmap = new();
        await renderTargetBitmap.RenderAsync(Canvas);

        IBuffer pixels = await renderTargetBitmap.GetPixelsAsync();
        byte[] pixelBytes = new byte[pixels.Length];
        using (DataReader reader = DataReader.FromBuffer(pixels))
        {
            reader.ReadBytes(pixelBytes);
        }

        return new ScreenPrint(pixelBytes, (uint)renderTargetBitmap.PixelWidth, (uint)renderTargetBitmap.PixelHeight);
    }

    private async Task SaveScreenPrintAsync()
    {
        if (screenPrintInProgress)
        {
            return;
        }

        screenPrintInProgress = true;

        try
        {
            ScreenPrint screenPrint = await CaptureScreenPrintAsync();
            using InMemoryRandomAccessStream previewStream = await CreatePngStreamAsync(screenPrint);

            BitmapImage thumbnail = new();
            await thumbnail.SetSourceAsync(previewStream);

            Image previewImage = new()
            {
                Source = thumbnail,
                Width = previewWidth,
                Height = previewHeight,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform
            };

            ContentDialog dialog = new()
            {
                Title = "Save drawing",
                Content = previewImage,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Canvas.XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            FileSavePicker picker = new()
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = $"spiro_{DateTime.Now:yyyyMMdd_HHmmss}"
            };
            picker.FileTypeChoices.Add("PNG image", [".png"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is not null)
            {
                await SavePngAsync(file, screenPrint);
            }
        }
        finally
        {
            screenPrintInProgress = false;
        }
    }

    private static async Task<InMemoryRandomAccessStream> CreatePngStreamAsync(ScreenPrint screenPrint)
    {
        InMemoryRandomAccessStream stream = new();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            screenPrint.PixelWidth,
            screenPrint.PixelHeight,
            96,
            96,
            screenPrint.Pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        return stream;
    }

    private static async Task SavePngAsync(StorageFile file, ScreenPrint screenPrint)
    {
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            screenPrint.PixelWidth,
            screenPrint.PixelHeight,
            96,
            96,
            screenPrint.Pixels);
        await encoder.FlushAsync();
    }


















    private sealed class AppSettings
    {
        public float StrokeThickness { get; set; } = 2.0f;
        public int CurvesToDraw { get; set; } = 3;
        public int PauseBeforeErase { get; set; } = 8;
        public int PauseBetweenRuns { get; set; } = 1;
        public double Increments { get; set; } = 100d;
    }


    public void SaveSettings()
    {
        try
        {
            AppSettings s = new()
            {
                StrokeThickness = StrokeThickness,
                CurvesToDraw = CurvesToDraw,
                PauseBeforeErase = PauseBeforeErase,
                PauseBetweenRuns = PauseBetweenRuns,
                Increments = Increments
            };

            string json = JsonSerializer.Serialize(s, JsonOptions);
            File.WriteAllText(settingsFilePath, json);
        }
        catch (Exception x)
        {
            Debug.WriteLine($"Error saving settings: {x.ToString()}");
        }
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(settingsFilePath))
            {
                string json = File.ReadAllText(settingsFilePath);
                AppSettings? s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s != null)
                {
                    StrokeThickness = Math.Clamp(s.StrokeThickness, 1.0f, 20.0f);
                    CurvesToDraw = (int)Math.Clamp(s.CurvesToDraw, 1.0d, 10.0d);
                    PauseBeforeErase = Math.Clamp(s.PauseBeforeErase, 0, 60);
                    PauseBetweenRuns = Math.Clamp(s.PauseBetweenRuns, 0, 10);
                    Increments = Math.Clamp(s.Increments, 20d, 500d);
                }
            }
        }
        catch (Exception x)
        {
            Debug.WriteLine($"Error loading settings: {x.ToString()}");
        }
    }



    private static uint ColorToUint(Color c) => (uint)((c.A << 24) | (c.R << 16) | (c.G << 8) | c.B);

    private static Color UintToColor(uint v) => Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);







    private static AppWindow GetAppWindowForWindow(Window window)
    {
        IntPtr hWnd = WindowNative.GetWindowHandle(window);
        WindowId myWndId = Win32Interop.GetWindowIdFromWindow(hWnd);
        return AppWindow.GetFromWindowId(myWndId);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        skipCurrentCurve = true;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        await SaveScreenPrintAsync();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        //toolWindow.Close();
        this.Close();
    }



    private void Window_Closed(object sender, WindowEventArgs args)
    {
        // Save settings on exit
        SaveSettings();

        // Cancel the background loop and clean up the CanvasControl
        if (renderLoopCts != null && !renderLoopCts.IsCancellationRequested)
        {
            renderLoopCts.Cancel();
            renderLoopCts.Dispose();
            renderLoopCts = null;
        }

        this.CanvasControlInstance?.RemoveFromVisualTree();
        this.CanvasControlInstance = null;
    }







    private static double GCD(int a, int b)
    {
        a = Math.Abs(a);
        b = Math.Abs(b);

        bool finished = false;

        while (!finished)
        {
            int remainder = a % b;

            if (remainder == 0)
            {
                finished = true;
                continue;
            }

            a = b;
            b = remainder;
        }

        return b;
    }

















    // --- Monitor / window placement helpers ---

    private static void MoveWindowToMonitor(Window window, int monitorIndex)
    {
        if (window is null)
        {
            return;
        }

        IntPtr hwnd = WindowNative.GetWindowHandle(window);

        List<RECT> monitors = GetMonitorRects();
        if (monitorIndex < 0 || monitorIndex >= monitors.Count)
        {
            return; // index out of range, do nothing
        }

        RECT target = monitors[monitorIndex];

        // Move window origin to top-left of monitor. Preserve window size.
        // Use SetWindowPos with SWP_NOSIZE to keep current size.
        SetWindowPos(hwnd, IntPtr.Zero, target.Left, target.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_SHOWWINDOW);
    }


    private static List<RECT> GetMonitorRects()
    {
        List<RECT> list = [];

        bool callback(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData)
        {
            // Copy the monitor rect passed from EnumDisplayMonitors
            list.Add(lprcMonitor);
            return true;
        }

        // EnumDisplayMonitors marshals callback as MonitorEnumProc
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        
        return list;
    }

    /// <summary>
    /// Move and optionally resize the <paramref name="window"/> so it is centered on the specified monitor.
    /// If desiredWidthDips/desiredHeightDips are provided (>0) they are treated as device-independent pixels (DIPs)
    /// and converted to physical pixels using the monitor DPI.
    /// </summary>
    private static void MoveWindowToMonitorCentered(Window window, int monitorIndex, int desiredWidthDips = 0, int desiredHeightDips = 0)
    {
        if (window is null)
        {
            return;
        }

        IntPtr hwnd = WindowNative.GetWindowHandle(window);

        List<MonitorInfo> monitors = GetMonitorInfos();
        if (monitorIndex < 0 || monitorIndex >= monitors.Count)
        {
            return; // invalid monitor index
        }

        MonitorInfo monitor = monitors[monitorIndex];
        RECT target = monitor.Rect;
        int targetWidth = target.Right - target.Left;
        int targetHeight = target.Bottom - target.Top;

        // Determine final window size in physical pixels.
        int winWidth;
        int winHeight;

        if (desiredWidthDips > 0 && desiredHeightDips > 0)
        {
            // Convert requested DIPs to physical pixels using monitor DPI (dpiX/dpiY)
            uint dpiX = monitor.DpiX != 0 ? monitor.DpiX : 96;
            uint dpiY = monitor.DpiY != 0 ? monitor.DpiY : 96;
            winWidth = (int)Math.Max(1, Math.Round(desiredWidthDips * dpiX / 96.0));
            winHeight = (int)Math.Max(1, Math.Round(desiredHeightDips * dpiY / 96.0));
        }
        else if (GetWindowRect(hwnd, out RECT wr))
        {
            winWidth = wr.Right - wr.Left;
            winHeight = wr.Bottom - wr.Top;
        }
        else
        {
            // fallback default in pixels
            winWidth = Math.Min(fallbackWindowWidth, targetWidth);
            winHeight = Math.Min(fallbackWindowHeight, targetHeight);
        }

        // Clamp to monitor size
        winWidth = Math.Min(winWidth, targetWidth);
        winHeight = Math.Min(winHeight, targetHeight);

        // Compute centered origin inside target monitor
        int newX = target.Left + Math.Max(0, (targetWidth - winWidth) / 2);
        int newY = target.Top + Math.Max(0, (targetHeight - winHeight) / 2);

        // Move and resize window to computed origin/size.
        // Use SetWindowPos without SWP_NOSIZE so size is applied.
        SetWindowPos(hwnd, IntPtr.Zero, newX, newY, winWidth, winHeight, SWP_NOZORDER | SWP_SHOWWINDOW);
    }

    /// <summary>
    /// Removes resizing styles so the specified window cannot be resized or maximized.
    /// Call this after the native handle is available (after Activate()).
    /// </summary>
    private static void DisableWindowResize(Window window)
    {
        if (window is null)
        {
            return;
        }

        IntPtr hwnd = WindowNative.GetWindowHandle(window);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        const int GWL_STYLE = -16;
        const uint WS_THICKFRAME = 0x00040000;
        const uint WS_MAXIMIZEBOX = 0x00010000;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_FRAMECHANGED = 0x0020;

        if (IntPtr.Size == 8)
        {
            // x64
            IntPtr stylePtr = GetWindowLongPtr(hwnd, GWL_STYLE);
            long style = stylePtr.ToInt64();
            style &= ~(WS_THICKFRAME | (long)WS_MAXIMIZEBOX);
            _ = SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));
        }
        else
        {
            // x86
            int style = GetWindowLong(hwnd, GWL_STYLE);
            style &= ~((int)WS_THICKFRAME | (int)WS_MAXIMIZEBOX);
            _ = SetWindowLong(hwnd, GWL_STYLE, style);
        }

        // Force a frame update so the change takes effect immediately
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    // P/Invoke helpers for style manipulation
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static partial int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private struct MonitorInfo
    {
        public IntPtr HMonitor;
        public RECT Rect;
        public uint DpiX;
        public uint DpiY;
    }

    private static List<MonitorInfo> GetMonitorInfos()
    {
        List<MonitorInfo> list = [];
        bool callback(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData)
        {
            MonitorInfo info = new()
            {
                HMonitor = hMonitor,
                Rect = lprcMonitor,
                DpiX = 0,
                DpiY = 0
            };

            // Try to get DPI for this monitor (Shcore.dll). If it fails, DpiX/DpiY remain 0 -> fallback to 96 later.
            try
            {
                if (GetDpiForMonitor(hMonitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY) == 0)
                {
                    info.DpiX = dpiX;
                    info.DpiY = dpiY;
                }
            }
            catch
            {
                // ignore - we'll fallback to 96 DPI
            }

            list.Add(info);
            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return list;
    }

    private enum MONITOR_DPI_TYPE
    {
        MDT_EFFECTIVE_DPI = 0,
        MDT_ANGULAR_DPI = 1,
        MDT_RAW_DPI = 2,
        MDT_DEFAULT = MDT_EFFECTIVE_DPI
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

    [LibraryImport("Shcore.dll")]
    private static partial int GetDpiForMonitor(IntPtr hmonitor, MONITOR_DPI_TYPE dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
