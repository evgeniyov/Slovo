using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace Slovo.App.FlowBar;

public partial class FlowBarView : UserControl
{
    /// <summary>One spring for every change of shape, as on macOS.</summary>
    static readonly TimeSpan MorphDuration = TimeSpan.FromSeconds(0.42);
    /// <summary>How long the stage keeps its size after the bar shrinks, so the spring can settle inside it.</summary>
    public static readonly TimeSpan ShrinkDelay = TimeSpan.FromSeconds(0.45);

    const double BarHeight = 34;
    const double MinBarWidth = 120;
    static readonly Size IdleSize = new(40, 8);
    static readonly Size AwakeSize = new(58, 10);
    static readonly Size HiddenSize = new(20, 4);
    /// <summary>The idle pill is small; the area that reacts to the pointer is not.</summary>
    static readonly Size IdleHitArea = new(72, 22);
    static readonly BoxShadows BarShadow = BoxShadows.Parse("0 2 7 0 #40000000");
    static readonly BoxShadows AwakeShadow = BoxShadows.Parse("0 1 3 0 #26000000");
    static readonly TransformOperations Rest = TransformOperations.Parse("scale(1)");
    static readonly TransformOperations Tucked = TransformOperations.Parse("scale(0.88)");

    FlowBarViewModel? model;
    DispatcherTimer? shrinkTimer;
    Control[] layers = [];
    /// <summary>The layer the bar shows now; null for the idle pill and while hidden.</summary>
    Control? active;
    string? backgroundKey;
    IDisposable? backgroundBinding;
    /// <summary>The size the stage is heading for; it may still be larger while a shrink waits.</summary>
    Size stageTarget;

