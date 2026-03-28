# 飞书回复大片空行诊断与修复设计

## 背景

飞书端最近出现了“不应该的大片空行”。问题样本已经确认：包含“方案对比 / 优点 / 缺点 / 编号列表 / 收尾确认语”的正常段落，在飞书里被渲染成过多段落间距。

当前代码链路存在两层飞书文本整形：

1. `MinoLink.Core/Engine.cs`
   - `NormalizeFinalReplyForFeishu`
   - `SplitMergedFinalReplyLine`
   - `CompactFinalReplyBlankLines`
2. `MinoLink.Feishu/FeishuPlatform.cs`
   - `NormalizeFeishuMarkdown`
   - `SplitMergedMarkdownLine`
   - `CompactBlankLines`

## 已确认现象

- 当前未提交改动里，`Engine.cs` 新增了飞书专用总结规整逻辑。
- 当前未提交改动里，`FeishuPlatform.cs` 也新增了飞书 markdown 规整逻辑，并统一将回复走 interactive markdown card。
- 旧版 `HEAD` 中不存在 `NormalizeFinalReplyForFeishu` 与 `NormalizeFeishuMarkdown` 这两层叠加格式化。
- 问题样本文本恰好命中 `Engine.cs` 内的“优点：/缺点：/如果这个设计没问题，直接回复：/编号列表”强制拆行规则。

## 根因假设

高概率根因不是飞书平台自身异常，而是**双层 markdown 规范化叠加**：

- `Engine` 先把语义段落拆成多行；
- `FeishuPlatform` 再次按标题/代码块/编号进行拆分和空行整理；
- 最终 interactive markdown card 将这些人为插入的段落边界放大为视觉上的大片空白。

## 设计原则

1. **先定根因，再修复**：不做猜测式修改。
2. **只保留一层主要整形责任**：避免双重加工。
3. **优先删除语义猜测型规则**：像“优点：/缺点：/如果这个设计没问题，直接回复：”这种文案特化规则，不应在通用链路里强插换行。
4. **保留结构保护型规则**：
   - CRLF 统一
   - 代码块边界保护
   - 连续空行压缩
   - 必要的标题兼容
5. **回归用例覆盖真实样本**：必须覆盖这次用户给出的实际文本形态。

## 执行范围

- `MinoLink.Core/Engine.cs`
- `MinoLink.Feishu/FeishuPlatform.cs`
- 新增测试项目或测试文件
- `Docs/plans/` 文档

## 成功标准

- 能明确说明问题由哪一层或哪两层叠加导致。
- 真实样本文本经本地规范化后不再被拆成大量空段落。
- 代码块、列表、短文本回复不回归。
- `dotnet build MinoLink.slnx` 通过。

