using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Win32;
using System.Diagnostics;
using System.ServiceProcess;
using WinPrintBridge;

var options = new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService()
                      ? AppContext.BaseDirectory : default
};

// Check if running as service or console (Preserved existing logic)
if (!WindowsServiceHelpers.IsWindowsService() && OperatingSystem.IsWindows())
{
    try
    {
        const string serviceName = "WinPrintBridge";
        bool serviceExists = false;
        try
        {
            using var sc = new ServiceController(serviceName);
            var s = sc.Status;
            serviceExists = true;
        }
        catch { serviceExists = false; }

        if (!serviceExists)
        {
            Console.WriteLine("WinPrintBridge isn't installed.");
            Console.WriteLine("Do you want to install and configure service? (Y/N)");
            var key = Console.ReadKey();
            Console.WriteLine();

            if (key.Key == ConsoleKey.T || key.Key == ConsoleKey.Y)
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    if (exePath.EndsWith(".dll")) exePath = exePath.Replace(".dll", ".exe");
                    Console.WriteLine($"Installing service: {exePath}");
                    var createProcess = Process.Start(new ProcessStartInfo
                    {
                        FileName = "sc.exe",
                        Arguments = $"create {serviceName} binPath= \"{exePath}\" start= auto",
                        UseShellExecute = false,
                        RedirectStandardOutput = true
                    });
                    createProcess?.WaitForExit();
                    Console.WriteLine(createProcess?.StandardOutput.ReadToEnd());

                    if (createProcess?.ExitCode == 0)
                    {
                        Console.WriteLine("Service installed. Start now? (Y/N)");
                        var startKey = Console.ReadKey();
                        Console.WriteLine();
                        if (startKey.Key == ConsoleKey.T || startKey.Key == ConsoleKey.Y)
                        {
                            Process.Start(new ProcessStartInfo("sc.exe", $"start {serviceName}") { UseShellExecute = false })?.WaitForExit();
                        }
                        return;
                    }
                }
            }
        }
    }
    catch (Exception ex) { Console.WriteLine($"Error: {ex.Message}"); }
}

var builder = WebApplication.CreateBuilder(options);

builder.Host.UseWindowsService();

var port = builder.Configuration.GetValue<int>("PrintServer:Port", 5000);
builder.WebHost.UseUrls($"http://*:{port}");

builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<PrintService>();
builder.Services.AddHostedService<SpoolerCleanerService>();

var app = builder.Build();

