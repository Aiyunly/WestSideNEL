using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop; // 必须引入这个来获取 WindowHandle
using Microsoft.Web.WebView2.Core;
using WestSide.Manager;
using Serilog;
using System.Text.Json;

namespace WestSide.UI.Bridge;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent(); 
        
        this.SizeChanged += (_, _) => WindowHandler.OnWindowSizeChanged();

        // 【修改这里】不要在构造函数直接调用，而是绑定到 Loaded 事件
        this.Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 此时 WPF 的 Event Loop 已经启动，可以安全初始化 WebView2 了
        InitializeWebViewAsync();
    }

    // ========== 补全 Handler 需要的桥接属性和方法 ==========
    
    // 获取原生 Win32 窗口句柄 (供 WindowEffects, SystemHandler, WindowHandler 使用)
    public IntPtr WindowHandle => new WindowInteropHelper(this).Handle;

    public bool Maximized => this.WindowState == WindowState.Maximized;

    public void SetMaximized(bool maximize)
    {
        this.WindowState = maximize ? WindowState.Maximized : WindowState.Normal;
    }

    public void SetMinimized(bool minimize)
    {
        if (minimize) this.WindowState = WindowState.Minimized;
    }

    public void SendWebMessage(string json)
    {
        if (MainWebView?.CoreWebView2 != null)
        {
            this.Dispatcher.InvokeAsync(() => 
            {
                try { MainWebView.CoreWebView2.PostWebMessageAsString(json); }
                catch { }
            });
        }
    }
    // =========================================================

    private async void InitializeWebViewAsync()
    {
        try
        {
            var settings = SettingManager.Instance.Get();

            var webviewDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WestSide", "EBWebView");
            Directory.CreateDirectory(webviewDataDir);

            // 【回归正轨 1】调用你原本完美的提取逻辑，拿到真实的 wwwroot 物理路径
            var wwwroot = ResourceExtractor.Extract();

            var env = await CoreWebView2Environment.CreateAsync(null, webviewDataDir);
            await MainWebView.EnsureCoreWebView2Async(env);

            MainWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;

            // 【回归正轨 2】使用官方原生映射：将你提取的文件夹映射为 http://app.local
            // 这种方式绝不会掉文件，完美兼容任何前端路由、JS、CSS 和字体！
            MainWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "app.local", 
                wwwroot, 
                CoreWebView2HostResourceAccessKind.Allow);

            // 消息接收机制 (加入异常捕获防崩溃)
            MainWebView.CoreWebView2.WebMessageReceived += (_, e) => 
            {
                string rawMessage;
                try { rawMessage = e.TryGetWebMessageAsString(); }
                catch { rawMessage = e.WebMessageAsJson; }
                MessageRouter.HandleMessage(MainWebView, rawMessage);
            };

            // 【回归正轨 3】只做最基础的 Photino API 兼容，绝不造假 Bridge 对象！
            // 让你的前端框架重新接管控制权。
            await MainWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                window.external = window.external || {};
                window.external.sendMessage = function(msg) { window.chrome.webview.postMessage(msg); };
                window.external.receiveMessage = function(cb) {
                    window.chrome.webview.addEventListener('message', function(e) { cb(e.data); });
                };
            ");

            WindowHandler.ApplyRoundedCorners();
            WindowEffects.Apply(settings.Backdrop);
            Serilog.Log.Information("WPF 窗口已初始化，已应用圆角");

            // 导航到首页
            MainWebView.Source = new Uri("http://app.local/index.html");
        }
        catch (Exception ex)
        {
            Serilog.Log.Fatal(ex, "WebView2 初始化发生致命错误！");
            MessageBox.Show($"UI 引擎初始化失败，程序即将退出。\n\n错误信息：{ex.Message}", 
                            "WestSide 致命错误", 
                            MessageBoxButton.OK, 
                            MessageBoxImage.Error);
            Application.Current?.Shutdown();
        }
    }

    private void CoreWebView2_WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = new Uri(e.Request.Uri);
        var path = uri.AbsolutePath.TrimStart('/');
        if (string.IsNullOrEmpty(path)) path = "index.html";

        var resourcePath = "WestSide.UI.wwwroot." + path.Replace('/', '.').Replace('-', '_');
        
        var assembly = Assembly.GetExecutingAssembly();
        var stream = assembly.GetManifestResourceStream(resourcePath);

        if (stream != null)
        {
            string ext = Path.GetExtension(path).ToLower();
            string mimeType = ext switch
            {
                ".html" => "text/html",
                ".js" => "application/javascript",
                ".css" => "text/css",
                ".json" => "application/json",
                ".png" => "image/png",
                ".svg" => "image/svg+xml",
                ".woff2" => "font/woff2",
                _ => "application/octet-stream"
            };

            e.Response = MainWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                stream, 200, "OK", $"Content-Type: {mimeType}");
        }
        else
        {
            Log.Warning("前端请求了不存在的资源: {Path}", resourcePath);
            e.Response = MainWebView.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
        }
    }
}

public static class AppWindow
{
    private static MainWindow? _mainWindow;
    private static Application? _app;

    // 暴露 Instance 供各种 Handler 调用
    public static MainWindow? Instance => _mainWindow;

    public static void Run()
    {
        _app = new Application();
        _mainWindow = new MainWindow();
        
        var iconPath = Path.Combine(AppContext.BaseDirectory, "WestSide.ico");
        if (File.Exists(iconPath))
            _mainWindow.Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri(iconPath));

        Log.Information("WPF Application 启动");
        _app.Run(_mainWindow);
    }

    public static void PushEvent(string action, object? data = null)
    {
        var json = JsonSerializer.Serialize(new { action, requestId = "", success = true, data });
        _mainWindow?.SendWebMessage(json);
    }

    public static void PushNotification(string message, string level)
    {
        PushEvent("notify", new { message, level });
    }
}