    public FlowBarView()
    {
        InitializeComponent();
        layers = [HintLayer, RecordingLayer, ProcessingLayer, MessageLayer];
        if (!ReducedMotion.IsOn)
        {
            var spring = new SpringEasing();
            Capsule.Transitions =
            [
                new DoubleTransition { Property = WidthProperty, Duration = MorphDuration, Easing = spring },
                new DoubleTransition { Property = HeightProperty, Duration = MorphDuration, Easing = spring },
                new CornerRadiusTransition { Property = Border.CornerRadiusProperty, Duration = MorphDuration, Easing = spring },
                new BrushTransition { Property = Border.BackgroundProperty, Duration = TimeSpan.FromSeconds(0.25) },
                new BoxShadowsTransition { Property = Border.BoxShadowProperty, Duration = TimeSpan.FromSeconds(0.3) },
                new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromSeconds(0.2) },
            ];
            foreach (var layer in layers)
            {
                layer.Transitions =
                [
                    new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromSeconds(0.2), Easing = new CubicEaseOut() },
                    new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromSeconds(0.34), Easing = spring },
                ];
            }
        }
        foreach (var layer in layers)
        {
            layer.Opacity = 0;
            layer.IsHitTestVisible = false;
            layer.RenderTransform = Tucked;
        }
        // Hover is tracked on the stage, which outlives every reshaping, so the capsule
        // growing under the pointer cannot bounce the hover state.
        Stage.PointerEntered += (_, _) => model?.SetHoveringAfterDwell(true);
        Stage.PointerExited += (_, _) => model?.SetHoveringAfterDwell(false);
        Stage.PointerPressed += OnPressed;
        DataContextChanged += (_, _) => Attach(DataContext as FlowBarViewModel);
        // Text arrives through bindings after the state changes; the bar follows the size
        // the content settles on after each layout pass.
        LayoutUpdated += (_, _) => SyncSize();
    }

    void Attach(FlowBarViewModel? next)
    {
        if (model is not null)
        {
            model.PropertyChanged -= OnModelChanged;
        }
        model = next;
        if (model is not null)
        {
            model.PropertyChanged += OnModelChanged;
        }
        UpdateShape();
    }

    void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The level changes thirty times a second and never changes the shape.
        if (e.PropertyName != nameof(FlowBarViewModel.Level))
        {
            UpdateShape();
        }
    }

    void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (model is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        switch (model.Shape)
        {
            case FlowBarShape.Idle or FlowBarShape.Hint:
                model.StartCommand.Execute(null);
                e.Handled = true;
                break;
            case FlowBarShape.Message:
                model.TapMessageCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Reshapes the capsule for the model's state; the transitions animate the change.</summary>
    void UpdateShape()
    {
        var shape = model?.Shape ?? FlowBarShape.Hidden;
        Control? content = shape switch
        {
            FlowBarShape.Hint => HintLayer,
            FlowBarShape.Recording => RecordingLayer,
            FlowBarShape.Processing => ProcessingLayer,
            FlowBarShape.Message or FlowBarShape.MeetingNotice => MessageLayer,
            _ => null,
        };
        foreach (var layer in layers)
        {
            var shown = ReferenceEquals(layer, content);
            layer.Opacity = shown ? 1 : 0;
            layer.IsHitTestVisible = shown;
            layer.RenderTransform = shown ? Rest : Tucked;
        }

        active = content;
        var awake = shape == FlowBarShape.Idle && model?.IsPointerInside == true;
        Capsule.Opacity = shape == FlowBarShape.Hidden ? 0 : 1;
        SetBackground(content is not null ? "Slovo.FlowBarBackground" : awake ? "Slovo.FlowBarAccent" : "Slovo.FlowBarIdle");
        Capsule.BoxShadow = content is not null ? BarShadow : awake ? AwakeShadow : default;
        Stage.Cursor = new Cursor(shape is FlowBarShape.Idle or FlowBarShape.Hint || (shape == FlowBarShape.Message && model?.MessageIsActionable == true)
            ? StandardCursorType.Hand : StandardCursorType.Arrow);
        content?.Measure(Size.Infinity);
        SyncSize();
    }

    /// <summary>The capsule's size for what it shows now, and the stage around it.</summary>
    void SyncSize()
    {
        var shape = model?.Shape ?? FlowBarShape.Hidden;
        Size capsule;
        if (active is not null)
        {
            capsule = new Size(Math.Max(MinBarWidth, Math.Ceiling(active.DesiredSize.Width) + 2), BarHeight);
        }
        else
        {
            var awake = shape == FlowBarShape.Idle && model?.IsPointerInside == true;
            capsule = shape == FlowBarShape.Hidden ? HiddenSize : awake ? AwakeSize : IdleSize;
        }
        if (Capsule.Width != capsule.Width || Capsule.Height != capsule.Height)
        {
            Capsule.Width = capsule.Width;
            Capsule.Height = capsule.Height;
            Capsule.CornerRadius = new CornerRadius(capsule.Height / 2);
        }
        if (shape != FlowBarShape.Hidden)
        {
            var stage = active is null ? IdleHitArea : capsule;
            if (stage != stageTarget)
            {
                stageTarget = stage;
                ResizeStage(stage);
            }
        }
    }

    /// <summary>Follows the theme's brush, so a theme change recolours the bar too.</summary>
    void SetBackground(string key)
    {
        if (key == backgroundKey)
        {
            return;
        }
        backgroundKey = key;
        backgroundBinding?.Dispose();
        backgroundBinding = Capsule.Bind(Border.BackgroundProperty, Capsule.GetResourceObservable(key));
    }

    /// <summary>Grows the stage at once; shrinks it once the capsule has settled.</summary>
    void ResizeStage(Size size)
    {
        shrinkTimer?.Stop();
        shrinkTimer = null;
        var current = new Size(double.IsNaN(Stage.Width) ? 0 : Stage.Width, double.IsNaN(Stage.Height) ? 0 : Stage.Height);
        if (size.Width >= current.Width && size.Height >= current.Height || ReducedMotion.IsOn)
        {
            Stage.Width = size.Width;
            Stage.Height = size.Height;
            return;
        }
        // Never smaller than either shape while the spring overshoots toward the new one.
        Stage.Width = Math.Max(size.Width, current.Width);
        Stage.Height = Math.Max(size.Height, current.Height);
        shrinkTimer = new DispatcherTimer { Interval = ShrinkDelay };
        shrinkTimer.Tick += (_, _) =>
        {
            shrinkTimer?.Stop();
            shrinkTimer = null;
            Stage.Width = size.Width;
            Stage.Height = size.Height;
        };
        shrinkTimer.Start();
    }

}
