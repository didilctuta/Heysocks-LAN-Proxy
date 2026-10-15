using System.Text.Json;

namespace HeysocksLanProxy.Core;

public sealed class ConfigStore<T> where T : new()
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public ConfigStore(string applicationFolder, string fileName = "settings.json")
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        DirectoryPath = Path.Combine(root, "HeysocksLanProxy", applicationFolder);
        FilePath = Path.Combine(DirectoryPath, fileName);
    }

    public string DirectoryPath { get; }
    public string FilePath { get; }

    public T Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new T();
            }

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? new T();
        }
        catch
        {
            return new T();
        }
    }

    public void Save(T value)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporaryPath = FilePath + ".tmp";
        var json = JsonSerializer.Serialize(value, JsonOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, FilePath, true);
    }
}
