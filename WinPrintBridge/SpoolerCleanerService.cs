using System.Diagnostics;

namespace WinPrintBridge
{
    public class SpoolerCleanerService : BackgroundService
    {
        private readonly ILogger<SpoolerCleanerService> _logger;
        private readonly SettingsService _settingsService;
        private const string SpoolPath = @"C:\Windows\System32\spool\PRINTERS";

        public SpoolerCleanerService(ILogger<SpoolerCleanerService> logger, SettingsService settingsService)
        {
            _logger = logger;
            _settingsService = settingsService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("SpoolerCleanerService is starting.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckAndCleanSpooler();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in SpoolerCleanerService loop.");
                }

                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }

        private async Task CheckAndCleanSpooler()
        {
            var settings = _settingsService.GetSettings();
            if (!settings.AutoCleanEnabled) return;

            if (!Directory.Exists(SpoolPath))
            {
                // If path doesn't exist, we can't check. Maybe not a Windows print server?
                return;
            }

            var timeoutMinutes = settings.AutoCleanTimeoutMinutes;
            if (timeoutMinutes <= 0) timeoutMinutes = 20; // Default safety

            var directoryInfo = new DirectoryInfo(SpoolPath);
            var files = directoryInfo.GetFiles();

            bool needsCleanup = false;
            var cutoff = DateTime.Now.AddMinutes(-timeoutMinutes);

            foreach (var file in files)
            {
                // Check if file is older than cutoff
                if (file.CreationTime < cutoff)
                {
                    _logger.LogWarning("Found stuck file {FileName} created at {CreationTime}. Triggering cleanup.", file.Name, file.CreationTime);
                    needsCleanup = true;
                    break;
                }
            }

            if (needsCleanup)
            {
                await PerformCleanup();
            }
        }

        private Task PerformCleanup()
        {
            _logger.LogInformation("Performing Force Spooler Cleanup...");
            try
            {
                RunPowerShellCommand("Stop-Service -Name Spooler -Force");

                // Double check deletion (PowerShell usually handles it better with locks)
                RunPowerShellCommand($"Remove-Item \"{SpoolPath}\\*\" -Force -Recurse");

                RunPowerShellCommand("Start-Service Spooler");

                _logger.LogInformation("Spooler Cleanup completed successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to perform spooler cleanup.");
            }
            return Task.CompletedTask;
        }

        private void RunPowerShellCommand(string command)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            process?.WaitForExit();

            if (process?.ExitCode != 0)
            {
                string error = process?.StandardError?.ReadToEnd() ?? "Unknown error";
                throw new Exception($"Command failed: {command}. Error: {error}");
            }
        }
    }
}
