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
using Windows.UI;
using WinRT.Interop;


namespace Spiro;

public sealed partial class MainWindow : Window
{
    private readonly AppWindow? appWindow;
    private readonly Window toolWindow;

    private const int toolWindowWidth = 1024;
    private const int toolWindowHeight = 800;
    

    private const double twoPi = 2.0 * Math.PI;
    private const double halfPi = Math.PI / 2.0;


    private readonly string settingsFilePath;


    // Cancellation token to stop the background loop when window closes
    private CancellationTokenSource? renderLoopCts;
    private DispatcherQueue? uiDispatcher;

    // Ensure loop starts only once after the window is visible/activated
    private bool renderLoopStarted;


    private int frameIndex = 0;
    private int curveIndex = 0;
    private bool curveAnimationStarted = false;
    private readonly Stopwatch curveStopwatch = new();
    private double completeTrace = twoPi;


    private enum CurvePhase { Drawing, PausingBeforeErase, Erasing, PausingBeforeDraw }
    private CurvePhase curvePhase = CurvePhase.Drawing;


    private double width = 1920f;
    private double height = 1080f;
    private float centerX = 1920f / 2f;
    private float centerY = 1080f / 2f;
    private double increments = 120d;

    public Color BackgroundColor { get; set; } = Color.FromArgb(255, 28, 81, 34);
    public Color CurveColor { get; set; } = Color.FromArgb(255, 255, 252, 228);
    public float StrokeThickness { get; set; } = 3.0f;


    public CanvasControl? CanvasControlInstance { get; private set; }


    public List<Vector2[]> CurvePointsList { get; private set; } = new();
    public int CurvesToDraw { get; set; } = 5;

    public int PauseBeforeErase { get; set; } = 8;
    public int PauseBetweenRuns { get; set; } = 4;


    public double Increments
    {
        get => increments;
        set
        {
            increments = value;
            DeltaT = Math.PI / increments;
        }
    }
    public double DeltaT { get; set; } = Math.PI / 120d;

    public double ARadius { get; set; } = 222d;
    public double BRadius { get; set; } = 60d;
    public double CDistance { get; set; } = 88d;



    public MainWindow()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string dir = Path.Combine(appData, "Points");
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




        CanvasControlInstance = this.Canvas;

        MoveWindowToMonitor(this, 1);
        appWindow = GetAppWindowForWindow(this);
        appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);




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
            PrepareGeometry(canvasControl);
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

            foreach (var curvePoints in CurvePointsList)
            {
                DrawPolygon(sender, ds, curvePoints);
            }

            frameIndex += 1; // increment frame index for each draw call

            Vector2[] points = new Vector2[frameIndex + 1];

            double t = 0d;

            for (int i = 0; i < frameIndex; i++)
            {
                float x = centerX + X(t, ARadius, BRadius, CDistance);
                float y = centerY + Y(t, ARadius, BRadius, CDistance);

                points[i] = new Vector2(x, y);

                t += DeltaT;
            }

            if (t >= completeTrace)
            {
                points[frameIndex] = points[0];
            }
            else
            {
                points[frameIndex] = points[frameIndex - 1];
            }


            DrawPolygon(sender, ds, points);


            if (t >= completeTrace)
            {
                curveIndex += 1;

                if (curveIndex >= CurvesToDraw)
                {
                    curveIndex = 0;
                    CurvePointsList.Clear();
                    curvePhase = CurvePhase.PausingBeforeErase;
                }
                else
                {
                    CurvePointsList.Add(points);
                    PrepareGeometry(sender);
                }
            }

            uiDispatcher?.TryEnqueue(() => sender.Invalidate());
        }

        else if (curvePhase == CurvePhase.PausingBeforeErase)
        {
            curveStopwatch.Restart();

            //pause for a moment before erasing
            while (curveStopwatch.Elapsed.TotalSeconds < PauseBeforeErase)
            {
                // Just wait, no drawing
            }

            curvePhase = CurvePhase.Erasing;
            uiDispatcher?.TryEnqueue(() => sender.Invalidate());
        }

        else if (curvePhase == CurvePhase.Erasing)
        {
            ds.Clear(BackgroundColor);
            curvePhase = CurvePhase.PausingBeforeDraw;

            uiDispatcher?.TryEnqueue(() => sender.Invalidate());
        }

        else // PausingBeforeDraw
        {
            curveStopwatch.Restart();

            //pause for a moment before erasing
            while (curveStopwatch.Elapsed.TotalSeconds < PauseBetweenRuns)
            {
                // Just wait, no drawing
            }

            frameIndex = 0;
            curvePhase = CurvePhase.Drawing;

            uiDispatcher?.TryEnqueue(() => sender.Invalidate());
        }
    }






    private void PrepareGeometry(CanvasControl canvasControl)
    {
        Random rand = new Random();
        frameIndex = 0;

        // Prepare geometry
        width = canvasControl.ActualWidth;
        height = canvasControl.ActualHeight;

        centerX = (float)Math.Clamp(rand.NextDouble() * width, width * 0.1d, width * 0.9d);
        centerY = (float)Math.Clamp(rand.NextDouble() * height, height * 0.1d, height * 0.9d);

        completeTrace = 999d; // reset complete trace to a large value before generating new geometry

        while (completeTrace > 120d)
        {
            ARadius = Math.Clamp(rand.NextDouble() * height, height * 0.1d, height * 0.9d);
            BRadius = Math.Clamp(rand.NextDouble() * ARadius, ARadius * 0.05d, ARadius * 0.8d);
            CDistance = Math.Clamp(rand.NextDouble() * ARadius, ARadius * 0.05d, ARadius * 0.8d);

            completeTrace = twoPi * BRadius / GCD((int)ARadius, (int)BRadius);
        }
    }


    private void DrawPolygon(CanvasControl sender, CanvasDrawingSession ds, Vector2[] points)
    {
        using var pathBuilder = new CanvasPathBuilder(sender);
        pathBuilder.BeginFigure(points[0]);

        for (int i = 1; i < points.Length; i++)
        {
            pathBuilder.AddLine(points[i]);
        }

        pathBuilder.EndFigure(CanvasFigureLoop.Open);

        using var pathGeometry = CanvasGeometry.CreatePath(pathBuilder);
        ds.DrawGeometry(pathGeometry, CurveColor, StrokeThickness);
    }


    private static float X(double t, double a, double b, double c)
    {
        return (float)((a - b) * Math.Cos(t) + c * Math.Cos((a - b) / b * t));
    }

    private static float Y(double t, double a, double b, double c)
    {
        return (float)((a - b) * Math.Sin(t) - c * Math.Sin((a - b) / b * t));
    }










    private sealed class AppSettings
    {
        public uint BackgroundColorArgb { get; set; }
        public uint CurveColorArgb { get; set; }
        public float StrokeThickness { get; set; }
    }


    public void SaveSettings()
    {
        try
        {
            AppSettings s = new()
            {
                BackgroundColorArgb = ColorToUint(BackgroundColor),
                CurveColorArgb = ColorToUint(CurveColor)
            };

            JsonSerializerOptions options = new()
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            string json = JsonSerializer.Serialize(s, options);
            File.WriteAllText(settingsFilePath, json);
        }
        catch
        {
            // ignore save errors
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
                    BackgroundColor = UintToColor(s.BackgroundColorArgb);
                    CurveColor = UintToColor(s.CurveColorArgb);
                }
            }
        }
        catch
        {
            // ignore load errors
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
            winWidth = Math.Min(800, targetWidth);
            winHeight = Math.Min(600, targetHeight);
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
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

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

    private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, MONITOR_DPI_TYPE dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

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
