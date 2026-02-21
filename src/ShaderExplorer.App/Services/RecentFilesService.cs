using System.IO;
using System.Text.Json;

namespace ShaderExplorer.App.Services;

public class RecentFilesService
{
    private const int MaxRecentFiles = 15;

    private static readonly string RecentFilesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ShaderExplorer", "recent.json");

    private readonly List<string> _recentFiles;

    public RecentFilesService()
    {
        _recentFiles = LoadRecentFiles();
    }

    public IReadOnlyList<string> Files => _recentFiles;

    public void Add(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        _recentFiles.Remove(fullPath);
        _recentFiles.Insert(0, fullPath);
        if (_recentFiles.Count > MaxRecentFiles)
            _recentFiles.RemoveRange(MaxRecentFiles, _recentFiles.Count - MaxRecentFiles);
        SaveRecentFiles();
    }

    private static List<string> LoadRecentFiles()
    {
        try
        {
            if (!File.Exists(RecentFilesPath)) return [];
            var json = File.ReadAllText(RecentFilesPath);
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void SaveRecentFiles()
    {
        try
        {
            var dir = Path.GetDirectoryName(RecentFilesPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_recentFiles);
            File.WriteAllText(RecentFilesPath, json);
        }
        catch
        {
            /* ignore */
        }
    }
}
