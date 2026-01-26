using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using WinPrintServer;
using Microsoft.Extensions.Hosting.WindowsServices;
using System.Diagnostics;

var options = new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService()
                      ? AppContext.BaseDirectory : default
};

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
var uploadDir = Path.Combine(app.Environment.ContentRootPath, "uploads");
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
        return Results.Ok(new { message = "Kolejka wydruku wyczyszczona (Spooler zrestartowany)." });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Błąd: {ex.Message}");
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

        return Results.Ok(new { message = "Serwer jest restartowany..." });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Błąd restartu: {ex.Message}");
    }
});

app.Run();
