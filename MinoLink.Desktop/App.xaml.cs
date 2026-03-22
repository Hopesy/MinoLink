using System.Drawing;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MinoLink.ClaudeCode;
using MinoLink.Core;
using MinoLink.Core.Interfaces;
using MinoLink.Core.Models;
using MinoLink.Desktop.Services;
using MinoLink.Feishu;
using WinForms = System.Windows.Forms;

namespace MinoLink.Desktop;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private WinForms.NotifyIcon? _notifyIcon;
    private bool _isExiting;
    private MenuItem? _autoStartMenuItem;
    private ContextMenu? _trayContextMenu;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(
                $"发生未处理异常：\n{args.Exception}",
                "MinoLink 错误", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        try
        {
            _host = BuildHost();

            SetupNotifyIcon();

            await _host.StartAsync();

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Closing += (_, args) =>
            {
                if (!_isExiting)
                {
                    args.Cancel = true;
                    _trayContextMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
                    mainWindow.Hide();
                }
            };
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"启动失败：\n{ex}", "MinoLink 错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _notifyIcon?.Dispose();

        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        base.OnExit(e);
    }

    private IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();

        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        builder.Configuration.AddJsonFile(configPath, optional: true, reloadOnChange: true);
        builder.Configuration.AddEnvironmentVariables("MINO_");

        var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        builder.Logging.ClearProviders();
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
        builder.Logging.AddProvider(new FileLoggerProvider(logDirectory));

        var config = builder.Configuration.GetSection("MinoLink").Get<MinoLinkConfig>()
            ?? throw new InvalidOperationException("配置缺失：请在 appsettings.json 中配置 MinoLink 节");

        var defaultWorkDir = ResolveDefaultWorkDir(config.Agent.WorkDir);

        builder.Services.AddSingleton<IConfigService>(new ConfigService(configPath, config));

        builder.Services.AddSingleton<AutoStartHelper>();
        builder.Services.AddSingleton<IAutoStartService>(sp => sp.GetRequiredService<AutoStartHelper>());

        builder.Services.AddSingleton<IAgent>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<ClaudeCodeAgent>>();
            return new ClaudeCodeAgent(new AgentOptions
            {
                Model = config.Agent.Model,
                Mode = config.Agent.Mode ?? "default",
            }, logger);
        });

        var sessionStoragePath = Path.Combine(AppContext.BaseDirectory, "data", "sessions.json");
        builder.Services.AddSingleton(new SessionManager(sessionStoragePath));

        builder.Services.AddSingleton<Engine>(sp =>
        {
            var agent = sp.GetRequiredService<IAgent>();
            var platforms = sp.GetServices<IPlatform>();
            var sessions = sp.GetRequiredService<SessionManager>();
            var logger = sp.GetRequiredService<ILogger<Engine>>();
            return new Engine(config.ProjectName ?? "default", agent, platforms, defaultWorkDir, sessions, logger);
        });

        if (config.Feishu is { AppId: not null and not "" })
        {
            var feishuOpts = new FeishuPlatformOptions
            {
                AppId = config.Feishu.AppId,
                AppSecret = config.Feishu.AppSecret ?? "",
                VerificationToken = config.Feishu.VerificationToken ?? "",
            };
            builder.Services.AddFeishuPlatform(feishuOpts);
            builder.Services.AddSingleton<IPlatform>(sp => sp.GetRequiredService<FeishuPlatform>());
        }

        builder.Services.AddWpfBlazorWebView();
        builder.Services.AddSingleton<MainWindow>();

        builder.Services.Configure<HostOptions>(options =>
        {
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
        });

        builder.Services.AddHostedService<EngineHostedService>();

        return builder.Build();
    }

    private void SetupNotifyIcon()
    {
        _notifyIcon = new WinForms.NotifyIcon
        {
            Text = "MinoLink",
            Visible = true,
        };

        var iconPath = Path.Combine(AppContext.BaseDirectory, "icon.ico");
        if (File.Exists(iconPath))
            _notifyIcon.Icon = new Icon(iconPath);
        else
            _notifyIcon.Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;

        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();

        // 用 WPF ContextMenu 代替 WinForms ContextMenuStrip，匹配 Win11 风格
        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Right)
                ShowTrayContextMenu();
        };
    }

    private void ShowTrayContextMenu()
    {
        _trayContextMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);

        // 每次刷新菜单状态
        var isAutoStart = AutoStartHelper.IsEnabled();

        var menu = new ContextMenu
        {
            Style = CreateTrayMenuStyle(),
            StaysOpen = false,
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
        };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_trayContextMenu, menu))
                _trayContextMenu = null;
        };
        _trayContextMenu = menu;

        // 状态指示
        var statusItem = new MenuItem
        {
            IsEnabled = false,
            Header = CreateStatusHeader(),
        };
        menu.Items.Add(statusItem);

        // 显示窗口
        var showItem = new MenuItem { Header = "显示窗口" };
        showItem.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(showItem);

        // 重启应用
        var restartItem = new MenuItem { Header = "重启" };
        restartItem.Click += (_, _) => RestartApplication();
        menu.Items.Add(restartItem);

        // 开机自启
        _autoStartMenuItem = new MenuItem
        {
            Header = "开机自启",
            IsCheckable = true,
            IsChecked = isAutoStart,
        };
        _autoStartMenuItem.Click += (_, _) =>
        {
            var helper = _host?.Services.GetService<AutoStartHelper>();
            if (helper is not null)
                helper.SetAutoStart(_autoStartMenuItem.IsChecked);
            else
                AutoStartHelper.SetEnabled(_autoStartMenuItem.IsChecked);
        };
        menu.Items.Add(_autoStartMenuItem);

        // 退出
        var exitItem = new MenuItem
        {
            Header = "退出",
            Tag = "danger",
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44)),
        };
        exitItem.Click += (_, _) =>
        {
            _isExiting = true;
            Shutdown();
        };
        menu.Items.Add(exitItem);

        menu.IsOpen = true;
    }

    private static object CreateStatusHeader()
    {
        var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        panel.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E)),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "MinoLink 运行中",
            FontSize = 12,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return panel;
    }

    private static Style CreateTrayMenuStyle()
    {
        var menuStyle = new Style(typeof(ContextMenu));
        menuStyle.Setters.Add(new Setter(ContextMenu.PaddingProperty, new Thickness(6)));
        menuStyle.Setters.Add(new Setter(ContextMenu.BorderThicknessProperty, new Thickness(0)));
        menuStyle.Setters.Add(new Setter(ContextMenu.BackgroundProperty, System.Windows.Media.Brushes.Transparent));
        menuStyle.Setters.Add(new Setter(ContextMenu.HasDropShadowProperty, false));
        menuStyle.Setters.Add(new Setter(ContextMenu.FontFamilyProperty, new System.Windows.Media.FontFamily("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI")));
        menuStyle.Setters.Add(new Setter(ContextMenu.FontSizeProperty, 12.0));

        var menuTemplate = new ControlTemplate(typeof(ContextMenu));

        // 外层透明 Border 留出阴影空间
        var outerBorderFactory = new FrameworkElementFactory(typeof(Border));
        outerBorderFactory.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
        outerBorderFactory.SetValue(Border.PaddingProperty, new Thickness(24));

        // 内层 Border 承载圆角背景和自定义阴影
        var menuBorderFactory = new FrameworkElementFactory(typeof(Border));
        menuBorderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(ContextMenu.PaddingProperty));
        menuBorderFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFC, 0xFB, 0xFB, 0xFC)));
        menuBorderFactory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xD8, 0xE7, 0xEA, 0xEE)));
        menuBorderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        menuBorderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
        menuBorderFactory.SetValue(Border.SnapsToDevicePixelsProperty, true);
        menuBorderFactory.SetValue(UIElement.EffectProperty, new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 24,
            ShadowDepth = 0,
            Color = System.Windows.Media.Color.FromArgb(0x26, 0x00, 0x00, 0x00),
            Opacity = 1,
        });

        var itemsPresenterFactory = new FrameworkElementFactory(typeof(ItemsPresenter));
        menuBorderFactory.AppendChild(itemsPresenterFactory);
        outerBorderFactory.AppendChild(menuBorderFactory);
        menuTemplate.VisualTree = outerBorderFactory;

        menuStyle.Setters.Add(new Setter(ContextMenu.TemplateProperty, menuTemplate));

        var itemStyle = new Style(typeof(MenuItem));
        itemStyle.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6B, 0x72, 0x80))));
        itemStyle.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, System.Windows.Media.Brushes.Transparent));
        itemStyle.Setters.Add(new Setter(System.Windows.Controls.Control.BorderThicknessProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(System.Windows.Controls.Control.PaddingProperty, new Thickness(12, 8, 12, 8)));
        itemStyle.Setters.Add(new Setter(System.Windows.Controls.Control.MarginProperty, new Thickness(2)));
        itemStyle.Setters.Add(new Setter(System.Windows.Controls.Control.FontSizeProperty, 12.0));

        var template = new ControlTemplate(typeof(MenuItem));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.Name = "ItemBorder";
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        borderFactory.SetValue(Border.SnapsToDevicePixelsProperty, true);

        var dockPanelFactory = new FrameworkElementFactory(typeof(DockPanel));
        dockPanelFactory.SetValue(DockPanel.LastChildFillProperty, true);

        var checkFactory = new FrameworkElementFactory(typeof(TextBlock));
        checkFactory.Name = "CheckMark";
        checkFactory.SetValue(TextBlock.TextProperty, "✓");
        checkFactory.SetValue(TextBlock.FontSizeProperty, 12.0);
        checkFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        checkFactory.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6)));
        checkFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        checkFactory.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 8, 0));
        checkFactory.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        checkFactory.SetValue(DockPanel.DockProperty, Dock.Left);
        dockPanelFactory.AppendChild(checkFactory);

        var contentPresenterFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        contentPresenterFactory.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        contentPresenterFactory.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(System.Windows.Controls.Control.PaddingProperty));
        contentPresenterFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        dockPanelFactory.AppendChild(contentPresenterFactory);

        borderFactory.AppendChild(dockPanelFactory);
        template.VisualTree = borderFactory;

        var highlightBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6));
        var highlightBackground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1F, 0x3B, 0x82, 0xF6));
        var dangerBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44));
        var dangerBackground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x16, 0xEF, 0x44, 0x44));

        var isHighlightedTrigger = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        isHighlightedTrigger.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, highlightBrush));
        isHighlightedTrigger.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, highlightBackground));
        template.Triggers.Add(isHighlightedTrigger);

        var isCheckedTrigger = new Trigger { Property = MenuItem.IsCheckedProperty, Value = true };
        isCheckedTrigger.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, highlightBrush));
        isCheckedTrigger.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, highlightBackground));
        isCheckedTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "CheckMark"));
        isCheckedTrigger.Setters.Add(new Setter(System.Windows.Controls.Control.FontWeightProperty, FontWeights.SemiBold));
        template.Triggers.Add(isCheckedTrigger);

        var dangerHighlightTrigger = new MultiTrigger();
        dangerHighlightTrigger.Conditions.Add(new Condition(MenuItem.IsHighlightedProperty, true));
        dangerHighlightTrigger.Conditions.Add(new Condition(FrameworkElement.TagProperty, "danger"));
        dangerHighlightTrigger.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, dangerBrush));
        dangerHighlightTrigger.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, dangerBackground));
        template.Triggers.Add(dangerHighlightTrigger);

        var isEnabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        isEnabledTrigger.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0xA3, 0xAF))));
        template.Triggers.Add(isEnabledTrigger);

        itemStyle.Setters.Add(new Setter(MenuItem.TemplateProperty, template));
        menuStyle.Resources.Add(typeof(MenuItem), itemStyle);

        return menuStyle;
    }

    private void ShowMainWindow()
    {
        MainWindow?.Show();
        MainWindow!.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    private void RestartApplication()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = true,
            WorkingDirectory = AppContext.BaseDirectory,
        });

        _isExiting = true;
        Shutdown();
    }

    private static string ResolveDefaultWorkDir(string? configuredWorkDir)
    {
        if (string.IsNullOrWhiteSpace(configuredWorkDir))
            return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return Path.GetFullPath(configuredWorkDir);
    }
}

sealed class EngineHostedService(Engine engine, ILogger<EngineHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        logger.LogInformation("MinoLink 正在启动...");
        try
        {
            await engine.StartAsync(ct);
            logger.LogInformation("MinoLink 已启动");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "MinoLink Engine 启动失败！消息将无法处理。请检查 claude CLI 是否可用。");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        logger.LogInformation("MinoLink 正在关闭...");
        await engine.DisposeAsync();
        logger.LogInformation("MinoLink 已关闭");
    }
}
