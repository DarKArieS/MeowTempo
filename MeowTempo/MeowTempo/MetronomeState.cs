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

public sealed class MetronomeState
{
    public const int MinimumBpm = 20;
    public const int MaximumBpm = 300;
    public const int MinimumBeatsPerMeasure = 1;
    public const int MaximumBeatsPerMeasure = 32;

    private readonly List<BeatType> _beatTypes =
    [
        BeatType.Accent,
        BeatType.SecondaryAccent,
        BeatType.Normal,
        BeatType.Silent
    ];

    public int Bpm { get; private set; } = 110;

    public int BeatsPerMeasure { get; private set; } = 4;

    public BeatSubdivision Subdivision { get; private set; } = BeatSubdivision.Quarter;

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

        if (subdivisionIndex > 0)
        {
            return MetronomeSound.Do;
        }

        return _beatTypes[beatIndex] switch
        {
            BeatType.Accent or BeatType.SecondaryAccent => MetronomeSound.Di,
            BeatType.Normal => MetronomeSound.Do,
            BeatType.Silent => MetronomeSound.Silent,
            _ => throw new InvalidOperationException($"Unknown beat type: {_beatTypes[beatIndex]}.")
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
