# Desktop 托盘退出与重启链路修复设计

## 背景

当前 `MinoLink.Desktop/App.xaml.cs` 里，桌面端生命周期存在三个实际风险：

1. 主窗口右上角 X 会按设计缩到托盘，但托盘退出链路与窗口关闭链路分散，清理逻辑没有统一入口。
2. `SingleInstanceMutex.ReleaseMutex()` 在 `OnExit()` 中无条件调用；若当前进程并未成功持有 mutex，存在错误释放风险。
3. `RestartApplication()` 先 `Process.Start()` 再 `Shutdown()`，新实例可能在旧实例释放 mutex 之前启动，触发“已经在运行中”的竞态。
4. 托盘退出仅做 `Dispose()`，未先显式 `Visible = false`，Windows Shell 存在幽灵托盘图标风险。

## 目标

1. 保持当前产品语义：点击窗口 X 仍然是“缩到托盘”，不是退出。
2. 托盘菜单“退出”必须真退出，并确保进程、托盘、Host 都按顺序释放。
3. 托盘菜单“重启”必须在旧实例完成清理并释放 mutex 后再启动新实例。
4. 整个退出链路幂等化，避免 `OnExit` 和 `ProcessExit` 双重触发时重复释放资源出错。

## 方案对比

### 方案 A：最小修复现有退出链路（推荐）

- 增加 mutex 持有标记；
- 统一托盘清理入口；
- 把重启改成“记录请求，退出后再拉起”；
- 保留 X = Hide。

优点是边界清晰、改动小、能直接解决现有风险。

### 方案 B：引入完整状态机

为 Hide/Exit/Restart 建专门状态机。

优点是长期最清晰；缺点是改动偏大，不符合当前最小修复目标。

结论：采用方案 A。

## 设计

### 1. 显式记录是否持有单实例 mutex

新增 `_ownsSingleInstanceMutex`：

- 成功 `WaitOne(0, false)` 时设为 `true`；
- 退出时只有在该标记为 `true` 时才调用 `ReleaseMutex()`。

这样可以避免二次实例启动失败后，在 `OnExit()` 中错误释放未持有的 mutex。

### 2. 统一托盘/UI 清理入口

新增幂等清理方法，例如 `PrepareUiForShutdown()`：

- 关闭 `_trayContextMenu`
- 置空 `_trayContextMenu`
- `NotifyIcon.Visible = false`
- `NotifyIcon.Dispose()`
- 置空 `_notifyIcon`

该方法由：
- 托盘退出链路
- `OnExit()`
- `ProcessExit` 兜底清理

共同复用。

### 3. 重启请求延迟到退出后执行

`RestartApplication()` 不再立即 `Process.Start()`，而是：

- 记录 `_restartRequested = true`
- 记录 `_restartProcessPath`
- 标记 `_isExiting = true`
- 调用 `Shutdown()`

真正的 `Process.Start()` 放到 `OnExit()` 最后执行，并确保此时：

- Host 已停止
- 托盘已清理
- mutex 已释放

### 4. 退出链路幂等化

由于 `OnExit()` 和 `ProcessExit` 都可能触发，清理方法必须允许重复调用而不抛异常。

## 测试策略

1. 先增加源代码级回归测试，验证：
   - mutex 释放被持有标记保护；
   - 托盘清理包含 `Visible = false`；
   - 重启不是直接在 `RestartApplication()` 里 `Process.Start()`；
   - `OnExit()` 里存在退出后再启动新实例的入口。
2. 运行 `dotnet test`。
3. 运行 `dotnet build MinoLink.slnx`。
4. 手工最小验证：
   - 点 X 后进程保留；
   - 托盘退出后进程消失；
   - 托盘重启后新实例可正常启动，不报“已经在运行中”。
