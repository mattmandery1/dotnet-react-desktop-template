using Dotnet10Template.Desktop.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;

namespace Dotnet10Template.Desktop
{
    public sealed partial class MainWindow : Window
    {
        private const string AppHostName = "app.local";
        private readonly DesktopApiHost apiHost = new();
        private readonly HttpClient apiClient = new(new HttpClientHandler
        {
            AllowAutoRedirect = false
        });
        private string? appFolder;

        public MainWindow()
        {
            InitializeComponent();
            AppWebView.Loaded += AppWebView_Loaded;
            Closed += MainWindow_Closed;
        }

        private async void AppWebView_Loaded(object sender, RoutedEventArgs e)
        {
            AppWebView.Loaded -= AppWebView_Loaded;

            try
            {
                appFolder = GetReactAppFolder();
                var indexPath = Path.Combine(appFolder, "index.html");

                if (!File.Exists(indexPath))
                {
                    throw new FileNotFoundException("The bundled React app was not found. Build the desktop project to generate and copy the web assets.", indexPath);
                }

                await apiHost.StartAsync();
                await AppWebView.EnsureCoreWebView2Async();
                RegisterAppHost();

                AppWebView.Source = new Uri($"https://{AppHostName}/index.html");
            }
            catch (Exception ex)
            {
                ShowStartupError(ex);
            }
        }

        private async void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            if (AppWebView.CoreWebView2 is not null)
            {
                AppWebView.CoreWebView2.WebResourceRequested -= CoreWebView2_WebResourceRequested;
            }

            apiClient.Dispose();
            await apiHost.DisposeAsync();
        }

        private void RegisterAppHost()
        {
            AppWebView.CoreWebView2.AddWebResourceRequestedFilter(
                "*",
                CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
            AppWebView.CoreWebView2.WebResourceRequested += CoreWebView2_WebResourceRequested;
        }

        private async void CoreWebView2_WebResourceRequested(
            CoreWebView2 sender,
            CoreWebView2WebResourceRequestedEventArgs args)
        {
            var requestUri = new Uri(args.Request.Uri);

            if (!IsAppRequest(requestUri))
            {
                return;
            }

            var deferral = args.GetDeferral();

            try
            {
                if (IsApiBridgeRequest(requestUri))
                {
                    args.Response = await ForwardApiRequestAsync(args.Request, requestUri);
                    return;
                }

                args.Response = CreateStaticAssetResponse(requestUri);
            }
            catch (Exception ex)
            {
                args.Response = CreateTextResponse(
                    502,
                    "Bad Gateway",
                    $"Unable to forward request to the API. {ex.Message}");
            }
            finally
            {
                deferral.Complete();
            }
        }

        private CoreWebView2WebResourceResponse CreateStaticAssetResponse(Uri requestUri)
        {
            var assetPath = GetStaticAssetPath(requestUri);

            if (!File.Exists(assetPath))
            {
                assetPath = Path.Combine(appFolder ?? string.Empty, "index.html");
            }

            var body = File.ReadAllBytes(assetPath);

            return AppWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                new MemoryStream(body).AsRandomAccessStream(),
                200,
                "OK",
                $"Content-Type: {GetContentType(assetPath)}\r\n");
        }

        private string GetStaticAssetPath(Uri requestUri)
        {
            var root = Path.GetFullPath(appFolder ?? string.Empty);
            var relativePath = Uri.UnescapeDataString(requestUri.AbsolutePath.TrimStart('/'))
                .Replace('/', Path.DirectorySeparatorChar);

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                relativePath = "index.html";
            }

            var assetPath = Path.GetFullPath(Path.Combine(root, relativePath));

            if (!assetPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(root, "index.html");
            }

