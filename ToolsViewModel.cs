using CommunityToolkit.Mvvm.ComponentModel;

namespace Spiro;

public partial class ToolsViewModel : ObservableObject
{
    private readonly MainWindow mainWindow;
    private float strokeThickness;
    private int curvesToDraw;
    private int pauseBeforeErase;
    private int pauseBetweenRuns;
    private double increments;

    public ToolsViewModel(MainWindow mainWindow)
    {
        this.mainWindow = mainWindow;
        strokeThickness = mainWindow.StrokeThickness;
        curvesToDraw = mainWindow.CurvesToDraw;
        pauseBeforeErase = mainWindow.PauseBeforeErase;
        pauseBetweenRuns = mainWindow.PauseBetweenRuns;
        increments = mainWindow.Increments;
    }

    public float StrokeThickness
    {
        get => strokeThickness;
        set
        {
            if (SetProperty(ref strokeThickness, value))
                mainWindow.StrokeThickness = value;
        }
    }

    public int CurvesToDraw
    {
        get => curvesToDraw;
        set
        {
            if (SetProperty(ref curvesToDraw, value))
                mainWindow.CurvesToDraw = value;
        }
    }

    public int PauseBeforeErase
    {
        get => pauseBeforeErase;
        set
        {
            if (SetProperty(ref pauseBeforeErase, value))
                mainWindow.PauseBeforeErase = value;
        }
    }

    public int PauseBetweenRuns
    {
        get => pauseBetweenRuns;
        set
        {
            if (SetProperty(ref pauseBetweenRuns, value))
                mainWindow.PauseBetweenRuns = value;
        }
    }

    public double Increments
    {
        get => increments;
        set
        {
            if (SetProperty(ref increments, value))
                mainWindow.Increments = value;
        }
    }
}
