using System.Text.Json;

namespace MinoLink.Tests.Feishu;

public sealed class FeishuLoggingSafetyTests
{
    [Fact]
    public void AppSettings_ShouldNotContainConcreteFeishuSecrets()
    {
        using var doc = LoadAppSettings();
        var feishu = doc.RootElement
            .GetProperty("MinoLink")
            .GetProperty("Feishu");

        var appSecret = feishu.GetProperty("AppSecret").GetString();
        var verificationToken = feishu.GetProperty("VerificationToken").GetString();

        Assert.True(string.IsNullOrWhiteSpace(appSecret) || LooksLikePlaceholder(appSecret),
            $"appsettings.json 不应提交真实 AppSecret，当前值: {appSecret}");
        Assert.True(string.IsNullOrWhiteSpace(verificationToken) || LooksLikePlaceholder(verificationToken),
            $"appsettings.json 不应提交真实 VerificationToken，当前值: {verificationToken}");
    }

    [Fact]
    public void AppSettings_ShouldThrottleFeishuSdkAndHttpNoise()
    {
        using var doc = LoadAppSettings();
        var logLevel = doc.RootElement
            .GetProperty("Logging")
            .GetProperty("LogLevel");

        Assert.Equal("Warning", logLevel.GetProperty("FeishuNetSdk").GetString());
        Assert.Equal("Warning", logLevel.GetProperty("System.Net.Http.HttpClient.FeishuNetSdk").GetString());
        Assert.Equal("Warning", logLevel.GetProperty("WebApiClientCore").GetString());
    }

    [Fact]
    public void FeishuServiceExtensions_ShouldDisableSdkVerboseLogging()
    {
        var source = File.ReadAllText(GetRepoPath("MinoLink.Feishu", "FeishuServiceExtensions.cs"));
        Assert.Contains("sdkOpts.EnableLogging = false;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WebProgram_ShouldLoadUserSecretsAfterAppSettings()
    {
        var source = File.ReadAllText(GetRepoPath("MinoLink", "Program.cs"));
        Assert.Contains("builder.Configuration.AddUserSecrets<Program>(optional: true);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopApp_ShouldLoadUserSecretsAfterAppSettings()
    {
        var source = File.ReadAllText(GetRepoPath("MinoLink.Desktop", "App.xaml.cs"));
        Assert.Contains("builder.Configuration.AddUserSecrets<App>(optional: true);", source, StringComparison.Ordinal);
    }

    private static JsonDocument LoadAppSettings()
    {
        var path = GetRepoPath("MinoLink", "appsettings.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static bool LooksLikePlaceholder(string value) =>
        value.Contains("<", StringComparison.Ordinal) ||
        value.Contains("your", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("placeholder", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("use user-secrets", StringComparison.OrdinalIgnoreCase);

    private static string GetRepoPath(params string[] segments)
    {
        var path = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(path))
        {
            if (File.Exists(Path.Combine(path, "MinoLink.slnx")))
                return Path.Combine([path, .. segments]);

            path = Path.GetDirectoryName(path)!;
        }

        throw new DirectoryNotFoundException("未找到仓库根目录。");
    }
}
