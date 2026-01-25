using System.Diagnostics;
using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.InteropServices;

namespace WinPrintServer
{
    public class PrintService
    {
        private readonly ILogger<PrintService> _logger;

        public PrintService(ILogger<PrintService> logger)
        {
            _logger = logger;
        }

        public void Print(string filePath)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _logger.LogError("Printing is only supported on Windows.");
                throw new PlatformNotSupportedException("Printing is only supported on Windows.");
            }

            string extension = Path.GetExtension(filePath).ToLower();

            try
            {
                if (extension == ".pdf")
                {
                    PrintPdf(filePath);
                }
                else if (extension == ".jpg" || extension == ".jpeg" || extension == ".png" || extension == ".bmp")
                {
                    PrintImage(filePath);
                }
                else
                {
                    throw new NotSupportedException($"File type {extension} is not supported.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error printing file {FilePath}", filePath);
                throw;
            }
        }

        private void PrintPdf(string filePath)
        {
            _logger.LogInformation("Starting PDF print for: {FilePath}", filePath);

            var p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName = filePath,
                Verb = "print",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            // "printto" allows specifying a printer, but "print" uses default.
            // The user implies default printer usage or "connected printer".
            // Using default is safer unless we implement printer selection.

            p.Start();

            // Allow some time for the print command to be handed off to the application
            // Ideally we don't wait indefinitely, but the process might exit immediately after spooling
            // or stick around (like Acrobat). We won't WaitForExit because it might block if the reader stays open.
        }

        private void PrintImage(string filePath)
        {
            _logger.LogInformation("Starting Image print for: {FilePath}", filePath);

            using var printDoc = new PrintDocument();
            printDoc.PrintPage += (sender, e) =>
            {
                using var image = Image.FromFile(filePath);

                // Logic to fit image to page
                RectangleF m = e.MarginBounds;
                RectangleF p = e.PageBounds;

                // Check orientation
                if (image.Width > image.Height)
                {
                    // Image is Landscape. If page is Portrait, rotate image logic or rotate page setting?
                    // Easiest is to just fit inside the margins.
                    // But if we want to maximize usage, we might want to rotate the PageSettings.
                    printDoc.DefaultPageSettings.Landscape = true;
                    // Re-read margins after changing orientation
                     m = e.MarginBounds;
                }

                // Calculate scaling
                float imageRatio = (float)image.Width / image.Height;
                float containerRatio = m.Width / m.Height;

                float width, height;

                if (imageRatio >= containerRatio)
                {
                    // Image is wider than container relative to height
                    width = m.Width;
                    height = width / imageRatio;
                }
                else
                {
                    // Image is taller
                    height = m.Height;
                    width = height * imageRatio;
                }

                // Center image
                float x = m.Left + (m.Width - width) / 2;
                float y = m.Top + (m.Height - height) / 2;

                e.Graphics.DrawImage(image, x, y, width, height);
            };

            printDoc.Print();
        }
    }
}
