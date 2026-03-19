# Feishu Permission Card Callback Fix Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复飞书权限卡片点击“允许 / 拒绝 / 全部允许”后客户端直接报错的问题，并补齐权限命中日志。

**Architecture:** 先通过一次最小人工隔离验证确认问题集中在回调返回体，再把 `FeishuCardActionHandler` 从手工 `raw + JsonElement` 返回切换到 SDK 推荐的 `SetCard(MessageCard)` 结果态卡片。`Engine.ResolvePermission()` 只补可观测性，不改主控制流，避免扩大到普通消息卡片与流式预览卡片。

**Tech Stack:** .NET 8, C#, xUnit, FeishuNetSdk 4.0.1 / FeishuNetSdk.WebSocket 4.0.1, Microsoft.Extensions.Logging

---

## File Map

### Existing files to modify
- `MinoLink.Feishu/FeishuCardActionHandler.cs`
  - 当前飞书卡片回调处理器。
  - 负责解析 `action` / `session_key`、调用 `Engine.ResolvePermission(...)`、向飞书返回回调响应。
  - 需要把手工 `raw + JsonElement` 返回切换到 SDK `SetCard(...)` 结果态卡片。

- `MinoLink.Core/Engine.cs`
  - 当前权限等待器与 `ResolvePermission(...)` 所在位置。
  - 仅增加命中、未命中、等待为空、编号不匹配等日志，不改变状态机主逻辑。

### New test files to create
- `MinoLink.Feishu.Tests/MinoLink.Feishu.Tests.csproj`
  - 飞书适配层测试项目。
  - 引用 `MinoLink.Feishu`、`MinoLink.Core`、xUnit、Microsoft.NET.Test.Sdk。

- `MinoLink.Feishu.Tests/FeishuCardActionHandlerTests.cs`
  - 覆盖卡片回调解析、SDK 结果卡片结构、私聊/群聊 session_key 回退路径、卡片正文不泄露内部诊断信息。

- `MinoLink.Core.Tests/MinoLink.Core.Tests.csproj`
  - Core 层测试项目。
  - 引用 `MinoLink.Core`、xUnit、Microsoft.NET.Test.Sdk。

- `MinoLink.Core.Tests/EngineResolvePermissionTests.cs`
  - 覆盖 `ResolvePermission()` 在命中、sessionKey 未命中、等待为空、requestId 未命中时的日志行为。

### Existing files to modify for solution wiring
- `MinoLink.slnx`
  - 将两个新测试项目加入解决方案。

## Test Strategy

1. **先建立最小可运行测试工程。**
2. **立即写会失败的真实行为测试。**
3. **实现最小生产代码让测试通过。**
4. **最后做构建与人工飞书验证。**

## Manual Isolation Check Before Code Changes

在修改生产代码前先做一次临时人工隔离验证，不把任何“Toast-only 开关”带进正式代码：

1. 临时把 `FeishuCardActionHandler.ExecuteAsync(...)` 的成功路径改成：
   - 保留 `engine.ResolvePermission(...)`
   - 直接 `return BuildToast(..., "已处理")`
   - 不返回 `Card`
2. 本地运行并在飞书里点一次 `allow` / `deny` / `allow_all`。
3. 观察结果：
   - **若不再报错**：说明问题集中在回调返回卡片结构，继续执行本计划。
   - **若仍然报错**：停止本计划，转去检查 `FeishuCardBuilder.cs` 初始权限卡片结构和飞书回调通道。
4. 验证完成后，不保留这段临时代码，回到正式的 TDD 实现。

---

### Task 1: 建立飞书回调测试工程并立即写真实失败测试

**Files:**
- Create: `MinoLink.Feishu.Tests/MinoLink.Feishu.Tests.csproj`
- Create: `MinoLink.Feishu.Tests/FeishuCardActionHandlerTests.cs`
- Modify: `MinoLink.slnx`
- Modify: `MinoLink.Feishu/FeishuCardActionHandler.cs`

- [ ] **Step 1: 创建最小飞书测试项目**

在 `MinoLink.Feishu.Tests/MinoLink.Feishu.Tests.csproj` 中创建 xUnit 项目，引用：
- `../MinoLink.Feishu/MinoLink.Feishu.csproj`
- `../MinoLink.Core/MinoLink.Core.csproj`
- `Microsoft.NET.Test.Sdk`
- `xunit`
- `xunit.runner.visualstudio`
- `Microsoft.Extensions.Logging.Abstractions`

并把它加入 `MinoLink.slnx`。

- [ ] **Step 2: restore 测试项目**

Run: `dotnet restore "C:/Users/zhouh/Desktop/MinoLink/MinoLink.Feishu.Tests/MinoLink.Feishu.Tests.csproj"`

Expected: restore 成功。

- [ ] **Step 3: 立即写真实失败测试**

