using System;
using System.Collections.Generic;
using System.Diagnostics;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace MeowTempo;

public sealed partial class MainWindow : Window
{
    private const double BpmDragStepPixels = 12;
    private static readonly SolidColorBrush BeatGreenBrush = new(Color.FromArgb(255, 151, 255, 32));
    private static readonly SolidColorBrush BeatForegroundBrush = new(Color.FromArgb(255, 77, 124, 22));
    private static readonly SolidColorBrush SilentBeatBrush = new(Color.FromArgb(255, 117, 117, 117));
    private static readonly SolidColorBrush ActiveBeatBrush = new(Color.FromArgb(255, 215, 215, 215));
    private readonly MetronomeState _metronome = new();
    private readonly MetronomeStateStorage _stateStorage = new();
    private readonly DispatcherTimer _bpmHoldTimer = new();
    private readonly List<long> _tapTimestamps = [];
    private readonly List<Button> _beatButtons = [];
    private readonly MetronomePlaybackService _playback;
    private int _bpmHoldDelta;
    private bool _bpmHoldRepeating;
    private bool _suppressNextBpmClick;
    private uint? _dragPointerId;
    private Point _lastDragPosition;
    private double _dragRemainder;

    public MainWindow()
    {
        InitializeComponent();
        RestoreState();
        UpdateWindowBounds();
        AppWindow.Changed += AppWindow_Changed;

        _bpmHoldTimer.Tick += BpmHoldTimer_Tick;

        // Button marks pointer events as Handled internally, so XAML-attached
        // handlers never fire. Register with handledEventsToo to enable long-press.
        foreach (var bpmButton in new[] { BpmDecreaseButton, BpmIncreaseButton })
        {
            bpmButton.AddHandler(UIElement.PointerPressedEvent,
                new PointerEventHandler(BpmButton_PointerPressed), handledEventsToo: true);
            bpmButton.AddHandler(UIElement.PointerReleasedEvent,
                new PointerEventHandler(BpmButton_PointerReleased), handledEventsToo: true);
            bpmButton.AddHandler(UIElement.PointerCanceledEvent,
                new PointerEventHandler(BpmButton_PointerCanceled), handledEventsToo: true);
        }

        _playback = new MetronomePlaybackService(_metronome);
        _playback.SubdivisionPlayed += Playback_SubdivisionPlayed;
        Closed += (_, _) =>
        {
            AppWindow.Changed -= AppWindow_Changed;
            _stateStorage.Save(_metronome.CreateSnapshot());
            _playback.Dispose();
        };

        UpdateBpmDisplay();
        TimeSignatureText.Text = $"{_metronome.BeatsPerMeasure}/4";
        UpdateBeatIndicators();
        UpdateSubdivisionIcon();
    }

