using System.Reflection;
using System.Text.Json;

namespace MinoLink.Tests.Feishu;

public sealed class FeishuMarkdownNormalizationTests
{
    private const string PlanSample = """
方案对比A. 标准模板直出（推荐）
优点：最快、最稳、后续扩展命令/View/ViewModel/Service 都顺
缺点：会带一个示例功能，需要你后面按需替换
如果这个设计没问题，直接回复：确认执行
""";

    private const string CompactSample = """
方案对比- 方案 A：标准 saury-revit 模板（推荐，已按这个收参）
优点：自带 Revit2026 + net8.0-windows + Generic Host + MVVM + DI + Ribbon + About 示例
风险最低，后续扩展命令/View/ViewModel也最顺- 方案 B：手工从0 搭骨架
灵活，但慢，容易漏 .addin、构建配置、复制到 Addins目录这些细节- 方案 C：基于现有项目改-适合续做，不适合“全新新建”

最终设计- 项目名：RevitTest
创建位置：C:\Users\zhouh\Desktop\nihao\RevitTest
Revit版本：2026
模板内容：保留默认 About 示例- 构建配置：只用 Debug_R26
执行链路：
检查 dotnet 和 saury-revit 模板
执行 dotnet new saury-revit
检查生成结构
dotnet restore
dotnet build RevitTest.slnx --configuration Debug_R26 -m:1
确认 addin 是否复制到 C:\ProgramData\Autodesk\Revit\Addins\2026
如果这版没问题，你只回两个字：

执行
""";

    [Fact]
    public void EngineNormalization_ShouldNotSplitRecommendationLabelsFromTheirContent()
    {
        var normalized = InvokePrivateStaticStringMethod(
            "MinoLink.Core.Engine, MinoLink.Core",
            "NormalizeFinalReplyForFeishu",
            PlanSample);

        Assert.Contains("优点：最快、最稳、后续扩展命令/View/ViewModel/Service 都顺", normalized);
        Assert.Contains("缺点：会带一个示例功能，需要你后面按需替换", normalized);
        Assert.Contains("如果这个设计没问题，直接回复：确认执行", normalized);

        Assert.DoesNotContain("优点：\n", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("缺点：\n", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("直接回复：\n", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void FeishuCardMarkdown_ShouldNotTurnRecommendationLabelsIntoSeparateParagraphs()
    {
        var cardJson = InvokePrivateStaticStringMethod(
            "MinoLink.Feishu.FeishuPlatform, MinoLink.Feishu",
            "BuildMarkdownCardJson",
            PlanSample);

        var markdown = ExtractMarkdownContent(cardJson);

        Assert.Contains("优点：最快、最稳、后续扩展命令/View/ViewModel/Service 都顺", markdown);
        Assert.Contains("缺点：会带一个示例功能，需要你后面按需替换", markdown);
        Assert.Contains("如果这个设计没问题，直接回复：确认执行", markdown);

        Assert.DoesNotContain("优点：\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("缺点：\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("直接回复：\n", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void EngineNormalization_ShouldKeepCodeFenceStructure()
    {
        const string sample = """
执行命令：
```powershell
dotnet build MinoLink.slnx -m:1
```
完成后回复结果
""";

        var normalized = InvokePrivateStaticStringMethod(
            "MinoLink.Core.Engine, MinoLink.Core",
            "NormalizeFinalReplyForFeishu",
            sample);

        Assert.Contains("```powershell", normalized);
        Assert.Contains("dotnet build MinoLink.slnx -m:1", normalized);
        Assert.Contains("```", normalized);
    }

    [Fact]
    public void EndToEndFeishuMarkdownChain_ShouldKeepParagraphsCompact()
    {
        var engineNormalized = InvokePrivateStaticStringMethod(
            "MinoLink.Core.Engine, MinoLink.Core",
            "NormalizeFinalReplyForFeishu",
            PlanSample);

        var cardJson = InvokePrivateStaticStringMethod(
            "MinoLink.Feishu.FeishuPlatform, MinoLink.Feishu",
            "BuildMarkdownCardJson",
            engineNormalized);

        var markdown = ExtractMarkdownContent(cardJson);

        Assert.Contains("优点：最快、最稳、后续扩展命令/View/ViewModel/Service 都顺", markdown);
        Assert.Contains("缺点：会带一个示例功能，需要你后面按需替换", markdown);
        Assert.Contains("如果这个设计没问题，直接回复：确认执行", markdown);
        Assert.DoesNotContain("\n\n优点：", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n缺点：", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n如果这个设计没问题，直接回复：", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void EndToEndFeishuMarkdownChain_ShouldNotLeaveBlankParagraphBeforeStandaloneAnswer()
    {
        var engineNormalized = InvokePrivateStaticStringMethod(
            "MinoLink.Core.Engine, MinoLink.Core",
            "NormalizeFinalReplyForFeishu",
            CompactSample);

        var cardJson = InvokePrivateStaticStringMethod(
            "MinoLink.Feishu.FeishuPlatform, MinoLink.Feishu",
            "BuildMarkdownCardJson",
            engineNormalized);

        var markdown = ExtractMarkdownContent(cardJson);

        Assert.DoesNotContain("你只回两个字：\n\n执行", markdown, StringComparison.Ordinal);
        Assert.Contains("你只回两个字：\n执行", markdown, StringComparison.Ordinal);
    }

    private static string InvokePrivateStaticStringMethod(string assemblyQualifiedTypeName, string methodName, params object[] arguments)
    {
        var type = Type.GetType(assemblyQualifiedTypeName, throwOnError: true)!;
        var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, arguments);
        return Assert.IsType<string>(result);
    }

    private static string ExtractMarkdownContent(string cardJson)
    {
        using var document = JsonDocument.Parse(cardJson);
        var root = document.RootElement;
        var markdown = root
            .GetProperty("body")
            .GetProperty("elements")[0]
            .GetProperty("content")
            .GetString();

        return Assert.IsType<string>(markdown);
    }
}
