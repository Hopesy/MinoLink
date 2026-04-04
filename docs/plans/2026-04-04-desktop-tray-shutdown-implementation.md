# Desktop Tray Shutdown And Restart Fix Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 修复桌面端托盘退出/重启链路中的 mutex 释放风险、重启竞态和幽灵托盘图标问题，同时保留点击窗口 X 缩到托盘的语义。

**Architecture:** 通过在 `App.xaml.cs` 中引入持锁标记、幂等 UI 清理入口与延迟重启机制，把退出逻辑统一收口，并用源代码级回归测试锁住关键生命周期语义。

**Tech Stack:** .NET 8、WPF、BlazorWebView、xUnit

---

### Task 1: 锁定 Desktop 生命周期语义

**Files:**
- Create: `MinoLink.Tests/Desktop/DesktopExitLifecycleTests.cs`
- Test Input: `MinoLink.Desktop/App.xaml.cs`

**Step 1: Write the failing test**

编写测试断言：
- mutex 释放必须受持有标记保护；
- 托盘清理必须先隐藏再释放；
- `RestartApplication()` 只记录重启请求，不直接 `Process.Start()`；
- `OnExit()` 会在清理后调用延迟重启入口。

**Step 2: Run test to verify it fails**

Run: `dotnet test MinoLink.Tests/MinoLink.Tests.csproj --filter DesktopExitLifecycleTests`
Expected: FAIL，因为这些保护尚未实现。

**Step 3: Write minimal implementation**

修改 `App.xaml.cs` 实现持锁标记、托盘幂等清理和延迟重启。

**Step 4: Run test to verify it passes**

Run: `dotnet test MinoLink.Tests/MinoLink.Tests.csproj --filter DesktopExitLifecycleTests`
Expected: PASS

**Step 5: Commit**

```bash
git add MinoLink.Desktop/App.xaml.cs MinoLink.Tests/Desktop/DesktopExitLifecycleTests.cs
git commit -m "fix: harden desktop tray shutdown lifecycle"
```

### Task 2: 做全量验证

**Files:**
- Verify only

**Step 1: Run tests**

Run: `dotnet test MinoLink.Tests/MinoLink.Tests.csproj`
Expected: PASS

**Step 2: Run build**

Run: `dotnet build MinoLink.slnx`
Expected: PASS

**Step 3: Manual smoke**

运行桌面端，验证：
- X 仍然缩到托盘；
- 托盘退出后进程消失；
- 托盘重启后不会误判单实例冲突。
