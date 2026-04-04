# Sessions History Load Optimization Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 消除 `/sessions` 页面首次打开时因全量历史会话扫描导致的明显卡顿，同时保留按项目查看完整摘要的能力。

**Architecture:** 通过 `NativeSessionCatalogService` 把“项目列表元数据扫描”和“项目详情摘要读取”拆开；页面首屏只读取轻量列表并异步刷新，展开项目时再懒加载完整会话详情。

**Tech Stack:** .NET 8、ASP.NET Core Blazor Server、xUnit

---

### Task 1: 建立会话目录聚合服务

**Files:**
- Create: `MinoLink.Core/Interfaces/INativeSessionProjectSource.cs`
- Create: `MinoLink.Core/Services/NativeSessionCatalogService.cs`
- Modify: `MinoLink.Core/ClaudeNativeSession.cs`
- Modify: `MinoLink.Core/CodexNativeSession.cs`
- Test: `MinoLink.Tests/Core/NativeSessionCatalogServiceTests.cs`

**Step 1: Write the failing test**

编写测试，断言：
- 目录服务首次加载会合并多个 provider 的轻量项目列表；
- 再次读取不会重复调用 provider；
- 指定项目详情加载时只调用对应 provider 的完整读取方法。

**Step 2: Run test to verify it fails**

Run: `dotnet test MinoLink.Tests/MinoLink.Tests.csproj --filter NativeSessionCatalogServiceTests`
Expected: FAIL，因为服务与接口尚不存在。

**Step 3: Write minimal implementation**

实现 provider 接口、目录聚合服务，以及 Claude/Codex 的轻量列表读取入口。

**Step 4: Run test to verify it passes**

Run: `dotnet test MinoLink.Tests/MinoLink.Tests.csproj --filter NativeSessionCatalogServiceTests`
Expected: PASS

**Step 5: Commit**

```bash
git add MinoLink.Core/Interfaces/INativeSessionProjectSource.cs MinoLink.Core/Services/NativeSessionCatalogService.cs MinoLink.Core/ClaudeNativeSession.cs MinoLink.Core/CodexNativeSession.cs MinoLink.Tests/Core/NativeSessionCatalogServiceTests.cs
git commit -m "feat: add cached native session catalog"
```

### Task 2: 接入 Web 管理页异步与懒加载

**Files:**
- Modify: `MinoLink/Program.cs`
- Modify: `MinoLink.Web/Components/Pages/Sessions.razor`

**Step 1: Write the failing test**

如无现成组件测试基建，本任务以 Task 1 的服务测试作为回归保护，页面层采用人工验证，不额外引入 UI 测试框架。

**Step 2: Run test to verify current limitation**

通过代码审阅确认页面仍在 `OnInitialized()` 同步阻塞扫描，作为修复前基线。

**Step 3: Write minimal implementation**

- 在 `Program.cs` 注册 `NativeSessionCatalogService`；
- 页面改为后台加载项目列表；
- 展开项目时懒加载摘要；
- 增加 loading / error 状态。

**Step 4: Run verification**

Run: `dotnet test MinoLink.Tests/MinoLink.Tests.csproj --filter NativeSessionCatalogServiceTests`
Run: `dotnet build MinoLink.slnx`
Expected: 全部通过。

**Step 5: Commit**

```bash
git add MinoLink/Program.cs MinoLink.Web/Components/Pages/Sessions.razor
git commit -m "fix: lazy load session history for admin page"
```
