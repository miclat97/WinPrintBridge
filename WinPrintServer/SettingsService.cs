using System.Text.Json;

namespace WinPrintBridge
{
    public class SettingsService
    {
        private readonly string _filePath;
        private RuntimeSettings _currentSettings;
        private readonly object _lock = new object();

        public SettingsService(IHostEnvironment env, IConfiguration configuration)
        {
            _filePath = Path.Combine(env.ContentRootPath, "runtime_settings.json");
            LoadSettings(configuration);
        }

        private void LoadSettings(IConfiguration configuration)
        {
            lock (_lock)
            {
                if (File.Exists(_filePath))
                {
                    try
                    {
                        var json = File.ReadAllText(_filePath);
                        _currentSettings = JsonSerializer.Deserialize<RuntimeSettings>(json) ?? new RuntimeSettings();
                        return;
                    }
                    catch
                    {
                        // Log error? Fallback to defaults.
                    }
                }

                // Fallback to appsettings or defaults
                _currentSettings = new RuntimeSettings
                {
                    PrinterName = configuration["PrintServer:PrinterName"] ?? "HP",
                    AutoCleanEnabled = false,
                    AutoCleanTimeoutMinutes = 20,
                    PreviewEnabled = true
                };
            }
        }

        public RuntimeSettings GetSettings()
        {
            lock (_lock)
            {
                // Return a copy to avoid external mutation without saving
                return new RuntimeSettings
                {
                    PrinterName = _currentSettings.PrinterName,
                    AutoCleanEnabled = _currentSettings.AutoCleanEnabled,
                    AutoCleanTimeoutMinutes = _currentSettings.AutoCleanTimeoutMinutes,
                    PreviewEnabled = _currentSettings.PreviewEnabled
                };
            }
        }

        public void SaveSettings(RuntimeSettings settings)
        {
            lock (_lock)
            {
                _currentSettings = settings;
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_filePath, json);
            }
        }
    }
}
