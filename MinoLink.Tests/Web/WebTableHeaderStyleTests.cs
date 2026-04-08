using System.Text.RegularExpressions;

namespace MinoLink.Tests.Web;

public class WebTableHeaderStyleTests
{
    [Fact]
    public void AppCss_ShouldUseSofterTableHeaderBackgroundForDashboardAndGuideTables()
    {
        var css = File.ReadAllText(GetAppCssPath());

        Assert.Contains("--table-header-bg: #f8f8fa;", css);
        Assert.Contains("--table-header-bg: #242437;", css);
        Assert.Matches(
            new Regex(@"\.data-table th\s*\{[\s\S]*background:\s*var\(--table-header-bg\);", RegexOptions.Multiline),
            css);
        Assert.Matches(
            new Regex(@"\.guide-table th\s*\{[\s\S]*background:\s*var\(--table-header-bg\);", RegexOptions.Multiline),
            css);
    }

    private static string GetAppCssPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "MinoLink.Web", "wwwroot", "css", "app.css");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("未找到 MinoLink.Web/wwwroot/css/app.css");
    }
}
