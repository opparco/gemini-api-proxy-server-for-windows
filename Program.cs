using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Http;
using System.Threading;
using System.Linq;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using System.Reflection;

namespace GeminiApiProxy
{
    public class ProxySettings
    {
        public required string TargetHost { get; set; }
        public required string ImageGenerationUrlPattern { get; set; }
        public int ProxyPort { get; set; }
        public required string BaseLogDir { get; set; }
        public required string PlaceholderImagePath { get; set; }
        public required string MockImageTemplatePath { get; set; }
    }

    public static class SessionUserDataExtensions
    {
        public static Dictionary<string, object>? GetUserData(this SessionEventArgs e)
        {
            return e.UserData as Dictionary<string, object>;
        }

        public static T? Get<T>(this SessionEventArgs e, string key)
        {
            if (e.UserData is Dictionary<string, object> dict && dict.TryGetValue(key, out var val) && val is T tVal)
                return tVal;
            return default;
        }

        public static void Set<T>(this SessionEventArgs e, string key, T value)
        {
            if (e.UserData == null || e.UserData is not Dictionary<string, object> dict)
            {
                dict = new Dictionary<string, object>();
                e.UserData = dict;
            }
            else
            {
                dict = (Dictionary<string, object>)e.UserData;
            }
            dict[key] = value!;
        }
    }

    class Program
    {
        static readonly ProxyServer proxyServer = new ProxyServer();
        static string currentLogDir;
        static int sequenceId = 1;
        static bool productionMode = true;
        static string placeholderImageBase64;
        static readonly object locker = new object();
        static bool isInitialized = false;
        static ProxySettings settings;
        static string mockImageTemplateJson;

        static async Task Main(string[] args)
        {
            Console.WriteLine("Gemini API Proxy - Windows CLI Edition");
            Console.WriteLine("=====================================");

            try
            {
                // Load configuration
                LoadConfiguration();

                await Initialize();
                await RunProxyServer();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Critical error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
            }
        }

        static void LoadConfiguration()
        {
            try
            {
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                    .Build();

                settings = configuration.GetSection("ProxySettings").Get<ProxySettings>();

                if (settings == null)
                {
                    Console.WriteLine("Warning: Could not load configuration from appsettings.json. Using default values.");
                    settings = GetDefaultSettings();
                }

                Console.WriteLine("Configuration loaded successfully");
                Console.WriteLine($"Target Host: {settings.TargetHost}");
                Console.WriteLine($"Proxy Port: {settings.ProxyPort}");
            }
            catch (Exception ex)
            {
                HandleError("loading configuration", ex, () => settings = GetDefaultSettings());
            }
        }

        static async Task Initialize()
        {
            try
            {
                // Create base log directory
                Directory.CreateDirectory(settings.BaseLogDir);

                // Create templates directory if it doesn't exist
                string templateDir = Path.GetDirectoryName(settings.MockImageTemplatePath);
                if (!string.IsNullOrEmpty(templateDir))
                {
                    Directory.CreateDirectory(templateDir);
                }

                // Create initial log directory
                CreateNewLogDirectory();

                // Load placeholder image
                LoadPlaceholderImage();

                // Load mock templates
                LoadMockTemplates();

                // Configure proxy server
                proxyServer.CertificateManager.CreateRootCertificate();
                proxyServer.CertificateManager.TrustRootCertificate();

                var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, settings.ProxyPort, true);
                proxyServer.AddEndPoint(explicitEndPoint);

                // Subscribe to events
                proxyServer.BeforeRequest += OnRequest;
                proxyServer.BeforeResponse += OnResponse;

                var (proxyEnabled, proxyServerAuthority) = GetWindowsProxySettings();
                Console.WriteLine("\nCurrent Windows Proxy Settings:");
                Console.WriteLine($"Status: {(proxyEnabled ? "Enabled" : "Disabled")}");
                if (proxyEnabled)
                {
                    Console.WriteLine($"Proxy Server: {proxyServerAuthority}");

                    bool isOwnProxy = proxyServerAuthority.Contains($"127.0.0.1:{settings.ProxyPort}");
                    if (isOwnProxy)
                    {
                        Console.WriteLine("This proxy is already set as the system proxy.");
                    }
                    else
                    {
                        Console.WriteLine("Warning: Another proxy is currently active.");
                        Console.WriteLine($"To use this proxy, press 'p' after startup to set 127.0.0.1:{settings.ProxyPort}");
                    }
                }
                else
                {
                    Console.WriteLine("No proxy server is currently active.");
                    Console.WriteLine($"Press 'p' after startup to enable this proxy (127.0.0.1:{settings.ProxyPort})");
                }
                Console.WriteLine();

                isInitialized = true;
                Console.WriteLine("Initialization completed successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Initialization failed: {ex.Message}");
                throw;
            }
        }

