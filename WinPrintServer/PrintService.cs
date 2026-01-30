using PdfiumViewer;
using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.InteropServices;

namespace WinPrintBridge
{
    public class PrintService
    {
        private readonly ILogger<PrintService> _logger;
        private readonly SettingsService _settingsService;

        public PrintService(ILogger<PrintService> logger, SettingsService settingsService)
        {
            _logger = logger;
            _settingsService = settingsService;
        }

        private string? GetPrinterName()
        {
            return _settingsService.GetSettings().PrinterName;
        }

        public void Print(string filePath, int copies = 1, int rotation = 0)
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
                    if (rotation == 0)
                    {
                        PrintPdf(filePath, copies);
                    }
                    else
                    {
                        // If rotation is requested, render to image to ensure WYSIWYG rotation
                        PrintPdfAsImage(filePath, copies, rotation);
                    }
                }
                else if (new[] { ".jpg", ".jpeg", ".png", ".bmp" }.Contains(extension))
                {
                    PrintImage(filePath, copies, rotation);
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

        private void PrintPdf(string filePath, int copies)
        {
            _logger.LogInformation("Starting PDF print for: {FilePath} (Copies: {Copies})", filePath, copies);

            try
            {
                using var document = PdfDocument.Load(filePath);
                using var printDocument = document.CreatePrintDocument();
                printDocument.PrintController = new StandardPrintController();

                var printerName = GetPrinterName();
                if (!string.IsNullOrEmpty(printerName))
                {
                    printDocument.PrinterSettings.PrinterName = printerName;
                }

                printDocument.PrinterSettings.Copies = (short)copies;

                printDocument.Print();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to print PDF {FilePath}", filePath);
                throw;
            }
        }

        private void PrintPdfAsImage(string filePath, int copies, int rotation)
        {
            _logger.LogInformation("Starting PDF (as Image) print for: {FilePath} (Rotation: {Rotation})", filePath, rotation);

            using var document = PdfDocument.Load(filePath);
            // Render first page at 300 DPI
            int pageIndex = 0; // Assumption: Single page printing or user only sees/rotates first page?
                               // Task implies "document", but preview is usually page 1.
                               // If it's a multi-page PDF, rendering page 1 is insufficient.
                               // However, implementing multi-page rasterized print is complex.
                               // For now, I will render each page and print it? No, CreatePrintDocument handles multi-page.
                               // If I must rotate, I have to assume single page OR apply to all.

            // To support multipage rotated PDF, we need to implement a custom PrintDocument that renders each page.

            using var printDoc = new PrintDocument();
            var printerName = GetPrinterName();
            if (!string.IsNullOrEmpty(printerName))
            {
                printDoc.PrinterSettings.PrinterName = printerName;
            }
            printDoc.PrinterSettings.Copies = (short)copies;

            int currentPage = 0;
            printDoc.PrintPage += (sender, e) =>
            {
                 if (currentPage >= document.PageCount)
                 {
                     e.HasMorePages = false;
                     return;
                 }

                 // Render page to bitmap
                 using var image = document.Render(currentPage, 300, 300, PdfRenderFlags.CorrectFromDpi);

                 ProcessAndDrawImage(image, e, rotation);

                 currentPage++;
                 e.HasMorePages = (currentPage < document.PageCount);
            };

            printDoc.Print();
        }

        private void PrintImage(string filePath, int copies, int rotation)
        {
            _logger.LogInformation("Starting Image print for: {FilePath}", filePath);

            using var printDoc = new PrintDocument();

            var printerName = GetPrinterName();
            if (!string.IsNullOrEmpty(printerName))
            {
                printDoc.PrinterSettings.PrinterName = printerName;
            }
            printDoc.PrinterSettings.Copies = (short)copies;

            printDoc.PrintPage += (sender, e) =>
            {
                using var image = Image.FromFile(filePath);
                ProcessAndDrawImage(image, e, rotation);
            };

            printDoc.Print();
        }

        private void ProcessAndDrawImage(Image image, PrintPageEventArgs e, int rotation)
        {
            // Apply rotation
            RotateFlipType rotateType = RotateFlipType.RotateNoneFlipNone;
            switch (rotation % 360)
            {
                case 90: rotateType = RotateFlipType.Rotate90FlipNone; break;
                case 180: rotateType = RotateFlipType.Rotate180FlipNone; break;
                case 270: rotateType = RotateFlipType.Rotate270FlipNone; break;
            }

            if (rotateType != RotateFlipType.RotateNoneFlipNone)
            {
                image.RotateFlip(rotateType);
            }

            // Logic to fit image to page
            RectangleF m = e.MarginBounds;

            // Auto-rotate paper logic?
            // If image is wider than tall, and paper is tall, switch paper to landscape?
            bool imageIsLandscape = image.Width > image.Height;
            bool paperIsLandscape = m.Width > m.Height;

            // Simple logic: If mismatch, rotate page settings?
            // Note: Changing PageSettings inside PrintPage often doesn't work for the *current* page on some drivers.
            // But we can try setting e.PageSettings.Landscape

            // Just fit best we can.

            // Calculate scaling
            float imageRatio = (float)image.Width / image.Height;
            float containerRatio = m.Width / m.Height;

            float width, height;

            if (imageRatio >= containerRatio)
            {
                width = m.Width;
                height = width / imageRatio;
            }
            else
            {
                height = m.Height;
                width = height * imageRatio;
            }

            // Center image
            float x = m.Left + (m.Width - width) / 2;
            float y = m.Top + (m.Height - height) / 2;

            e.Graphics?.DrawImage(image, x, y, width, height);
        }

        public Stream RenderPdfPage(string filePath, int pageIndex = 0)
        {
             if (!File.Exists(filePath)) throw new FileNotFoundException("File not found", filePath);

             // Check if it's PDF
             if (Path.GetExtension(filePath).ToLower() != ".pdf")
             {
                 throw new InvalidOperationException("Not a PDF file");
             }

             using var document = PdfDocument.Load(filePath);
             // Render to memory stream
             // Use 96 DPI for preview (screen)
             var image = document.Render(pageIndex, 96, 96, PdfRenderFlags.CorrectFromDpi);
             var ms = new MemoryStream();
             image.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
             ms.Position = 0;
             return ms;
        }
    }
}