// Open firewall port for HTTP server on startup
var firewallRuleName = $"WinPrintBridge_HTTP_Port_{port}";
Process.Start(new ProcessStartInfo("netsh", $"advfirewall firewall add rule name=\"{firewallRuleName}\" dir=in action=allow protocol=TCP localport={port} profile=any") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit();

app.UseDefaultFiles();
app.UseStaticFiles();

var uploadDir = app.Configuration.GetValue<string>("PrintServer:UploadDirectory", @"C:\WinPrintBridge\");
if (string.IsNullOrWhiteSpace(uploadDir)) uploadDir = Path.Combine(app.Environment.ContentRootPath, "uploads");
if (!Directory.Exists(uploadDir)) Directory.CreateDirectory(uploadDir);

// Helpers
void RunPowerShellCommand(string command)
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
    if (process?.ExitCode != 0) throw new Exception($"Command failed: {command} (Exit Code: {process?.ExitCode})");
}

bool IsAdmin(HttpContext context, IConfiguration config)
{
    var pass = config["PrintServer:AdminPassword"];
    if (string.IsNullOrEmpty(pass)) return true; // No password = open? Or safe default? Assuming open for local unless set.

    if (context.Request.Headers.TryGetValue("X-Admin-Password", out var val) && val == pass) return true;
    return false;
}

// APIs
app.MapPost("/api/upload", async (IFormFile file) =>
{
    if (file == null || file.Length == 0) return Results.BadRequest("No file uploaded.");
    var id = Guid.NewGuid().ToString();
    var ext = Path.GetExtension(file.FileName);
    var filename = $"{id}{ext}";
    var filepath = Path.Combine(uploadDir, filename);
    using (var stream = new FileStream(filepath, FileMode.Create)) await file.CopyToAsync(stream);
    return Results.Ok(new { id = id, filename = file.FileName, type = ext });
}).DisableAntiforgery();

app.MapGet("/api/preview/{id}", (string id) =>
{
    var files = Directory.GetFiles(uploadDir, $"{id}.*");
    if (files.Length == 0) return Results.NotFound();
    var filepath = files[0];
    var ext = Path.GetExtension(filepath).ToLower();
    var mimeType = ext switch { ".jpg" => "image/jpeg", ".jpeg" => "image/jpeg", ".png" => "image/png", ".bmp" => "image/bmp", ".pdf" => "application/pdf", _ => "application/octet-stream" };
    return Results.File(filepath, mimeType);
});

app.MapGet("/api/render-pdf/{id}", (string id, [FromServices] PrintService printService) =>
{
    var files = Directory.GetFiles(uploadDir, $"{id}.*");
    if (files.Length == 0) return Results.NotFound();
    var filepath = files[0];
    try
    {
        var stream = printService.RenderPdfPage(filepath, 0);
        return Results.File(stream, "image/png");
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.MapPost("/api/print/{id}", (string id, [FromBody] PrintRequest req, [FromServices] PrintService printService) =>
{
    var files = Directory.GetFiles(uploadDir, $"{id}.*");
    if (files.Length == 0) return Results.NotFound();
    try
    {
        printService.Print(files[0], req.Copies, req.Rotation);
        return Results.Ok(new { message = "Print job started." });
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.MapGet("/api/scan", (bool saveToServer = true) =>
{
    string? finalFilePath = null;
    string? tempFilePath = null;
    Exception? scanException = null;

    // Skanowanie w dedykowanym wątku STA
    Thread staThread = new Thread(() =>
    {
        try
        {
            var deviceManager = new WIA.DeviceManager();
            WIA.DeviceInfo? scannerInfo = null;

            foreach (WIA.DeviceInfo info in deviceManager.DeviceInfos)
            {
                if (info.Type == WIA.WiaDeviceType.ScannerDeviceType)
                {
                    scannerInfo = info;
                    break;
                }
            }

            if (scannerInfo == null) throw new Exception("Nie znaleziono skanera w systemie.");

            var device = scannerInfo.Connect();
            WIA.Item item = device.Items[1];

            string formatJPEG = "{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}";
            var imageFile = (WIA.ImageFile)item.Transfer(formatJPEG);

            string fileName = $"Skan_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.jpg";

            // Logika wyboru miejsca zapisu
            if (saveToServer)
            {
                string targetDir = @"C:\Skany";
                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                finalFilePath = Path.Combine(targetDir, fileName);
                if (File.Exists(finalFilePath)) File.Delete(finalFilePath);

                imageFile.SaveFile(finalFilePath);
            }
            else
            {
                tempFilePath = Path.Combine(Path.GetTempPath(), fileName);
                if (File.Exists(tempFilePath)) File.Delete(tempFilePath);

                imageFile.SaveFile(tempFilePath);
            }
        }
        catch (Exception ex)
        {
            scanException = ex;
        }
    });

    staThread.SetApartmentState(ApartmentState.STA);
    staThread.Start();
    staThread.Join();

    if (scanException != null)
        return Results.Problem($"Błąd skanowania: {scanException.Message}");

    // Jeśli wybraliśmy zapis na serwerze (zwraca tylko komunikat JSON)
    if (saveToServer && finalFilePath != null)
    {
        return Results.Ok(new { message = $"Plik zapisano bezpiecznie na serwerze w C:\\Skany" });
    }
    // Jeśli wybraliśmy pobieranie na urządzenie (zwraca plik, a potem go kasuje z Temp)
    else if (!saveToServer && tempFilePath != null && File.Exists(tempFilePath))
    {
        byte[] fileBytes = System.IO.File.ReadAllBytes(tempFilePath);
        System.IO.File.Delete(tempFilePath);
        return Results.File(fileBytes, "image/jpeg", Path.GetFileName(tempFilePath));
    }

    return Results.Problem("Nie udało się utworzyć pliku ze skanem.");
});

// Settings / Admin
app.MapGet("/api/settings", ([FromServices] SettingsService ss) =>
{
    var s = ss.GetSettings();
    return Results.Ok(new { s.PrinterName, s.PreviewEnabled });
});

app.MapPost("/api/admin/verify", (HttpContext ctx, IConfiguration config) =>
{
    if (IsAdmin(ctx, config)) return Results.Ok();
    return Results.Unauthorized();
});

app.MapGet("/api/admin/settings", (HttpContext ctx, IConfiguration config, [FromServices] SettingsService ss) =>
{
    if (!IsAdmin(ctx, config)) return Results.Unauthorized();
    return Results.Ok(ss.GetSettings());
});

app.MapPost("/api/admin/settings", (HttpContext ctx, IConfiguration config, [FromServices] SettingsService ss, [FromBody] RuntimeSettings newSettings) =>
{
    if (!IsAdmin(ctx, config)) return Results.Unauthorized();
    ss.SaveSettings(newSettings);
    return Results.Ok();
});

app.MapPost("/api/admin/clean-spooler", (HttpContext ctx, IConfiguration config) =>
{
    if (!IsAdmin(ctx, config)) return Results.Unauthorized();
    try
    {
        RunPowerShellCommand("Stop-Service -Name Spooler -Force");
        RunPowerShellCommand("Remove-Item \"C:\\Windows\\System32\\spool\\PRINTERS\\*\" -Force -Recurse");
        RunPowerShellCommand("Start-Service Spooler");
        return Results.Ok(new { message = "Deleted all documents from printing queue. Spooler service restarted." });
    }
    catch (Exception ex) { return Results.Problem($"Error: {ex.Message}"); }
});

app.MapPost("/api/admin/restart-server", (HttpContext ctx, IConfiguration config) =>
{
    if (!IsAdmin(ctx, config)) return Results.Unauthorized();
    try
    {
        RunPowerShellCommand("Stop-Service -Name Spooler -Force");
        RunPowerShellCommand("Remove-Item \"C:\\Windows\\System32\\spool\\PRINTERS\\*\" -Force -Recurse");
        RunPowerShellCommand("Start-Service Spooler");
        Process.Start("shutdown", "/r /t 0");
        return Results.Ok(new { message = "Deleting all documents from queue and restarting Windows..." });
    }
    catch (Exception ex) { return Results.Problem($"Error: {ex.Message}"); }
});

app.MapPost("/api/admin/enable-rdp", (HttpContext ctx, IConfiguration config) =>
{
    if (!IsAdmin(ctx, config)) return Results.Unauthorized();
    try
    {
        // Enable RDP
        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server", true))
        {
            if (key != null) key.SetValue("fDenyTSConnections", 0, RegistryValueKind.DWord);
        }

        // Disable NLA
        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", true))
        {
            if (key != null) key.SetValue("UserAuthentication", 0, RegistryValueKind.DWord);
        }

        // Unlock Port 3389
        Process.Start(new ProcessStartInfo("netsh", "advfirewall firewall add rule name=\"WinPrintBridge_RDP_3389\" dir=in action=allow protocol=TCP localport=3389 profile=any") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit();

        return Results.Ok(new { message = "RDP Enabled (No NLA), Port 3389 Unlocked." });
    }
    catch (Exception ex) { return Results.Problem($"Error: {ex.Message}"); }
});

app.MapPost("/api/admin/exec-powershell", async (HttpContext ctx, IConfiguration config, [FromBody] PowerShellRequest req) =>
{
    if (!IsAdmin(ctx, config)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(req.Command)) return Results.BadRequest("Command is empty");

    try
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{req.Command.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null) return Results.Problem("Failed to start PowerShell process");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        var output = await outputTask;
        var error = await errorTask;

        return Results.Ok(new { output, error, exitCode = process.ExitCode });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error executing command: {ex.Message}");
    }
});

app.Run();

// DTOs
record PrintRequest(int Copies = 1, int Rotation = 0);
record PowerShellRequest(string Command);
