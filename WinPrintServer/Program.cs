using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting.WindowsServices;
using System.Diagnostics;
using System.ServiceProcess;
using WinPrintServer;

var options = new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService()
                      ? AppContext.BaseDirectory : default
};

// Check if running as service or console
if (!WindowsServiceHelpers.IsWindowsService() && OperatingSystem.IsWindows())
{
    try
    {
        // Try to check if service is installed
        // We use a try-catch because on some non-admin contexts this might fail,
        // or if we are not on Windows (though we checked IsWindows).
        const string serviceName = "WinPrintServer";
        bool serviceExists = false;
        try
        {
            using var sc = new ServiceController(serviceName);
            serviceExists = (sc.Status != ServiceControllerStatus.Stopped && sc.Status != ServiceControllerStatus.Running && sc.Status != ServiceControllerStatus.Paused);
            // Actually accessing Status throws if service doesn't exist
            var s = sc.Status;
            serviceExists = true;
        }
        catch
        {
            serviceExists = false;
        }

        if (!serviceExists)
        {
            Console.WriteLine("WinPrintServer isn't installed.");
            Console.WriteLine("Do you want to install and configure service? (Y/N)");
            var key = Console.ReadKey();
            Console.WriteLine();

            if (key.Key == ConsoleKey.T || key.Key == ConsoleKey.Y)
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    // Remove extension .dll if it's there (running with dotnet command) and ensure .exe
                    // But usually MainModule.FileName points to the host exe
                    if (exePath.EndsWith(".dll")) exePath = exePath.Replace(".dll", ".exe");

                    Console.WriteLine($"Installing service: {exePath}");

                    // Create service
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
                        Console.WriteLine("Service installed.");
                        Console.WriteLine("Do you want to start it now? (Y/N)");
                        var startKey = Console.ReadKey();
                        Console.WriteLine();

                        if (startKey.Key == ConsoleKey.T || startKey.Key == ConsoleKey.Y)
                        {
                            var startProcess = Process.Start(new ProcessStartInfo
                            {
                                FileName = "sc.exe",
                                Arguments = $"start {serviceName}",
                                UseShellExecute = false,
                                RedirectStandardOutput = true
                            });
                            startProcess?.WaitForExit();
                            Console.WriteLine(startProcess?.StandardOutput.ReadToEnd());
                        }

                        Console.WriteLine("Installed and configured service successfully.");
                        return;
                    }
                    else
                    {
                        Console.WriteLine("Error when installing service. Make sure to run this app as administrator");
                    }
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
    }
}

var builder = WebApplication.CreateBuilder(options);

// Configure as Windows Service
builder.Host.UseWindowsService();

// Read Port from configuration
var port = builder.Configuration.GetValue<int>("PrintServer:Port", 5000);
builder.WebHost.UseUrls($"http://*:{port}");

// Add services to the container.
builder.Services.AddSingleton<PrintService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseDefaultFiles();
app.UseStaticFiles();

// Ensure upload directory exists
var uploadDir = app.Configuration.GetValue<string>("PrintServer:UploadDirectory", @"C:\WinPrintBridge\");
if (string.IsNullOrWhiteSpace(uploadDir))
{
    // Fallback to local 'uploads' folder if config is explicitly empty but not null
    uploadDir = Path.Combine(app.Environment.ContentRootPath, "uploads");
}

if (!Directory.Exists(uploadDir))
{
    Directory.CreateDirectory(uploadDir);
}

// APIs
app.MapPost("/api/upload", async (IFormFile file) =>
{
    if (file == null || file.Length == 0)
        return Results.BadRequest("No file uploaded.");

    var id = Guid.NewGuid().ToString();
    var ext = Path.GetExtension(file.FileName);
    var filename = $"{id}{ext}";
    var filepath = Path.Combine(uploadDir, filename);

    using (var stream = new FileStream(filepath, FileMode.Create))
    {
        await file.CopyToAsync(stream);
    }

    return Results.Ok(new { id = id, filename = file.FileName, type = ext });
})
.DisableAntiforgery(); // Simplify for this local tablet use case

app.MapGet("/api/preview/{id}", (string id) =>
{
    // Find file starting with id
    var files = Directory.GetFiles(uploadDir, $"{id}.*");
    if (files.Length == 0) return Results.NotFound();

    var filepath = files[0];
    var ext = Path.GetExtension(filepath).ToLower();

    var mimeType = ext switch
    {
        ".jpg" => "image/jpeg",
        ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".bmp" => "image/bmp",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream"
    };

    return Results.File(filepath, mimeType);
});

app.MapPost("/api/print/{id}", (string id, [FromServices] PrintService printService) =>
{
    var files = Directory.GetFiles(uploadDir, $"{id}.*");
    if (files.Length == 0) return Results.NotFound();

    var filepath = files[0];

    try
    {
        printService.Print(filepath);
        return Results.Ok(new { message = "Print job started." });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// Admin endpoints
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
    if (process == null) return;

    process.WaitForExit();

    if (process.ExitCode != 0)
    {
        string error = process.StandardError.ReadToEnd();
        // Log error? For now just throw to return 500
        throw new Exception($"Command failed: {command}. Error: {error}");
    }
}

app.MapPost("/api/admin/clean-spooler", () =>
{
    try
    {
        RunPowerShellCommand("Stop-Service -Name Spooler -Force");
        RunPowerShellCommand("Remove-Item \"C:\\Windows\\System32\\spool\\PRINTERS\\*\" -Force -Recurse");
        RunPowerShellCommand("Start-Service Spooler");
        return Results.Ok(new { message = "Deleted all documents from printing queue. Spooler service restarted." });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error: {ex.Message}");
    }
});

app.MapPost("/api/admin/restart-server", () =>
{
    try
    {
        RunPowerShellCommand("Stop-Service -Name Spooler -Force");
        RunPowerShellCommand("Remove-Item \"C:\\Windows\\System32\\spool\\PRINTERS\\*\" -Force -Recurse");
        RunPowerShellCommand("Start-Service Spooler");

        // Restart machine
        Process.Start("shutdown", "/r /t 0");

        return Results.Ok(new { message = "Deleting all documents from queue and resatrting Windows..." });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error: {ex.Message}");
    }
});

app.Run();
