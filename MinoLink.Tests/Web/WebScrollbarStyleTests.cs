using System.Text.RegularExpressions;

namespace MinoLink.Tests.Web;

public class WebScrollbarStyleTests
{
    [Fact]
    public void AppCss_ShouldHideVisibleScrollbarsWithoutRemovingScrollCapability()
    {
        var css = File.ReadAllText(GetAppCssPath());

        Assert.Contains("scrollbar-width: none;", css);
        Assert.Contains("::-webkit-scrollbar", css);
        Assert.Matches(
            new Regex(@"html,\s*body\s*\{[\s\S]*overflow-y:\s*auto;", RegexOptions.Multiline),
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
