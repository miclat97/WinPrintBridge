using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;

namespace WinPrintServer
{
    public class PrintService
    {
        private readonly ConsoleLogger _logger;

        public PrintService(ConsoleLogger logger)
        {
            _logger = logger;
        }

        public void Print(string filePath)
        {
            // Simple OS check - assuming Windows for Framework app mostly, but good to keep.
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                _logger.LogError("Printing is only supported on Windows.");
               // On Framework 4.7.2 running on Windows, this is always true.
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
                _logger.LogError(ex, "Error printing file {0}", filePath);
                throw;
            }
        }

        private void PrintPdf(string filePath)
        {
            _logger.LogInformation("Starting PDF print for: {0}", filePath);

            var p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName = filePath,
                Verb = "print",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            p.Start();
        }

        private void PrintImage(string filePath)
        {
            _logger.LogInformation("Starting Image print for: {0}", filePath);

            using (var printDoc = new PrintDocument())
            {
                printDoc.PrintPage += (sender, e) =>
                {
                    using (var image = Image.FromFile(filePath))
                    {
                        // Logic to fit image to page
                        RectangleF m = e.MarginBounds;

                        // Check orientation
                        if (image.Width > image.Height)
                        {
                            printDoc.DefaultPageSettings.Landscape = true;
                            m = e.MarginBounds; // Re-read
                        }

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

                        e.Graphics.DrawImage(image, x, y, width, height);
                    }
                };

                printDoc.Print();
            }
        }
    }
}