    private void RestoreState()
    {
        var snapshot = _stateStorage.Load();
        if (snapshot is null)
        {
            return;
        }

        try
        {
            _metronome.Restore(snapshot);
        }
        catch (ArgumentException)
        {
            return;
        }

        if (_metronome.WindowWidth > 0 && _metronome.WindowHeight > 0)
        {
            AppWindow.Resize(new SizeInt32(_metronome.WindowWidth, _metronome.WindowHeight));
        }

        if (_metronome.WindowX is int windowX && _metronome.WindowY is int windowY)
        {
            AppWindow.Move(new PointInt32(windowX, windowY));
        }
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange || args.DidPositionChange)
        {
            UpdateWindowBounds();
        }
    }

    private void UpdateWindowBounds()
    {
        var size = AppWindow.Size;
        _metronome.SetWindowSize(size.Width, size.Height);

        var position = AppWindow.Position;
        _metronome.SetWindowPosition(position.X, position.Y);
    }

    private void BpmButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button button || !int.TryParse(button.Tag?.ToString(), out _bpmHoldDelta))
        {
            return;
        }

        button.CapturePointer(e.Pointer);
        _suppressNextBpmClick = false;
        _bpmHoldRepeating = false;
        _bpmHoldTimer.Interval = TimeSpan.FromMilliseconds(450);
        _bpmHoldTimer.Start();
    }

    private void BpmButton_Click(object sender, RoutedEventArgs e)
    {
        if (_bpmHoldRepeating)
        {
            _suppressNextBpmClick = true;
            return;
        }

        if (_suppressNextBpmClick)
        {
            _suppressNextBpmClick = false;
            return;
        }

        if (sender is Button { Tag: not null } button && int.TryParse(button.Tag.ToString(), out var delta))
        {
            _metronome.AdjustBpm(delta);
            UpdateBpmDisplay();
        }
    }

    private void BpmButton_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        StopBpmHold(sender as UIElement, suppressClick: true);
    }

    private void BpmButton_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        StopBpmHold(sender as UIElement, suppressClick: false);
    }

    private void BpmHoldTimer_Tick(object? sender, object e)
    {
        if (!_bpmHoldRepeating)
        {
            _bpmHoldRepeating = true;
            _bpmHoldTimer.Interval = TimeSpan.FromMilliseconds(120);
        }

        _metronome.AdjustBpm(_bpmHoldDelta * 10);
        UpdateBpmDisplay();
    }

    private void StopBpmHold(UIElement? element, bool suppressClick)
    {
        element?.ReleasePointerCaptures();
        _bpmHoldTimer.Stop();
        _suppressNextBpmClick = suppressClick && _bpmHoldRepeating;
        _bpmHoldRepeating = false;
        _bpmHoldDelta = 0;
    }

    private void BpmDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragPointerId = e.Pointer.PointerId;
        _lastDragPosition = e.GetCurrentPoint(BpmDragArea).Position;
        _dragRemainder = 0;
        BpmDragArea.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void BpmDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointerId != e.Pointer.PointerId)
        {
            return;
        }

        var position = e.GetCurrentPoint(BpmDragArea).Position;
        _dragRemainder += (position.X - _lastDragPosition.X) - (position.Y - _lastDragPosition.Y);
        _lastDragPosition = position;

        var bpmDelta = (int)(_dragRemainder / BpmDragStepPixels);
        if (bpmDelta == 0)
        {
            return;
        }

        _dragRemainder -= bpmDelta * BpmDragStepPixels;
        _metronome.AdjustBpm(bpmDelta);
        UpdateBpmDisplay();
        e.Handled = true;
    }

    private void BpmDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        StopBpmDrag();
        e.Handled = true;
    }

    private void BpmDragArea_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        StopBpmDrag();
        e.Handled = true;
    }

    private void StopBpmDrag()
    {
        BpmDragArea.ReleasePointerCaptures();
        _dragPointerId = null;
        _dragRemainder = 0;
    }

    private void TapButton_Click(object sender, RoutedEventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        if (_tapTimestamps.Count > 0 && now - _tapTimestamps[^1] > Stopwatch.Frequency * 5)
        {
            _tapTimestamps.Clear();
        }

        _tapTimestamps.Add(now);
        if (_tapTimestamps.Count < 2)
        {
            return;
        }

        var totalIntervals = _tapTimestamps[^1] - _tapTimestamps[0];
        var averageInterval = totalIntervals / (double)(_tapTimestamps.Count - 1);
        _metronome.SetBpm((int)Math.Round(60 * Stopwatch.Frequency / averageInterval));
        UpdateBpmDisplay();
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_playback.IsPlaying)
        {
            _playback.Stop();
            PlayPauseIcon.Glyph = "\uE768";
            AutomationProperties.SetName(PlayPauseButton, "開始播放節拍器");
            UpdateBeatIndicators();
            return;
        }

        _playback.Start();
        PlayPauseIcon.Glyph = "\uE71A";
        AutomationProperties.SetName(PlayPauseButton, "停止節拍器");
    }

    private void Playback_SubdivisionPlayed(object? sender, SubdivisionPlayedEventArgs e)
    {
        if (e.SubdivisionIndex != 0)
        {
            return;
        }

        UpdateBeatIndicators(e.BeatIndex);
    }

    private void BeatButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !int.TryParse(button.Tag?.ToString(), out var beatIndex))
        {
            return;
        }

        _metronome.CycleBeatType(beatIndex);
        _playback.ResetSchedule();
        UpdateBeatIndicators();
    }

    private static void BeatButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button)
        {
            AnimateBeatButtonScale(button, 1.2);
        }
    }

    private static void BeatButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button)
        {
            AnimateBeatButtonScale(button, 1);
        }
    }

    private static void AnimateBeatButtonScale(Button button, double scale)
    {
        var transform = (ScaleTransform)button.RenderTransform;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(140));
        var storyboard = new Storyboard();

        foreach (var property in new[] { nameof(ScaleTransform.ScaleX), nameof(ScaleTransform.ScaleY) })
        {
            var animation = new DoubleAnimation
            {
                To = scale,
                Duration = duration,
                EasingFunction = easing,
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(animation, transform);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }

        storyboard.Begin();
    }

    private void TimeSignatureButton_Click(object sender, RoutedEventArgs e)
    {
        TimeSignatureTeachingTip.IsOpen = true;
    }

    private void TimeSignatureOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: not null } button && int.TryParse(button.Tag.ToString(), out var beatsPerMeasure))
        {
            _metronome.SetTimeSignature(beatsPerMeasure);
            TimeSignatureText.Text = $"{beatsPerMeasure}/4";
            _playback.ResetSchedule();
            UpdateBeatIndicators();
        }

        TimeSignatureTeachingTip.IsOpen = false;
    }

    private void SubdivisionButton_Click(object sender, RoutedEventArgs e)
    {
        SubdivisionTeachingTip.IsOpen = true;
    }

    private void SubdivisionOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<BeatSubdivision>(tag, out var subdivision))
        {
            _metronome.SetSubdivision(subdivision);
            _playback.ResetSchedule();
            UpdateSubdivisionIcon();
        }

        SubdivisionTeachingTip.IsOpen = false;
    }

    private void UpdateBpmDisplay()
    {
        BpmNumber.Text = _metronome.Bpm.ToString();
        _playback.SyncState();
    }

    private void UpdateSubdivisionIcon()
    {
        if (SubdivisionButton.Content is not TextBlock icon)
        {
            return;
        }

        icon.Text = _metronome.Subdivision switch
        {
            BeatSubdivision.Quarter => "♩",
            BeatSubdivision.Eighth => "♪",
            BeatSubdivision.EighthTriplet => "♪³",
            _ => "♩"
        };
    }

    private void UpdateBeatIndicators(int? activeBeatIndex = null)
    {
        EnsureBeatButtons();

        for (var index = 0; index < _beatButtons.Count; index++)
        {
            var button = _beatButtons[index];
            var beatType = _metronome.BeatTypes[index];
            var isSilent = beatType == BeatType.Silent;
            button.Content = beatType switch
            {
                BeatType.Accent => "⌃",
                BeatType.SecondaryAccent => ">",
                BeatType.Normal => " ",
                BeatType.Silent => " ",
                _ => string.Empty
            };
            button.FontSize = 28;
            button.Foreground = BeatForegroundBrush;
            button.Background = activeBeatIndex == index ? ActiveBeatBrush : isSilent ? SilentBeatBrush : BeatGreenBrush;
            button.BorderBrush = BeatGreenBrush;
            button.BorderThickness = isSilent ? new Thickness(3) : new Thickness(1);
            button.Resources["ButtonBackgroundPointerOver"] = button.Background;
            button.Resources["ButtonBorderBrushPointerOver"] = button.BorderBrush;
            button.Resources["ButtonBackgroundPressed"] = button.Background;
            button.Resources["ButtonBorderBrushPressed"] = button.BorderBrush;
            AutomationProperties.SetName(button, $"第 {index + 1} 拍：{GetBeatTypeName(beatType)}");
        }
    }

    private void EnsureBeatButtons()
    {
        if (_beatButtons.Count == _metronome.BeatsPerMeasure)
        {
            return;
        }

        BeatIndicatorHost.Children.Clear();
        _beatButtons.Clear();

        for (var index = 0; index < _metronome.BeatsPerMeasure; index++)
        {
            var button = new Button
            {
                Width = 60,
                Height = 60,
                Padding = new Thickness(0),
                Tag = index,
                BorderBrush = BeatGreenBrush,
                CornerRadius = new CornerRadius(38),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform()
            };
            button.Click += BeatButton_Click;
            button.PointerEntered += BeatButton_PointerEntered;
            button.PointerExited += BeatButton_PointerExited;
            BeatIndicatorHost.Children.Add(button);
            _beatButtons.Add(button);
        }
    }

    private static string GetBeatTypeName(BeatType beatType) => beatType switch
    {
        BeatType.Accent => "重拍",
        BeatType.SecondaryAccent => "次重拍",
        BeatType.Normal => "一般",
        BeatType.Silent => "不播放",
        _ => string.Empty
    };
}