在 `MinoLink.Feishu.Tests/FeishuCardActionHandlerTests.cs` 中直接写真实行为测试，而不是占位测试。至少包含：

```csharp
[Theory]
[InlineData("perm:allow:req-1", "已允许")]
[InlineData("perm:deny:req-2", "已拒绝")]
[InlineData("perm:allow_all:req-3", "已全部允许")]
public async Task ExecuteAsync_ReturnsExpectedResultCard(string action, string expectedLabel)
```

断言聚焦外部行为：
- 返回 `Card` 非空。
- `Card.Data` 序列化后包含 `expectedLabel`。
- `Card.Data` 序列化后包含“权限请求已处理”。
- `Card.Data` 序列化后**不包含** `sessionKey`。
- `Card.Data` 序列化后**不包含** `requestId`。
- 结果态不再包含交互按钮标签“允许 / 拒绝 / 全部允许”。

再加两个回退测试：

```csharp
[Fact]
public async Task ExecuteAsync_WhenSessionKeyMissingInPrivateChat_FallsBackToOpenIdSessionKey()

[Fact]
public async Task ExecuteAsync_WhenSessionKeyMissingInGroupChat_FallsBackToChatAndOpenIdSessionKey()
```

再加一个错误路径测试：

```csharp
[Fact]
public async Task ExecuteAsync_WhenActionMissing_ReturnsErrorToast()
```

- [ ] **Step 4: 运行测试确认正确失败**

Run: `dotnet test "C:/Users/zhouh/Desktop/MinoLink/MinoLink.Feishu.Tests/MinoLink.Feishu.Tests.csproj" --filter "FullyQualifiedName~FeishuCardActionHandlerTests"`

Expected: FAIL，且失败点是旧代码仍返回手工卡片内容并暴露会话信息，而不是测试工程配置错误。

- [ ] **Step 5: 用 SDK DTO 做最小实现**

修改 `MinoLink.Feishu/FeishuCardActionHandler.cs`：
- 引入 `FeishuNetSdk.Im.Dtos` 以及 `FeishuNetSdk.Extensions`（若编译需要）。
- 保留 `action` / `session_key` / `ResolveSessionKey(...)` / `engine.ResolvePermission(...)` 主逻辑。
- 删除旧的 `FeishuCardBuilder.BuildCardJson(...)` 与 `JsonSerializer.Deserialize<JsonElement>(...)` 成功返回路径。
- 改为私有结果构造方法，例如：

```csharp
private static CardActionTriggerResponseDto BuildResultCard(string label)
```

返回一个最小结果态卡片：
- 标题：`label`
- 正文：`权限请求已处理`
- 无任何按钮
- 正文不带 `sessionKey`、`requestId`

- [ ] **Step 6: 运行飞书测试确认通过**

Run: `dotnet test "C:/Users/zhouh/Desktop/MinoLink/MinoLink.Feishu.Tests/MinoLink.Feishu.Tests.csproj"`

Expected: PASS。

---

### Task 2: 建立 Engine 日志测试工程并写真实失败测试

**Files:**
- Create: `MinoLink.Core.Tests/MinoLink.Core.Tests.csproj`
- Create: `MinoLink.Core.Tests/EngineResolvePermissionTests.cs`
- Modify: `MinoLink.slnx`
- Modify: `MinoLink.Core/Engine.cs`

- [ ] **Step 1: 创建最小 Core 测试项目**

在 `MinoLink.Core.Tests/MinoLink.Core.Tests.csproj` 中创建 xUnit 项目，引用：
- `../MinoLink.Core/MinoLink.Core.csproj`
- `Microsoft.NET.Test.Sdk`
- `xunit`
- `xunit.runner.visualstudio`
- `Microsoft.Extensions.Logging.Abstractions`

并加入 `MinoLink.slnx`。

- [ ] **Step 2: restore Core 测试项目**

Run: `dotnet restore "C:/Users/zhouh/Desktop/MinoLink/MinoLink.Core.Tests/MinoLink.Core.Tests.csproj"`

Expected: restore 成功。

- [ ] **Step 3: 立即写真实失败测试**

在 `MinoLink.Core.Tests/EngineResolvePermissionTests.cs` 中直接写真实行为测试。至少覆盖：

```csharp
[Fact]
public void ResolvePermission_WhenSessionKeyMissing_LogsWarning()

[Fact]
public void ResolvePermission_WhenPendingPermissionMissing_LogsWarning()

[Fact]
public void ResolvePermission_WhenRequestIdDoesNotMatch_LogsWarning()

[Fact]
public void ResolvePermission_WhenRequestMatches_LogsInformation()
```

