using System;
using System.IO;
using System.Text.Json;


namespace BlindTouchOled.Services
{
    public interface ISettingsService
    {
        Models.AppSettings Load();
        void Save(Models.AppSettings settings);
    }

    public class SettingsService : ISettingsService
    {
        private static readonly object IoLock = new();
        private readonly string _filePath;

        public SettingsService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var folder = Path.Combine(appData, "BlindTouchOled");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "settings.json");
        }

        public Models.AppSettings Load()
        {
            lock (IoLock)
            {
                if (!File.Exists(_filePath)) return new Models.AppSettings();
                try
                {
                    var json = File.ReadAllText(_filePath);
                    return JsonSerializer.Deserialize<Models.AppSettings>(json) ?? new Models.AppSettings();
                }
                catch
                {
                    return new Models.AppSettings();
                }
            }
        }

        public void Save(Models.AppSettings settings)
        {
            lock (IoLock)
            {
                try
                {
                    var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                    var tempPath = _filePath + ".tmp";

                    File.WriteAllText(tempPath, json);
                    if (File.Exists(_filePath))
                    {
                        File.Copy(tempPath, _filePath, overwrite: true);
                        File.Delete(tempPath);
                    }
                    else
                    {
                        File.Move(tempPath, _filePath);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
                }
            }
        }
    }
}
