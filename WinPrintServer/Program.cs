using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WinPrintServer
{
    class Program
    {
        private static string _uploadDir;
        private static PrintService _printService;

        static void Main(string[] args)
        {
            _uploadDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "uploads");
            if (!Directory.Exists(_uploadDir))
                Directory.CreateDirectory(_uploadDir);

            // Setup Logger
            var logger = new ConsoleLogger();
            _printService = new PrintService(logger);

            using (var listener = new HttpListener())
            {
                listener.Prefixes.Add("http://+:5000/");

                try
                {
                    listener.Start();
                    Console.WriteLine("Server started on port 5000.");
                    Console.WriteLine("Uploads dir: " + _uploadDir);
                }
                catch (HttpListenerException ex)
                {
                    Console.WriteLine("Error starting listener: " + ex.Message);
                    Console.WriteLine("Make sure to run as Administrator or add URL reservation.");
                    return;
                }

                while (true)
                {
                    try
                    {
                        var context = listener.GetContext();
                        Task.Run(() => HandleRequest(context));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Listener error: " + ex.Message);
                    }
                }
            }
        }

        static async Task HandleRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            // CORS
            response.AddHeader("Access-Control-Allow-Origin", "*");
            response.AddHeader("Access-Control-Allow-Methods", "GET, POST");

            if (request.HttpMethod == "OPTIONS")
            {
                response.Close();
                return;
            }

            string path = request.Url.AbsolutePath.ToLower();

            try
            {
                if (path == "/" || path == "/index.html")
                {
                    ServeStaticFile("index.html", "text/html", response);
                }
                else if (path.StartsWith("/api/upload") && request.HttpMethod == "POST")
                {
                    await HandleUpload(request, response);
                }
                else if (path.StartsWith("/api/preview/") && request.HttpMethod == "GET")
                {
                    HandlePreview(path, response);
                }
                else if (path.StartsWith("/api/print/") && request.HttpMethod == "POST")
                {
                    HandlePrint(path, response);
                }
                else
                {
                    response.StatusCode = 404;
                    CloseResponse(response, "Not Found");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error handling request: " + ex.ToString());
                response.StatusCode = 500;
                CloseResponse(response, "Internal Server Error: " + ex.Message);
            }
        }

        static void ServeStaticFile(string fileName, string contentType, HttpListenerResponse response)
        {
            string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", fileName);
            if (File.Exists(filePath))
            {
                byte[] buffer = File.ReadAllBytes(filePath);
                response.ContentType = contentType;
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.Close();
            }
            else
            {
                response.StatusCode = 404;
                CloseResponse(response, "File not found");
            }
        }

        static async Task HandleUpload(HttpListenerRequest request, HttpListenerResponse response)
        {
            // Expecting query param ?filename=...
            string filename = request.QueryString["filename"];
            if (string.IsNullOrEmpty(filename))
                filename = "unknown.dat";

            var id = Guid.NewGuid().ToString();
            var ext = Path.GetExtension(filename);
            var safeFilename = $"{id}{ext}";
            var filepath = Path.Combine(_uploadDir, safeFilename);

            using (var fs = new FileStream(filepath, FileMode.Create))
            {
                await request.InputStream.CopyToAsync(fs);
            }

            string json = $"{{\"id\": \"{id}\", \"filename\": \"{filename}\", \"type\": \"{ext}\"}}";
            response.ContentType = "application/json";
            CloseResponse(response, json);
        }

        static void HandlePreview(string path, HttpListenerResponse response)
        {
            // /api/preview/{id}
            string id = path.Substring("/api/preview/".Length);

            var files = Directory.GetFiles(_uploadDir, $"{id}.*");
            if (files.Length == 0)
            {
                response.StatusCode = 404;
                CloseResponse(response, "File not found");
                return;
            }

            var filepath = files[0];
            var ext = Path.GetExtension(filepath).ToLower();
             var mimeType = ext == ".jpg" || ext == ".jpeg" ? "image/jpeg" :
                           ext == ".png" ? "image/png" :
                           ext == ".bmp" ? "image/bmp" :
                           ext == ".pdf" ? "application/pdf" : "application/octet-stream";

            byte[] buffer = File.ReadAllBytes(filepath);
            response.ContentType = mimeType;
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.Close();
        }

        static void HandlePrint(string path, HttpListenerResponse response)
        {
             // /api/print/{id}
            string id = path.Substring("/api/print/".Length);

            var files = Directory.GetFiles(_uploadDir, $"{id}.*");
            if (files.Length == 0)
            {
                response.StatusCode = 404;
                CloseResponse(response, "File not found");
                return;
            }

            try
            {
                _printService.Print(files[0]);
                CloseResponse(response, "{\"message\": \"Print job started\"}");
            }
            catch (Exception ex)
            {
                 response.StatusCode = 500;
                 CloseResponse(response, ex.Message);
            }
        }

        static void CloseResponse(HttpListenerResponse response, string content)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(content);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.Close();
        }
    }

    // Simple logger adapter
    public class ConsoleLogger
    {
        public void LogInformation(string msg, params object[] args) => Console.WriteLine("[INFO] " + msg, args);
        public void LogError(string msg, params object[] args) => Console.WriteLine("[ERROR] " + msg, args);
        public void LogError(Exception ex, string msg, params object[] args)
        {
            Console.WriteLine("[ERROR] " + msg, args);
            Console.WriteLine(ex.ToString());
        }
    }
}
