using System.Windows;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;
using MinoLink.Web.Components;

namespace MinoLink.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();

        // BlazorWebView 需要完整的 IServiceProvider 来解析 Razor 组件依赖
        WebView.Services = services;
        WebView.RootComponents.Add(new RootComponent
        {
            Selector = "#app",
            ComponentType = typeof(Routes),
        });
    }
}
