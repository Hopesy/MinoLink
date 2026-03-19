# 飞书权限卡片回调修复设计

## 背景
当前飞书权限卡片在用户点击“允许 / 拒绝 / 全部允许”后，飞书客户端直接报错。现有代码显示，回调处理器在解析动作后会先调用 `Engine.ResolvePermission(...)`，随后再返回一个手工拼装的 `CardActionTriggerResponseDto.CardSuffix`，其中 `Type = "raw"`，`Data` 通过 `JsonElement` 注入。

对照 `FeishuNetSdk 4.0.1` README 与 XML 注释，SDK 推荐在卡片回调中使用 `new CardActionTriggerResponseDto().SetCard(MessageCard)` 的方式返回更新后的卡片，而不是手工拼装 `raw + JsonElement`。因此，`raw + JsonElement` 是当前最优先的根因候选，但还不能在改动前直接下结论。因为项目内同时存在两种飞书卡片结构：权限申请卡片发送走 `FeishuCardBuilder` 的 `header + elements`，普通预览卡片走 `FeishuPlatform` 的 `schema = "2.0" + body.elements`。如果飞书对“卡片点击后的替换卡片”存在结构兼容要求，则本次修复仍需先通过最小隔离验证确认问题集中在回调返回体，而不是初始卡片结构与回调结构混用本身。

因此本次设计聚焦在“权限卡片点击回调”链路，但会先增加一个前置验证步骤：先以仅返回 Toast 的方式隔离问题，再在验证通过后将回调返回体切换到 SDK 推荐写法，避免扩大到普通消息卡片与流式预览卡片。

## 目标
- 修复飞书权限卡片点击后客户端直接报错的问题。
- 将权限回调返回体改为 SDK 推荐的 `MessageCard / ElementsCardV2Dto`。
- 保持现有权限解析与 `Engine.ResolvePermission(...)` 主链路不变。
- 增加 `ResolvePermission()` 的日志，便于定位 `sessionKey` / `requestId` 未命中的问题。

## 非目标
- 不重构普通飞书消息回复卡片。
- 不统一 `FeishuPlatform` 中现有的 Markdown 预览卡片结构。
- 不重构整个 `FeishuCardBuilder` 为 SDK DTO 模型。

## 方案对比

### 方案 A：仅返回 Toast
回调处理器保留 `ResolvePermission(...)`，不再返回更新卡片，只返回 Toast。优点是改动最小，适合快速止血；缺点是交互结束后卡片不会进入结果态，与现有“审批卡片”交互体验不一致。

### 方案 B：仅权限回调改为 SDK 推荐卡片返回（推荐）
保留现有权限申请卡片发送逻辑，仅在点击后的回调阶段使用 `CardActionTriggerResponseDto.SetCard(...)` 返回结果态卡片。优点是改动范围可控，直接对齐 SDK 推荐做法，能精确覆盖当前 bug；缺点是权限回调卡片与普通发送卡片会暂时并存两种实现方式。

### 方案 C：全面统一全部飞书卡片
把发送卡片、流式预览卡片、权限卡片全部统一为 SDK DTO。优点是长期更整洁；缺点是改动范围大，超出本次 bugfix 的目标。

## 最终方案
采用方案 B，但增加一个前置验证门槛：

1. 先做“仅返回 Toast，不更新卡片”的最小隔离验证。
2. 如果飞书客户端不再报错，则说明问题集中在回调返回体，继续把权限回调改为 SDK 推荐的 `SetCard(...)`。
3. 如果飞书客户端仍然报错，则暂停方案 B，回头检查 `FeishuCardBuilder` 生成的初始权限卡片结构是否与回调链路本身不兼容。

## 涉及文件
主修改文件：
- `MinoLink.Feishu/FeishuCardActionHandler.cs`
- `MinoLink.Core/Engine.cs`

条件性关注文件（仅当隔离验证未通过时继续排查）：
- `MinoLink.Feishu/FeishuCardBuilder.cs`
- `MinoLink.Feishu/FeishuPlatform.cs`

## 设计细节

### 1. 权限回调响应改用 SDK DTO
`FeishuCardActionHandler.ExecuteAsync(...)` 保留以下逻辑不变：
- 从 `evt.Action.Value` 中解析 `action` 与 `session_key`
- 解析 `perm:allow:{requestId}` / `perm:deny:{requestId}` / `perm:allow_all:{requestId}`
- 调用 `engine.ResolvePermission(sessionKey, requestId, response)`

