using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Dotnet10Template.Desktop
{
    public sealed partial class MainWindow : Window
    {
        private const string AppHostName = "app.local";

        public MainWindow()
        {
            InitializeComponent();
            AppWebView.Loaded += AppWebView_Loaded;
        }

        private async void AppWebView_Loaded(object sender, RoutedEventArgs e)
        {
            AppWebView.Loaded -= AppWebView_Loaded;

            var appFolder = GetReactAppFolder();
            var indexPath = Path.Combine(appFolder, "index.html");

            if (!File.Exists(indexPath))
            {
                throw new FileNotFoundException("The bundled React app was not found. Build the desktop project to generate and copy the web assets.", indexPath);
            }

            await AppWebView.EnsureCoreWebView2Async();
            AppWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                AppHostName,
                appFolder,
                CoreWebView2HostResourceAccessKind.Allow);

            AppWebView.Source = new Uri($"https://{AppHostName}/index.html");
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
