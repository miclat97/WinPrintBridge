using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting.WindowsServices;
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
    try {
        var stream = printService.RenderPdfPage(filepath, 0);
        return Results.File(stream, "image/png");
    } catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.MapPost("/api/print/{id}", (string id, [FromBody] PrintRequest req, [FromServices] PrintService printService) =>
{
    var files = Directory.GetFiles(uploadDir, $"{id}.*");
    if (files.Length == 0) return Results.NotFound();
    try {
        printService.Print(files[0], req.Copies, req.Rotation);
        return Results.Ok(new { message = "Print job started." });
    } catch (Exception ex) { return Results.Problem(ex.Message); }
});

// Settings / Admin
app.MapGet("/api/settings", ([FromServices] SettingsService ss) => {
    var s = ss.GetSettings();
    return Results.Ok(new { s.PrinterName, s.PreviewEnabled });
});

app.MapPost("/api/admin/verify", (HttpContext ctx, IConfiguration config) => {
    if (IsAdmin(ctx, config)) return Results.Ok();
    return Results.Unauthorized();
});

app.MapGet("/api/admin/settings", (HttpContext ctx, IConfiguration config, [FromServices] SettingsService ss) => {
    if (!IsAdmin(ctx, config)) return Results.Unauthorized();
    return Results.Ok(ss.GetSettings());
});

app.MapPost("/api/admin/settings", (HttpContext ctx, IConfiguration config, [FromServices] SettingsService ss, [FromBody] RuntimeSettings newSettings) => {
    if (!IsAdmin(ctx, config)) return Results.Unauthorized();
    ss.SaveSettings(newSettings);
    return Results.Ok();
});

app.MapPost("/api/admin/clean-spooler", (HttpContext ctx, IConfiguration config) =>
{
    if (!IsAdmin(ctx, config)) return Results.Unauthorized();
    try {
        RunPowerShellCommand("Stop-Service -Name Spooler -Force");
        RunPowerShellCommand("Remove-Item \"C:\\Windows\\System32\\spool\\PRINTERS\\*\" -Force -Recurse");
        RunPowerShellCommand("Start-Service Spooler");
        return Results.Ok(new { message = "Deleted all documents from printing queue. Spooler service restarted." });
    } catch (Exception ex) { return Results.Problem($"Error: {ex.Message}"); }
});

app.MapPost("/api/admin/restart-server", (HttpContext ctx, IConfiguration config) =>
{
    if (!IsAdmin(ctx, config)) return Results.Unauthorized();
    try {
        RunPowerShellCommand("Stop-Service -Name Spooler -Force");
        RunPowerShellCommand("Remove-Item \"C:\\Windows\\System32\\spool\\PRINTERS\\*\" -Force -Recurse");
        RunPowerShellCommand("Start-Service Spooler");
        Process.Start("shutdown", "/r /t 0");
        return Results.Ok(new { message = "Deleting all documents from queue and restarting Windows..." });
    } catch (Exception ex) { return Results.Problem($"Error: {ex.Message}"); }
});

app.Run();

// DTOs
record PrintRequest(int Copies = 1, int Rotation = 0);