修改点：
- 删除当前手工构造 `CardActionTriggerResponseDto.CardSuffix` 的逻辑。
- 改为使用 SDK 的 `SetCard(...)` 返回一个结果态卡片。
- 结果态卡片只展示处理结果，不再包含交互按钮，避免重复点击。

建议结果态内容：
- 标题：`已允许` / `已拒绝` / `已全部允许`
- 正文：
  - 权限请求已处理

`sessionKey` 与 `requestId` 只记录到日志，不展示在用户可见卡片正文中，避免把内部诊断信息暴露到飞书卡片界面。

### 2. 暂不统一普通卡片构建器
当前发送入口存在两种卡片组织方式：
- `FeishuPlatform.BuildMarkdownCardJson(...)` 使用 `schema = "2.0"` 与 `body.elements`
- `FeishuCardBuilder.BuildCardJson(...)` 使用 `header + elements`

本次不统一两者，因为当前问题发生在“卡片点击后的即时回调响应”阶段，而不是“初始发送卡片”阶段。为控制改动面，仅修复权限回调返回体。

### 3. 增加权限命中日志
`Engine.ResolvePermission(...)` 目前只在命中时执行 `Resolve(...)`，未命中时静默返回。修改为：
- 命中时记录 `Information`
- `_states` 未找到 `sessionKey` 时记录 `Warning`
- 找到会话但 `PendingPermission` 为空或 `requestId` 不匹配时记录 `Warning`
- 重复点击或回放点击导致再次回调时，记录 `Warning`

这样能快速区分：
- 飞书回调是否到达后端
- 回调是否命中了当前等待中的权限请求
- 是否存在重复点击、回放点击或状态错位

## 数据流
1. Claude 触发权限请求。
2. `Engine.HandlePermissionRequestAsync(...)` 创建 `PendingPermission` 并发送审批卡片。
3. 用户点击飞书卡片按钮。
4. `FeishuCardActionHandler.ExecuteAsync(...)` 解析动作并调用 `Engine.ResolvePermission(...)`。
5. `Engine` 唤醒等待中的 `PendingPermission`，向 Claude 回写权限响应。
6. `FeishuCardActionHandler` 使用 SDK 推荐的 `SetCard(...)` 返回结果态卡片给飞书客户端。

## 错误处理
- 缺少 `action` / `session_key`：继续返回错误 Toast。
- `perm:*` 格式不合法：继续返回错误 Toast。
- 未知权限动作：继续返回错误 Toast。
- `sessionKey` / `requestId` 未命中：在 `Engine.ResolvePermission()` 中记录 Warning，方便排查状态错位问题。

## 测试与验证

### 前置隔离验证
在正式切换到 SDK `SetCard(...)` 之前，先进行一次最小隔离验证：
- 暂时让回调处理器只返回 Toast，不返回更新卡片。
- 保留 `engine.ResolvePermission(...)` 不变。
- 分别验证 `allow`、`deny`、`allow_all` 三条路径。

预期：
- 如果飞书客户端不再报错，则说明问题主要集中在回调卡片返回体。
- 如果飞书客户端仍报错，则暂停方案 B，回头检查 `FeishuCardBuilder` 生成的初始权限卡片结构及飞书回调通道。

### 运行时数据验证
在正式修改前后，都应记录并确认以下运行时信息：
- `evt.Action.Value` 的运行时类型是否为 `Dictionary<string, object>`，避免与 `JsonElement` 分支判断失真。
- `session_key` 在私聊与群聊场景下是否能正确解析。
- 飞书回调是否确实进入了 `FeishuCardActionHandler.ExecuteAsync(...)`。

### 编译验证
执行 `dotnet build` 验证编译状态。若当前运行中的 `MinoLink` 进程锁定输出 DLL，则使用独立输出目录构建，或先停止运行进程再构建。

### 行为验证
在飞书中触发一个权限请求并点击按钮，至少覆盖以下场景：
- 点击“允许”后，飞书客户端不报错。
- 点击“拒绝”后，飞书客户端不报错。
- 点击“全部允许”后，飞书客户端不报错。
- 私聊场景下按钮点击正常。
- 群聊场景下按钮点击正常。
- 卡片更新为结果态，不再展示按钮。
- 后端日志能看到回调已到达。
- 后端日志能看到命中或未命中的权限状态。
- 重复点击或回放点击时，不会造成静默失败，日志中能看到 Warning。

## 成功标准
- 飞书点击允许 / 拒绝 / 全部允许后不再直接报错。
- 权限回调链路继续正常推进。
- 权限卡片能更新成非交互的结果态。
- `ResolvePermission()` 未命中时有明确日志。