测试做法：
- 创建 `ListLogger<Engine>` 或等效内存 logger。
- 用最小 fake `IAgent`、空平台集合、临时 sessions.json 路径实例化 `Engine`。
- 使用反射把 `_states` 设置为目标状态：
  - sessionKey 不存在
  - `PendingPermission = null`
  - `PendingPermission(requestId != actual)`
  - `PendingPermission(requestId == actual)`
- 调用 `ResolvePermission(...)`。
- 断言日志级别和消息关键字。

说明：这里**不要**通过“连续调用两次”验证重复点击，因为受 `PendingPermission` 清空时序影响，测试不稳定。重复点击风险统一归入“等待为空”日志分支验证。

- [ ] **Step 4: 运行测试确认正确失败**

Run: `dotnet test "C:/Users/zhouh/Desktop/MinoLink/MinoLink.Core.Tests/MinoLink.Core.Tests.csproj" --filter "FullyQualifiedName~ResolvePermission_"`

Expected: FAIL，且失败原因是缺少日志，不是测试夹具错误。

- [ ] **Step 5: 在 Engine 中补最小日志**

修改 `MinoLink.Core/Engine.cs` 的 `ResolvePermission(...)`：
- `sessionKey` 不存在：`Warning`
- `PendingPermission` 为空：`Warning`
- `requestId` 不匹配：`Warning`
- 命中：`Information`

日志文案要包含最小必要字段：
- `sessionKey`
- `requestId`
- 命中时的 `Allow` / `AllowAll`
- 不匹配时的 `expectedRequestId`

要求：
- 只补日志。
- 不改状态流转与命中条件。

- [ ] **Step 6: 运行 Core 测试确认通过**

Run: `dotnet test "C:/Users/zhouh/Desktop/MinoLink/MinoLink.Core.Tests/MinoLink.Core.Tests.csproj"`

Expected: PASS。

---

### Task 3: 运行整体验证

**Files:**
- Modify: `MinoLink.Feishu/FeishuCardActionHandler.cs`（如需小幅清理 using / 私有方法命名）
- Modify: `MinoLink.Core/Engine.cs`（如需小幅统一日志文案）
- Test: `MinoLink.Feishu.Tests/FeishuCardActionHandlerTests.cs`
- Test: `MinoLink.Core.Tests/EngineResolvePermissionTests.cs`

- [ ] **Step 1: 运行两个测试项目**

Run:
```bash
dotnet test "C:/Users/zhouh/Desktop/MinoLink/MinoLink.Feishu.Tests/MinoLink.Feishu.Tests.csproj" && dotnet test "C:/Users/zhouh/Desktop/MinoLink/MinoLink.Core.Tests/MinoLink.Core.Tests.csproj"
```

Expected: PASS。

- [ ] **Step 2: 用独立输出目录运行 Worker 构建**

仓库使用 `MinoLink.slnx` / csproj，不使用 `.sln`。若 `MinoLink` 正在运行并锁住 DLL，不要强杀，直接改用独立输出目录：

```bash
dotnet build "C:/Users/zhouh/Desktop/MinoLink/MinoLink/MinoLink.csproj" -o "C:/Users/zhouh/Desktop/MinoLink/.tmp-build/verify"
```

Expected: BUILD SUCCEEDED。

- [ ] **Step 3: 做人工飞书验证**

真实触发一次权限请求并手动验证：
- `allow`：点击后飞书不报错，卡片变成“已允许”。
- `deny`：点击后飞书不报错，卡片变成“已拒绝”。
- `allow_all`：点击后飞书不报错，卡片变成“已全部允许”。
- 结果态卡片不再展示按钮。
- 结果态卡片不展示 `sessionKey` / `requestId`。
- 群聊与私聊路径都能正确解析 session key。
- 人为制造未命中场景时，后端日志能看到 Warning。

- [ ] **Step 4: 记录验证结论**

在工作摘要中记录：
- 飞书端是否仍报错。
- 控制台日志关键行。
- 是否还有遗漏路径。

如果人工隔离验证表明“仅 Toast 也报错”，停止后续收尾，转去检查 `MinoLink.Feishu/FeishuCardBuilder.cs` 与飞书回调通道，不继续宣称本计划完成。

---

## Notes for the implementing agent

- 不要顺手统一 `FeishuPlatform.BuildMarkdownCardJson(...)` 与 `FeishuCardBuilder.BuildCardJson(...)`。
- 不要把 `sessionKey` 或 `requestId` 放回用户可见卡片正文。
- 不要为了测试引入持久的“Toast-only 模式开关”或额外生产构造参数。
- 若 `FeishuNetSdk` 的 `SetCard(...)` 扩展方法需要额外 `using FeishuNetSdk.Extensions;`，按编译错误补齐，保持最小改动。
- 若测试需要访问内部状态，优先反射，不要预先扩大生产 API。
- 最终若需要提交，统一在全部测试和人工验证通过后再做一次收尾提交，不做中间强制提交。
