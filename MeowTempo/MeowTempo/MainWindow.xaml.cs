using System;
using System.Collections.Generic;
using System.Diagnostics;
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MeowTempo;

public sealed partial class MainWindow : Window
{
    private const double BpmDragStepPixels = 12;
    private static readonly SolidColorBrush BeatGreenBrush = new(Color.FromArgb(255, 151, 255, 32));
    private static readonly SolidColorBrush BeatForegroundBrush = new(Color.FromArgb(255, 77, 124, 22));
    private static readonly SolidColorBrush SilentBeatBrush = new(Color.FromArgb(255, 117, 117, 117));
    private static readonly SolidColorBrush ActiveBeatBrush = new(Color.FromArgb(255, 215, 215, 215));
    private readonly MetronomeState _metronome = new();
    private readonly DispatcherTimer _bpmHoldTimer = new();
    private readonly List<long> _tapTimestamps = [];
    private readonly List<Button> _beatButtons = [];
    private readonly MetronomePlaybackService _playback;
    private int _bpmHoldDelta;
    private bool _bpmHoldRepeating;
    private uint? _dragPointerId;
    private Point _lastDragPosition;
    private double _dragRemainder;

    public MainWindow()
    {
        InitializeComponent();

        _bpmHoldTimer.Tick += BpmHoldTimer_Tick;
        _playback = new MetronomePlaybackService(_metronome);
        _playback.SubdivisionPlayed += Playback_SubdivisionPlayed;
        Closed += (_, _) => _playback.Dispose();

        UpdateBpmDisplay();
        UpdateBeatIndicators();
    }

    private void BpmButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button button || !int.TryParse(button.Tag?.ToString(), out _bpmHoldDelta))
        {
            return;
        }

        button.CapturePointer(e.Pointer);
        _metronome.AdjustBpm(_bpmHoldDelta);
        UpdateBpmDisplay();

        _bpmHoldRepeating = false;
        _bpmHoldTimer.Interval = TimeSpan.FromMilliseconds(450);
        _bpmHoldTimer.Start();
        e.Handled = true;
    }

    private void BpmButton_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        StopBpmHold(sender as UIElement);
        e.Handled = true;
    }

    private void BpmButton_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        StopBpmHold(sender as UIElement);
        e.Handled = true;
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

    private void StopBpmHold(UIElement? element)
    {
        element?.ReleasePointerCaptures();
        _bpmHoldTimer.Stop();
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
        }

        SubdivisionTeachingTip.IsOpen = false;
    }

    private void UpdateBpmDisplay()
    {
        BpmNumber.Text = _metronome.Bpm.ToString();
        _playback.SyncState();
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
                CornerRadius = new CornerRadius(38)
            };
            button.Click += BeatButton_Click;
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