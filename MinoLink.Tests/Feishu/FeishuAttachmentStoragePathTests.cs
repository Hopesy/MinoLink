using System.Reflection;
using MinoLink.Feishu;

namespace MinoLink.Tests.Feishu;

public sealed class FeishuAttachmentStoragePathTests
{
    [Fact]
    public void AttachmentStorageDirectory_ShouldUseOutputFeishuFilesUnderBaseDirectory()
    {
        var method = typeof(FeishuMessageHandler).GetMethod(
            "GetAttachmentStorageDirectory",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var date = new DateTime(2026, 4, 8);
        var dir = (string?)method!.Invoke(null, [date]);

        var expected = Path.Combine(
            AppContext.BaseDirectory,
            "output",
            "feishu-files",
            "20260408");

        Assert.Equal(expected, dir);
    }
}