            return assetPath;
        }

        private CoreWebView2WebResourceResponse CreateTextResponse(
            int statusCode,
            string reasonPhrase,
            string content)
        {
            var body = Encoding.UTF8.GetBytes(content);
            var headers = "Content-Type: text/plain; charset=utf-8";

            return AppWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                new MemoryStream(body).AsRandomAccessStream(),
                statusCode,
                reasonPhrase,
                headers);
        }

        private async Task<CoreWebView2WebResourceResponse> ForwardApiRequestAsync(
            CoreWebView2WebResourceRequest webViewRequest,
            Uri webViewRequestUri)
        {
            using var apiRequest = new HttpRequestMessage(
                new HttpMethod(webViewRequest.Method),
                CreateApiUri(webViewRequestUri));

            CopyRequestHeaders(webViewRequest, apiRequest);

            if (webViewRequest.Content is not null)
            {
                var body = new MemoryStream();
                await webViewRequest.Content.AsStreamForRead().CopyToAsync(body);
                body.Position = 0;

                apiRequest.Content = new StreamContent(body);
                CopyRequestContentHeaders(webViewRequest, apiRequest);
            }

            using var apiResponse = await apiClient.SendAsync(apiRequest);
            var responseBody = await apiResponse.Content.ReadAsByteArrayAsync();

            return AppWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                new MemoryStream(responseBody).AsRandomAccessStream(),
                (int)apiResponse.StatusCode,
                apiResponse.ReasonPhrase ?? string.Empty,
                CreateResponseHeaders(apiResponse));
        }

        private static bool IsApiBridgeRequest(Uri requestUri)
        {
            return IsAppRequest(requestUri) &&
                requestUri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal);
        }

        private static bool IsAppRequest(Uri requestUri)
        {
            return requestUri.Scheme == Uri.UriSchemeHttps &&
                requestUri.Host.Equals(AppHostName, StringComparison.OrdinalIgnoreCase);
        }

        private Uri CreateApiUri(Uri requestUri)
        {
            return new Uri(apiHost.BaseUri, requestUri.PathAndQuery);
        }

        private void ShowStartupError(Exception exception)
        {
            StartupErrorMessage.Text = exception.Message;
            StartupError.Visibility = Visibility.Visible;
        }

        private static void CopyRequestHeaders(
            CoreWebView2WebResourceRequest webViewRequest,
            HttpRequestMessage apiRequest)
        {
            foreach (var header in webViewRequest.Headers)
            {
                if (IsSkippedRequestHeader(header.Key))
                {
                    continue;
                }

                apiRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        private static void CopyRequestContentHeaders(
            CoreWebView2WebResourceRequest webViewRequest,
            HttpRequestMessage apiRequest)
        {
            foreach (var header in webViewRequest.Headers)
            {
                if (!IsContentHeader(header.Key))
                {
                    continue;
                }

                apiRequest.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        private static string CreateResponseHeaders(HttpResponseMessage apiResponse)
        {
            var headers = new StringBuilder();

            foreach (var header in apiResponse.Headers)
            {
                AppendHeader(headers, header.Key, string.Join(", ", header.Value));
            }

            foreach (var header in apiResponse.Content.Headers)
            {
                AppendHeader(headers, header.Key, string.Join(", ", header.Value));
            }

            return headers.ToString();
        }

        private static void AppendHeader(StringBuilder headers, string name, string value)
        {
            if (IsSkippedResponseHeader(name))
            {
                return;
            }

            headers.Append(name);
            headers.Append(": ");
            headers.Append(value);
            headers.Append("\r\n");
        }

        private static bool IsContentHeader(string name)
        {
            return name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Content-Language", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSkippedRequestHeader(string name)
        {
            return IsHopByHopHeader(name) ||
                name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                IsContentHeader(name);
        }

        private static bool IsSkippedResponseHeader(string name)
        {
            return IsHopByHopHeader(name) ||
                name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsHopByHopHeader(string name)
        {
            return name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Trailer", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetContentType(string path)
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".css" => "text/css; charset=utf-8",
                ".html" => "text/html; charset=utf-8",
                ".js" => "text/javascript; charset=utf-8",
                ".json" => "application/json; charset=utf-8",
                ".png" => "image/png",
                ".svg" => "image/svg+xml",
                ".txt" => "text/plain; charset=utf-8",
                ".webmanifest" => "application/manifest+json",
                ".woff" => "font/woff",
                ".woff2" => "font/woff2",
                _ => "application/octet-stream"
            };
        }

        private static string GetReactAppFolder()
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "WebAssets"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "WebAssets")),
                Assembly.GetExecutingAssembly()
                    .GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(attribute => attribute.Key == "DesktopWebDistPath")
                    ?.Value
            };

            var appFolder = candidates.FirstOrDefault(candidate =>
                !string.IsNullOrWhiteSpace(candidate) &&
                File.Exists(Path.Combine(candidate, "index.html")));

            return appFolder
                ?? Path.Combine(AppContext.BaseDirectory, "WebAssets");
        }
    }
}
