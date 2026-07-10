using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Render;
using Windows.Storage;
using Microsoft.UI.Dispatching;
using WinRT;

namespace MeowTempo;

public sealed class SubdivisionPlayedEventArgs(int beatIndex, int subdivisionIndex) : EventArgs
{
    public int BeatIndex { get; } = beatIndex;

    public int SubdivisionIndex { get; } = subdivisionIndex;
}

public sealed class MetronomePlaybackService : IDisposable
{
    private readonly MetronomeState _state;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly List<Voice> _voices = new(8);

    private AudioGraph? _graph;
    private AudioFrameInputNode? _frameInputNode;
    private AudioDeviceOutputNode? _deviceOutputNode;

    // Decoded click samples, mono, at the graph's sample rate.
    private float[] _diSamples = [];
    private float[] _doSamples = [];
    private int _sampleRate;
    private int _channelCount;

    // Scheduling state — mutated only on the audio render thread.
    private long _framePosition;
    private double _nextTickFrame;
    private int _beatIndex;
    private int _subdivisionIndex;

    // Control flags — written on the UI thread, read/cleared on the audio thread.
    private volatile PlaybackSnapshot? _snapshot;
    private volatile bool _running;
    private volatile bool _restartRequested;
    private volatile bool _flushRequested;
    private bool _disposed;

    public MetronomePlaybackService(MetronomeState state)
    {
        _state = state;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _ = InitializeAsync();
    }

    public event EventHandler<SubdivisionPlayedEventArgs>? SubdivisionPlayed;

    public bool IsPlaying => _running;

    public void Start()
    {
        ThrowIfDisposed();
        PublishSnapshot();
        _restartRequested = true;
        _running = true;
    }

    public void Stop()
    {
        _running = false;
        _flushRequested = true;
    }

    public void ResetSchedule()
    {
        PublishSnapshot();
        if (_running)
        {
            _restartRequested = true;
        }
    }

