using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace MeowTempo;

public sealed class SubdivisionPlayedEventArgs(int beatIndex, int subdivisionIndex) : EventArgs
{
    public int BeatIndex { get; } = beatIndex;

    public int SubdivisionIndex { get; } = subdivisionIndex;
}

public sealed class MetronomePlaybackService : IDisposable
{
    private readonly MetronomeState _state;
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(10) };
    private readonly MediaPlayer _diPlayer;
    private readonly MediaPlayer _doPlayer;
    private TimeSpan _nextTick;
    private int _beatIndex;
    private int _subdivisionIndex;
    private bool _disposed;

    public MetronomePlaybackService(MetronomeState state)
    {
        _state = state;
        _diPlayer = CreatePlayer("di.mp3");
        _doPlayer = CreatePlayer("do.mp3");
        _timer.Tick += Timer_Tick;
    }

    public event EventHandler<SubdivisionPlayedEventArgs>? SubdivisionPlayed;

    public bool IsPlaying => _timer.IsEnabled;

    public void Start()
    {
        ThrowIfDisposed();
        _clock.Restart();
        _beatIndex = 0;
        _subdivisionIndex = 0;
        _nextTick = TimeSpan.Zero;
        _timer.Start();
        PlayDueSubdivisions();
    }

    public void Stop()
    {
        _timer.Stop();
        _diPlayer.Pause();
        _doPlayer.Pause();
    }

    public void ResetSchedule()
    {
        if (!IsPlaying)
        {
            return;
        }

        _beatIndex = 0;
        _subdivisionIndex = 0;
        _nextTick = _clock.Elapsed;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _timer.Tick -= Timer_Tick;
        _diPlayer.Dispose();
        _doPlayer.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static MediaPlayer CreatePlayer(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
        var player = new MediaPlayer
        {
            AudioCategory = MediaPlayerAudioCategory.Media
        };
        player.Source = MediaSource.CreateFromUri(new Uri(path));
        return player;
    }

    private void Timer_Tick(object? sender, object e)
    {
        PlayDueSubdivisions();
    }

    private void PlayDueSubdivisions()
    {
        var processed = 0;
        while (IsPlaying && _clock.Elapsed >= _nextTick && processed++ < 8)
        {
            var sound = _state.GetSound(_beatIndex, _subdivisionIndex);
            Play(sound);
            SubdivisionPlayed?.Invoke(this, new SubdivisionPlayedEventArgs(_beatIndex, _subdivisionIndex));
            AdvancePosition();
            _nextTick += _state.SubdivisionInterval;
        }

        if (processed == 8 && _clock.Elapsed >= _nextTick)
        {
            _nextTick = _clock.Elapsed + _state.SubdivisionInterval;
        }
    }

    private void Play(MetronomeSound sound)
    {
        var player = sound switch
        {
            MetronomeSound.Di => _diPlayer,
            MetronomeSound.Do => _doPlayer,
            MetronomeSound.Silent => null,
            _ => throw new InvalidOperationException($"Unknown sound: {sound}.")
        };

        if (player is null)
        {
            return;
        }

        player.PlaybackSession.Position = TimeSpan.Zero;
        player.Play();
    }

    private void AdvancePosition()
    {
        _subdivisionIndex++;
        if (_subdivisionIndex < _state.SubdivisionsPerBeat)
        {
            return;
        }

        _subdivisionIndex = 0;
        _beatIndex = (_beatIndex + 1) % _state.BeatsPerMeasure;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
