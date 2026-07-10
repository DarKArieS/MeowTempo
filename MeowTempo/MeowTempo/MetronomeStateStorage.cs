using System;
using System.IO;
using System.Text.Json;
using Windows.Storage;

namespace MeowTempo;

public sealed class MetronomeStateStorage
{
    private const string FileName = "metronome-state.json";
    private readonly string _filePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, FileName);

    public MetronomeStateSnapshot? Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<MetronomeStateSnapshot>(File.ReadAllText(_filePath));
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(MetronomeStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(snapshot));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