    /// <summary>Republishes the current tempo/beat state so live BPM changes take effect immediately.</summary>
    public void SyncState()
    {
        PublishSnapshot();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _running = false;

        if (_frameInputNode is not null)
        {
            _frameInputNode.QuantumStarted -= FrameInputNode_QuantumStarted;
        }

        _graph?.Stop();
        _frameInputNode?.Dispose();
        _deviceOutputNode?.Dispose();
        _graph?.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task InitializeAsync()
    {
        var settings = new AudioGraphSettings(AudioRenderCategory.GameEffects)
        {
            QuantumSizeSelectionMode = QuantumSizeSelectionMode.LowestLatency
        };

        var graphResult = await AudioGraph.CreateAsync(settings);
        if (graphResult.Status != AudioGraphCreationStatus.Success || _disposed)
        {
            return;
        }

        _graph = graphResult.Graph;
        _sampleRate = (int)_graph.EncodingProperties.SampleRate;
        _channelCount = (int)_graph.EncodingProperties.ChannelCount;

        var deviceResult = await _graph.CreateDeviceOutputNodeAsync();
        if (deviceResult.Status != AudioDeviceNodeCreationStatus.Success || _disposed)
        {
            return;
        }

        _deviceOutputNode = deviceResult.DeviceOutputNode;

        _diSamples = await DecodeAsync("di.mp3");
        _doSamples = await DecodeAsync("do.mp3");

        if (_disposed)
        {
            return;
        }

        var nodeProperties = _graph.EncodingProperties;
        nodeProperties.ChannelCount = (uint)_channelCount;
        _frameInputNode = _graph.CreateFrameInputNode(nodeProperties);
        _frameInputNode.AddOutgoingConnection(_deviceOutputNode);
        _frameInputNode.QuantumStarted += FrameInputNode_QuantumStarted;

        _graph.Start();
    }

    /// <summary>Decodes a packaged MP3 to mono float samples at the graph's sample rate.</summary>
    private async Task<float[]> DecodeAsync(string fileName)
    {
        if (_graph is null)
        {
            return [];
        }

        var path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
        var file = await StorageFile.GetFileFromPathAsync(path);

        var inputResult = await _graph.CreateFileInputNodeAsync(file);
        if (inputResult.Status != AudioFileNodeCreationStatus.Success)
        {
            return [];
        }

        var fileInput = inputResult.FileInputNode;
        var frameOutput = _graph.CreateFrameOutputNode();
        fileInput.AddOutgoingConnection(frameOutput);

        var samples = new List<float>();
        var completed = new TaskCompletionSource<bool>();
        fileInput.FileCompleted += (_, _) => completed.TrySetResult(true);

        void OnQuantum(AudioGraph sender, object args)
        {
            AppendMonoSamples(frameOutput.GetFrame(), samples);
        }

        _graph.QuantumStarted += OnQuantum;
        _graph.Start();
        await completed.Task;
        // Drain one more quantum so the tail isn't clipped.
        await Task.Delay(20);
        _graph.Stop();
        _graph.QuantumStarted -= OnQuantum;

        fileInput.Dispose();
        frameOutput.Dispose();

        return samples.ToArray();
    }

    private unsafe void AppendMonoSamples(AudioFrame frame, List<float> destination)
    {
        using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        GetBufferBytes(reference, out var dataInBytes, out var capacityInBytes);

        var floats = (float*)dataInBytes;
        var totalSamples = (int)(capacityInBytes / sizeof(float));
        if (_channelCount <= 0)
        {
            return;
        }

        for (var i = 0; i + _channelCount <= totalSamples; i += _channelCount)
        {
            var sum = 0f;
            for (var c = 0; c < _channelCount; c++)
            {
                sum += floats[i + c];
            }

            destination.Add(sum / _channelCount);
        }
    }

    private void FrameInputNode_QuantumStarted(AudioFrameInputNode sender, FrameInputNodeQuantumStartedEventArgs args)
    {
        var required = args.RequiredSamples;
        if (required <= 0)
        {
            return;
        }

        sender.AddFrame(GenerateFrame(required));
    }

    private unsafe AudioFrame GenerateFrame(int requiredSamples)
    {
        if (_flushRequested)
        {
            _flushRequested = false;
            _voices.Clear();
        }

        if (_restartRequested)
        {
            _restartRequested = false;
            _beatIndex = 0;
            _subdivisionIndex = 0;
            _nextTickFrame = _framePosition;
        }

        var byteCount = (uint)(requiredSamples * _channelCount * sizeof(float));
        var frame = new AudioFrame(byteCount);

        using (var buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
        using (var reference = buffer.CreateReference())
        {
            GetBufferBytes(reference, out var dataInBytes, out _);
            var output = (float*)dataInBytes;
            var snapshot = _snapshot;

            for (var i = 0; i < requiredSamples; i++)
            {
                var globalFrame = _framePosition + i;

                if (_running && snapshot is not null)
                {
                    while (globalFrame >= (long)_nextTickFrame)
                    {
                        TriggerOnset(snapshot);
                    }
                }

                var mixed = 0f;
                for (var v = 0; v < _voices.Count; v++)
                {
                    mixed += _voices[v].NextSample();
                }

                for (var c = 0; c < _channelCount; c++)
                {
                    output[(i * _channelCount) + c] = mixed;
                }
            }

            RemoveFinishedVoices();
        }

        _framePosition += requiredSamples;
        return frame;
    }

    private void TriggerOnset(PlaybackSnapshot snapshot)
    {
        var beatsPerMeasure = snapshot.BeatsPerMeasure;
        if (_beatIndex >= beatsPerMeasure)
        {
            _beatIndex = 0;
        }

        var beat = _beatIndex;
        var subdivision = _subdivisionIndex;

        var sound = snapshot.GetSound(beat, subdivision);
        if (sound == MetronomeSound.Di && _diSamples.Length > 0)
        {
            _voices.Add(new Voice(_diSamples));
        }
        else if (sound == MetronomeSound.Do && _doSamples.Length > 0)
        {
            _voices.Add(new Voice(_doSamples));
        }

        RaiseSubdivisionPlayed(beat, subdivision);

        _subdivisionIndex++;
        if (_subdivisionIndex >= snapshot.SubdivisionsPerBeat)
        {
            _subdivisionIndex = 0;
            _beatIndex = (beat + 1) % beatsPerMeasure;
        }

        _nextTickFrame += snapshot.FramesPerSubdivision;
    }

    private void RemoveFinishedVoices()
    {
        for (var i = _voices.Count - 1; i >= 0; i--)
        {
            if (_voices[i].IsFinished)
            {
                _voices.RemoveAt(i);
            }
        }
    }

    private void RaiseSubdivisionPlayed(int beatIndex, int subdivisionIndex)
    {
        _dispatcherQueue.TryEnqueue(() =>
            SubdivisionPlayed?.Invoke(this, new SubdivisionPlayedEventArgs(beatIndex, subdivisionIndex)));
    }

    private void PublishSnapshot()
    {
        var beatTypes = new BeatType[_state.BeatsPerMeasure];
        for (var i = 0; i < beatTypes.Length; i++)
        {
            beatTypes[i] = _state.BeatTypes[i];
        }

        var subdivisionsPerBeat = _state.SubdivisionsPerBeat;
        var rate = _sampleRate > 0 ? _sampleRate : 48000;
        var framesPerSubdivision = rate * 60.0 / (_state.Bpm * subdivisionsPerBeat);

        _snapshot = new PlaybackSnapshot(beatTypes, subdivisionsPerBeat, framesPerSubdivision);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    // IMemoryBufferByteAccess. CsWinRT projects the buffer reference as IInspectable, so a classic
    // [ComImport] cast throws 0x80004002; QI to the native interface and invoke GetBuffer directly.
    private static readonly Guid MemoryBufferByteAccessIid = new("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");

    private static unsafe void GetBufferBytes(IMemoryBufferReference reference, out byte* data, out uint capacity)
    {
        data = null;
        capacity = 0;

        var iid = MemoryBufferByteAccessIid;
        var inspectable = MarshalInspectable<IMemoryBufferReference>.FromManaged(reference);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable, ref iid, out var byteAccess));
            try
            {
                // GetBuffer is vtable slot 3 (0-2 are IUnknown). HRESULT GetBuffer(byte** value, uint* capacity).
                var getBuffer = (delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, int>)(*(void***)byteAccess)[3];
                byte* localData;
                uint localCapacity;
                Marshal.ThrowExceptionForHR(getBuffer(byteAccess, &localData, &localCapacity));
                data = localData;
                capacity = localCapacity;
            }
            finally
            {
                Marshal.Release(byteAccess);
            }
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    /// <summary>A single playing click, advanced one sample at a time on the audio thread.</summary>
    private sealed class Voice(float[] data)
    {
        private int _position;

        public bool IsFinished => _position >= data.Length;

        public float NextSample()
        {
            return _position < data.Length ? data[_position++] : 0f;
        }
    }

    /// <summary>Immutable view of the metronome state, published by the UI thread and read by the audio thread.</summary>
    private sealed class PlaybackSnapshot(BeatType[] beatTypes, int subdivisionsPerBeat, double framesPerSubdivision)
    {
        public int SubdivisionsPerBeat { get; } = subdivisionsPerBeat;

        public double FramesPerSubdivision { get; } = framesPerSubdivision;

        public int BeatsPerMeasure => beatTypes.Length;

        public MetronomeSound GetSound(int beatIndex, int subdivisionIndex)
        {
            if (subdivisionIndex > 0)
            {
                return MetronomeSound.Do;
            }

            return beatTypes[beatIndex] switch
            {
                BeatType.Accent or BeatType.SecondaryAccent => MetronomeSound.Di,
                BeatType.Normal => MetronomeSound.Do,
                BeatType.Silent => MetronomeSound.Silent,
                _ => MetronomeSound.Silent
            };
        }
    }
}