        static void LoadMockTemplates()
        {
            try
            {
                if (File.Exists(settings.MockImageTemplatePath))
                {
                    mockImageTemplateJson = File.ReadAllText(settings.MockImageTemplatePath);
                    Console.WriteLine("Mock image template loaded successfully");
                }
                else
                {
                    Console.WriteLine($"Mock image template not found: {settings.MockImageTemplatePath}");
                    Console.WriteLine("Creating default template file...");

                    mockImageTemplateJson = GetDefaultMockImageTemplate();

                    Directory.CreateDirectory(Path.GetDirectoryName(settings.MockImageTemplatePath));
                    File.WriteAllText(settings.MockImageTemplatePath, mockImageTemplateJson);
                    Console.WriteLine("Default mock image template created");
                }
            }
            catch (Exception ex)
            {
                HandleError("loading mock templates", ex, () => mockImageTemplateJson = GetDefaultMockImageTemplate());
            }
        }

        static async Task RunProxyServer()
        {
            if (!isInitialized)
            {
                Console.WriteLine("Cannot start proxy server - not initialized");
                return;
            }

            try
            {
                Console.WriteLine("Starting proxy server...");
                proxyServer.Start();

                Console.WriteLine($"Proxy server running on 127.0.0.1:{settings.ProxyPort}");
                Console.WriteLine("Press 'p' to enable Windows proxy settings");
                Console.WriteLine("Press 'u' to disable Windows proxy settings");
                Console.WriteLine("Press 'm' to toggle between Production and Mock modes");
                Console.WriteLine("Press 'n' to create a new log directory");
                Console.WriteLine("Press 'r' to reload templates");
                Console.WriteLine("Press 'q' to exit");

                // Command handling loop
                while (true)
                {
                    var key = Console.ReadKey(true);

                    switch (key.KeyChar)
                    {
                        case 'p':
                            SetWindowsProxy(true);
                            break;
                        case 'u':
                            SetWindowsProxy(false);
                            break;
                        case 'm':
                            ToggleMode();
                            break;
                        case 'n':
                            CreateNewLogDirectory();
                            break;
                        case 'r':
                            LoadMockTemplates();
                            Console.WriteLine("Templates reloaded");
                            break;
                        case 'q':
                            SetWindowsProxy(false);
                            await ShutdownProxy();
                            return;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error running proxy server: {ex.Message}");
                await ShutdownProxy();
                throw;
            }
        }

        static string CreateNewLogDirectory()
        {
            lock (locker)
            {
                try
                {
                    sequenceId = 1;
                    string dirUuid = Guid.NewGuid().ToString();
                    currentLogDir = Path.Combine(settings.BaseLogDir, dirUuid);
                    Directory.CreateDirectory(currentLogDir);
                    Console.WriteLine($"Created new log directory: {currentLogDir}");
                    return currentLogDir;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error creating log directory: {ex.Message}");
                    // Fallback to base directory
                    currentLogDir = settings.BaseLogDir;
                    return currentLogDir;
                }
            }
        }

        static bool ToggleMode()
        {
            productionMode = !productionMode;
            string modeName = productionMode ? "Production" : "Mock";
            Console.WriteLine($"Switched to {modeName} mode");
            return productionMode;
        }

        static void LoadPlaceholderImage()
        {
            try
            {
                if (File.Exists(settings.PlaceholderImagePath))
                {
                    byte[] imageBytes = File.ReadAllBytes(settings.PlaceholderImagePath);
                    placeholderImageBase64 = Convert.ToBase64String(imageBytes);
                    Console.WriteLine("Placeholder image loaded successfully");
                }
                else
                {
                    Console.WriteLine($"Placeholder image not found: {settings.PlaceholderImagePath}");
                    Console.WriteLine("Using embedded placeholder image...");
                    placeholderImageBase64 = GetDefaultPlaceholderImageBase64();
                }
            }
            catch (Exception ex)
            {
                HandleError("loading placeholder image", ex, () => placeholderImageBase64 = GetDefaultPlaceholderImageBase64());
            }
        }

        static async Task OnRequest(object sender, SessionEventArgs e)
        {
            string url = e.HttpClient.Request.Url;

            if (url.Contains(settings.TargetHost))
            {
                EnsureLogDirectoryExists();

                string method = e.HttpClient.Request.Method.ToUpper();
                bool isImageGeneration = url.Contains(settings.ImageGenerationUrlPattern);

                int currentSequenceId;
                lock (locker)
                {
                    currentSequenceId = sequenceId++;
                }

                e.Set("LogTarget", true);
                e.Set("SequenceId", currentSequenceId);
                e.Set("IsImageGeneration", isImageGeneration);

                try
                {
                    byte[] requestBody = e.HttpClient.Request.HasBody ? await e.GetRequestBody() : [];

                    var metadataLines = new List<string>
                    {
                        $"{method} {url}",
                        "",
                        "Headers:"
                    };
                    foreach (var header in e.HttpClient.Request.Headers)
                    {
                        metadataLines.Add($"{header.Name}: {header.Value}");
                    }

                    string metadataFilePath = Path.Combine(currentLogDir, $"{currentSequenceId:D3}_request_metadata.txt");
                    File.WriteAllText(metadataFilePath, string.Join(Environment.NewLine, metadataLines), new UTF8Encoding());

                    string bodyFilePath = Path.Combine(currentLogDir, $"{currentSequenceId:D3}_request_body.txt");
                    File.WriteAllBytes(bodyFilePath, requestBody);

                    Console.WriteLine($"Saved request {currentSequenceId:D3} - {method} {url}");
                }
                catch (Exception ex)
                {
                    HandleError("saving request", ex);
                }

                if (!productionMode && isImageGeneration && method == "POST")
                {
                    try
                    {
                        Console.WriteLine("Generating mock image response");
                        string mockResponse = CreateMockImageResponse();

                        e.Ok(mockResponse, new List<HttpHeader> {
                            new HttpHeader("Content-Type", "application/json")
                        });
                    }
                    catch (Exception ex)
                    {
                        HandleError("generating mock response", ex);
                    }
                }
            }
        }

        static async Task OnResponse(object sender, SessionEventArgs e)
        {
            if (e.Get<bool>("LogTarget") == true)
            {
                EnsureLogDirectoryExists();

                int sequenceId = e.Get<int>("SequenceId");
                bool isImageGeneration = e.Get<bool>("IsImageGeneration");

                try
                {
                    byte[] responseBody = e.HttpClient.Response.HasBody ? await e.GetResponseBody() : [];

                    var metadataLines = new List<string>
                    {
                        $"HTTP/1.1 {e.HttpClient.Response.StatusCode} {GetStatusDescription(e.HttpClient.Response.StatusCode)}",
                        "",
                        "Headers:"
                    };
                    foreach (var header in e.HttpClient.Response.Headers)
                    {
                        metadataLines.Add($"{header.Name}: {header.Value}");
                    }

                    string metadataFilePath = Path.Combine(currentLogDir, $"{sequenceId:D3}_response_metadata.txt");
                    File.WriteAllText(metadataFilePath, string.Join(Environment.NewLine, metadataLines), new UTF8Encoding());

                    string bodyFilePath = Path.Combine(currentLogDir, $"{sequenceId:D3}_response_body.txt");
                    File.WriteAllBytes(bodyFilePath, responseBody);

                    Console.WriteLine($"Saved response {sequenceId:D3}");

                    if (productionMode && isImageGeneration && e.HttpClient.Response.StatusCode == 200)
                    {
                        try
                        {
                            using (JsonDocument document = JsonDocument.Parse(responseBody))
                            {
                                JsonElement root = document.RootElement;

                                if (root.TryGetProperty("candidates", out JsonElement candidates) &&
                                    candidates.GetArrayLength() > 0)
                                {
                                    JsonElement candidate = candidates[0];

                                    if (candidate.TryGetProperty("content", out JsonElement content) &&
                                        content.TryGetProperty("parts", out JsonElement parts))
                                    {
                                        for (int i = 0; i < parts.GetArrayLength(); i++)
                                        {
                                            JsonElement part = parts[i];

                                            if (part.TryGetProperty("inlineData", out JsonElement inlineData) &&
                                                inlineData.TryGetProperty("data", out JsonElement data))
                                            {
                                                string imageDataBase64 = data.GetString() ?? string.Empty;
                                                string mimeType = "image/png";

                                                if (inlineData.TryGetProperty("mimeType", out JsonElement mimeTypeElement))
                                                {
                                                    mimeType = mimeTypeElement.GetString() ?? "image/png";
                                                }

                                                if (!string.IsNullOrEmpty(imageDataBase64))
                                                {
                                                    byte[] imageData = Convert.FromBase64String(imageDataBase64);
                                                    string extension = mimeType.Contains("/") ? mimeType.Split('/')[1] : "png";
                                                    string imageFilename = $"{sequenceId:D3}_generated_image_{i}.{extension}";
                                                    string imagePath = Path.Combine(currentLogDir, imageFilename);

                                                    File.WriteAllBytes(imagePath, imageData);
                                                    Console.WriteLine($"Saved generated image to {imageFilename}");
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            HandleError("extracting image data", ex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    HandleError("saving response", ex);
                }
            }
        }

        static object TryParseJson(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }

            try
            {
                using (JsonDocument document = JsonDocument.Parse(text))
                {
                    return document.RootElement.Clone();
                }
            }
            catch
            {
                return text;
            }
        }

        static string CreateMockImageResponse()
        {
            // Create a mock response for image generation requests
            // Ensure placeholder image is loaded
            if (string.IsNullOrEmpty(placeholderImageBase64))
            {
                LoadPlaceholderImage();
            }

            try
            {
                // Use the mock template and replace the placeholder with the actual image base64
                string response = mockImageTemplateJson.Replace("PLACEHOLDER_IMAGE_BASE64", placeholderImageBase64);

                // Validate that the response is valid JSON
                JsonDocument.Parse(response);

                return response;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating mock response from template: {ex.Message}");
                Console.WriteLine("Falling back to hardcoded template");

                // Fallback to hardcoded template if there's an error with the template file
                var mockResponse = new
                {
                    candidates = new[]
                    {
                        new
                        {
                            content = new
                            {
                                parts = new[]
                                {
                                    new
                                    {
                                        inlineData = new
                                        {
                                            mimeType = "image/png",
                                            data = placeholderImageBase64
                                        }
                                    }
                                },
                                role = "model"
                            },
                            finishReason = "STOP",
                            index = 0
                        }
                    },
                    usageMetadata = new
                    {
                        promptTokenCount = 1263,
                        totalTokenCount = 1263,
                        promptTokensDetails = new[]
                        {
                            new
                            {
                                modality = "TEXT",
                                tokenCount = 231
                            },
                            new
                            {
                                modality = "IMAGE",
                                tokenCount = 1032
                            }
                        }
                    },
                    modelVersion = "gemini-2.0-flash-exp-image-generation"
                };

                return JsonSerializer.Serialize(mockResponse);
            }
        }

        static void EnsureLogDirectoryExists()
        {
            if (string.IsNullOrEmpty(currentLogDir) || !Directory.Exists(currentLogDir))
            {
                CreateNewLogDirectory();
            }
        }

        static async Task ShutdownProxy()
        {
            try
            {
                Console.WriteLine("Stopping proxy server...");
                proxyServer.Stop();

                try
                {
                    // Clean up certificates
                    proxyServer.CertificateManager.RemoveTrustedRootCertificate();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Could not remove root certificate: {ex.Message}");
                }

                Console.WriteLine("Proxy server stopped.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during shutdown: {ex.Message}");
            }
        }

        static ProxySettings GetDefaultSettings()
        {
            return new ProxySettings
            {
                TargetHost = "generativelanguage.googleapis.com",
                ImageGenerationUrlPattern = "gemini-2.0-flash-exp-image-generation",
                ProxyPort = 8080,
                BaseLogDir = "logs",
                PlaceholderImagePath = "placeholder.png",
                MockImageTemplatePath = "templates/mockImageTemplate.json"
            };
        }

        static string GetDefaultPlaceholderImageBase64()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourcePath = $"app.Resources.placeholder.png";

            using (Stream stream = assembly.GetManifestResourceStream(resourcePath))
            {
                if (stream == null)
                {
                    throw new FileNotFoundException($"Resource placeholder.png not found.");
                }

                using (MemoryStream ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
        }

        static string GetDefaultMockImageTemplate()
        {
            return ReadEmbeddedResource("mockImageTemplate.json");
        }

        static string ReadEmbeddedResource(string resourceName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourcePath = $"app.Resources.{resourceName}";

            using (Stream stream = assembly.GetManifestResourceStream(resourcePath))
            {
                if (stream == null)
                {
                    throw new FileNotFoundException($"Resource {resourceName} not found.");
                }

                using (StreamReader reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        static string SerializeToJson(object obj)
        {
            return JsonSerializer.Serialize(obj, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }

        static void HandleError(string context, Exception ex, Action fallback = null)
        {
            Console.WriteLine($"Error in {context}: {ex.Message}");
            fallback?.Invoke();
        }

        static string GetStatusDescription(int statusCode)
        {
            return statusCode switch
            {
                200 => "OK",
                201 => "Created",
                204 => "No Content",
                400 => "Bad Request",
                401 => "Unauthorized",
                403 => "Forbidden",
                404 => "Not Found",
                500 => "Internal Server Error",
                _ => "Unknown"
            };
        }

        static (bool proxyEnabled, string proxyServerAuthority) GetWindowsProxySettings()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", false);

                if (key == null)
                {
                    Console.WriteLine("Cannot open registry key for proxy settings");
                    return (false, string.Empty);
                }

                var enableValue = key.GetValue("ProxyEnable");
                bool proxyEnabled = enableValue != null && Convert.ToInt32(enableValue) == 1;

                string proxyServerAuthority = key.GetValue("ProxyServer") as string ?? string.Empty;

                return (proxyEnabled, proxyServerAuthority);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading Windows proxy settings: {ex.Message}");
                return (false, string.Empty);
            }
        }

        static bool SetWindowsProxy(bool enable)
        {
            try
            {
                var (proxyEnabled, proxyServerAuthority) = GetWindowsProxySettings();

                if (proxyEnabled == enable)
                {
                    if (enable && proxyServerAuthority.Contains($"127.0.0.1:{settings.ProxyPort}"))
                    {
                        Console.WriteLine($"Windows proxy is already enabled and set to 127.0.0.1:{settings.ProxyPort}");
                        return true;
                    }
                    else if (!enable)
                    {
                        Console.WriteLine("Windows proxy is already disabled");
                        return true;
                    }
                }

                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);

                if (key == null)
                {
                    Console.WriteLine("Cannot open registry key for proxy settings");
                    return false;
                }

                key.SetValue("ProxyEnable", enable ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);

                if (enable)
                {
                    key.SetValue("ProxyServer", $"127.0.0.1:{settings.ProxyPort}", Microsoft.Win32.RegistryValueKind.String);
                }

                Console.WriteLine($"Windows proxy {(enable ? "enabled" : "disabled")} successfully");
                if (enable)
                {
                    Console.WriteLine($"Proxy server set to 127.0.0.1:{settings.ProxyPort}");
                }

                RefreshInternetSettings();

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting Windows proxy: {ex.Message}");
                return false;
            }
        }

        static void RefreshInternetSettings()
        {
            try
            {
                NativeMethods.InternetSetOption(IntPtr.Zero,
                    NativeMethods.INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
                NativeMethods.InternetSetOption(IntPtr.Zero,
                    NativeMethods.INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not refresh Internet settings: {ex.Message}");
            }
        }

        static class NativeMethods
        {
            public const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
            public const int INTERNET_OPTION_REFRESH = 37;

            [System.Runtime.InteropServices.DllImport("wininet.dll", SetLastError = true)]
            public static extern bool InternetSetOption(IntPtr hInternet, int dwOption,
                IntPtr lpBuffer, int dwBufferLength);
        }
    }
}
