using System;
using System.Collections.Generic;

namespace MeowTempo;

public enum BeatType
{
    Accent,
    SecondaryAccent,
    Normal,
    Silent
}

public enum BeatSubdivision
{
    Quarter,
    Eighth,
    EighthTriplet
}

public enum MetronomeSound
{
    Di,
    Do,
    Silent
}

public sealed record MetronomeStateSnapshot(
    int Bpm,
    int BeatsPerMeasure,
    BeatSubdivision Subdivision,
    List<BeatType> BeatTypes,
    int WindowWidth,
    int WindowHeight);

public sealed class MetronomeState
{
    public const int MinimumBpm = 20;
    public const int MaximumBpm = 300;
    public const int MinimumBeatsPerMeasure = 1;
    public const int MaximumBeatsPerMeasure = 32;

    private readonly List<BeatType> _beatTypes =
    [
        BeatType.Accent,
        BeatType.Normal,
        BeatType.Accent,
        BeatType.Normal
    ];

    public int Bpm { get; private set; } = 110;

    public int BeatsPerMeasure { get; private set; } = 4;

    public BeatSubdivision Subdivision { get; private set; } = BeatSubdivision.Eighth;

    public int WindowWidth { get; private set; }

    public int WindowHeight { get; private set; }

    public IReadOnlyList<BeatType> BeatTypes => _beatTypes;

    public int SubdivisionsPerBeat => Subdivision switch
    {
        BeatSubdivision.Quarter => 1,
        BeatSubdivision.Eighth => 2,
        BeatSubdivision.EighthTriplet => 3,
        _ => throw new InvalidOperationException($"Unknown subdivision: {Subdivision}.")
    };

    public TimeSpan SubdivisionInterval => TimeSpan.FromMinutes(1d / (Bpm * SubdivisionsPerBeat));

    public void SetBpm(int bpm)
    {
        Bpm = Math.Clamp(bpm, MinimumBpm, MaximumBpm);
    }

    public void AdjustBpm(int delta)
    {
        SetBpm(Bpm + delta);
    }

    public void SetWindowSize(int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        WindowWidth = width;
        WindowHeight = height;
    }

    public MetronomeStateSnapshot CreateSnapshot() => new(
        Bpm,
        BeatsPerMeasure,
        Subdivision,
        new List<BeatType>(_beatTypes),
        WindowWidth,
        WindowHeight);

    public void Restore(MetronomeStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.BeatTypes is null || snapshot.BeatTypes.Count != snapshot.BeatsPerMeasure)
        {
            throw new ArgumentException("Beat types must match the time signature.", nameof(snapshot));
        }

        if (!Enum.IsDefined(snapshot.Subdivision))
        {
            throw new ArgumentException("Subdivision is invalid.", nameof(snapshot));
        }

        if (snapshot.BeatTypes.Exists(beatType => !Enum.IsDefined(beatType)))
        {
            throw new ArgumentException("Beat type is invalid.", nameof(snapshot));
        }

        SetBpm(snapshot.Bpm);
        SetTimeSignature(snapshot.BeatsPerMeasure);
        SetSubdivision(snapshot.Subdivision);

        for (var index = 0; index < _beatTypes.Count; index++)
        {
            _beatTypes[index] = snapshot.BeatTypes[index];
        }

        if (snapshot.WindowWidth > 0 && snapshot.WindowHeight > 0)
        {
            SetWindowSize(snapshot.WindowWidth, snapshot.WindowHeight);
        }
    }

    public void SetTimeSignature(int beatsPerMeasure)
    {
        if (beatsPerMeasure is < MinimumBeatsPerMeasure or > MaximumBeatsPerMeasure)
        {
            throw new ArgumentOutOfRangeException(nameof(beatsPerMeasure));
        }

        while (_beatTypes.Count < beatsPerMeasure)
        {
            _beatTypes.Add(BeatType.Normal);
        }

        if (_beatTypes.Count > beatsPerMeasure)
        {
            _beatTypes.RemoveRange(beatsPerMeasure, _beatTypes.Count - beatsPerMeasure);
        }

        BeatsPerMeasure = _beatTypes.Count;
    }

    public void SetSubdivision(BeatSubdivision subdivision)
    {
        Subdivision = subdivision;
    }

    public BeatType CycleBeatType(int beatIndex)
    {
        ValidateBeatIndex(beatIndex);
        var next = ((int)_beatTypes[beatIndex] + 1) % Enum.GetValues<BeatType>().Length;
        _beatTypes[beatIndex] = (BeatType)next;
        return _beatTypes[beatIndex];
    }

    public MetronomeSound GetSound(int beatIndex, int subdivisionIndex)
    {
        ValidateBeatIndex(beatIndex);

        if (subdivisionIndex is < 0 or >= 3 || subdivisionIndex >= SubdivisionsPerBeat)
        {
            throw new ArgumentOutOfRangeException(nameof(subdivisionIndex));
        }

        var beatType = _beatTypes[beatIndex];
        if (beatType == BeatType.Silent)
        {
            return MetronomeSound.Silent;
        }

        if (subdivisionIndex > 0)
        {
            return MetronomeSound.Do;
        }

        return beatType switch
        {
            BeatType.Accent or BeatType.SecondaryAccent => MetronomeSound.Di,
            BeatType.Normal => MetronomeSound.Do,
            _ => throw new InvalidOperationException($"Unknown beat type: {beatType}.")
        };
    }

    private void ValidateBeatIndex(int beatIndex)
    {
        if (beatIndex < 0 || beatIndex >= BeatsPerMeasure)
        {
            throw new ArgumentOutOfRangeException(nameof(beatIndex));
        }
    }
}
