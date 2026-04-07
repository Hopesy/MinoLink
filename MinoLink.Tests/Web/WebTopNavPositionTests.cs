using System.Text.RegularExpressions;

namespace MinoLink.Tests.Web;

public class WebTopNavPositionTests
{
    [Fact]
    public void AppCss_ShouldKeepTopNavFixedAndReserveMainContentOffset()
    {
        var css = File.ReadAllText(GetAppCssPath());

        Assert.Matches(
            new Regex(@"\.topnav\s*\{[\s\S]*position:\s*fixed;[\s\S]*top:\s*0;", RegexOptions.Multiline),
            css);
        Assert.Matches(
            new Regex(@"\.main-content\s*\{[\s\S]*padding:\s*98px\s+5vw\s+80px;", RegexOptions.Multiline),
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
