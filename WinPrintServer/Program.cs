using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using WinPrintServer;

var builder = WebApplication.CreateBuilder(args);

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

app.Run();
