# MinoLink

把 Claude Code CLI 接到飞书等即时通讯平台的消息网关，提供：

- 多会话
- 工具审批
- 流式回复预览
- Web 控制台

## 项目组成

| 项目                    | 作用                                   |
| ----------------------- | -------------------------------------- |
| `MinoLink.Core`       | Engine、会话、命令、权限审批、核心模型 |
| `MinoLink.ClaudeCode` | Claude Code CLI 适配                   |
| `MinoLink.Codex`      | Codex 适配                             |
| `MinoLink.Feishu`     | 飞书平台接入                           |
| `MinoLink.Web`        | Blazor UI 组件                         |
| `MinoLink`            | Web 宿主（ASP.NET Core）               |
| `MinoLink.Desktop`    | WPF 桌面端（当前版本 `1.0.6`）       |
| `MinoLink.Installer`  | WixSharp MSI 安装包                    |

## 配置

主配置文件：

- `MinoLink/appsettings.json`

当前飞书相关配置结构：

```json
"MinoLink": {
  "ProjectName": "my-project",
  "Agent": {
    "Type": "claudecode",
    "WorkDir": "C:/Users/zhouh/Desktop",
    "Mode": "default"
  },
  "Feishu": {
    "AppId": "<Feishu AppId>",
    "AppSecret": "<use user-secrets or MINO_MinoLink__Feishu__AppSecret>",
    "VerificationToken": "<use user-secrets or MINO_MinoLink__Feishu__VerificationToken>"
  }
}
```

常用项：

- `Agent.Type`：当前通常用 `claudecode`
- `Agent.WorkDir`：默认工作目录，建议直接设桌面
- `Agent.Mode`：权限模式，例如 `default`
- `Feishu.AppId`：飞书应用 AppId
- `Feishu.AppSecret`：飞书应用密钥
- `Feishu.VerificationToken`：飞书事件校验 token

敏感项建议不要写死在仓库里，优先用 `user-secrets`：

```powershell
dotnet user-secrets set "MinoLink:Feishu:AppSecret" "<你的飞书 AppSecret>" --project MinoLink/MinoLink.csproj
dotnet user-secrets set "MinoLink:Feishu:VerificationToken" "<你的飞书 VerificationToken>" --project MinoLink/MinoLink.csproj
```

也可以用环境变量：

```powershell
$env:MINO_MinoLink__Feishu__AppSecret="<你的飞书 AppSecret>"
$env:MINO_MinoLink__Feishu__VerificationToken="<你的飞书 VerificationToken>"
```

## 运行

### Web

```bash
cd MinoLink
dotnet run
```

### Desktop

```bash
cd MinoLink.Desktop
dotnet run
```

桌面端带：

- 系统托盘
- 开机自启开关
- 检查更新入口

## 常用命令

聊天里输入 `/` 命令：

- `/help`：查看完整命令
- `/new [名称] [--project 路径]`：新会话
- `/project [路径]`：切换当前会话工作目录
- `/mode [模式]`：切换权限模式
- `/stop`：中断当前回复
- `/continue` / `/resume` / `/switch <序号>`：恢复或切换会话
- `/file <要求>`：要求 Agent 产出文件并自动回传

### `/file`

`/file` 会强制 Agent 把产物写到 `output/` 下，并在回复结尾输出：

```text
[FILES]
output/report.pdf
output/charts/
[/FILES]
```

Engine 会自动：

- 隐藏 `[FILES]` 区块
- 校验路径范围
- 过滤不存在文件和超大文件
- 将文件发送到飞书

## 打包

本地构建 MSI：

```bash
dotnet build MinoLink.Installer -c Release
```

产物默认输出到：

```text
MinoLink.Installer/output/MinoLink-1.0.6-win-x64.msi
```

安装器行为：

- 安装范围：当前用户
- 安装目录：`%LocalAppData%\Programs\MinoLink`
- 快捷方式：开始菜单 + 桌面
- 升级方式：MSI 标准升级
- 版本来源：`MinoLink.Desktop/MinoLink.Desktop.csproj` 的 `<Version>`

## 发布

不再提供本地 `publish-installer` 脚本，发布统一走：

- `.github/workflows/release-installer.yml`

触发方式：

- push 版本 tag，例如 `v1.0.6`

标准流程：

1. 更新 `CHANGELOG.md`
2. 更新 `MinoLink.Desktop.csproj` 的 `<Version>`
3. 提交并 push `master`
4. 创建并 push `v{Version}` tag
5. GitHub Actions 自动构建 MSI、创建或更新 Release，并上传安装包

常用命令示例：

```powershell
git push origin master
git tag -a v1.0.6 -m "v1.0.6"
git push origin v1.0.6
```

Release 正文来源：

- workflow 从 tag 里取版本号，如 `v1.0.6 -> 1.0.6`
- 再从 `CHANGELOG.md` 提取对应区块作为 Release body
- 标题格式必须保持：

```md
## [1.0.6] - 2026-04-21
```

如果 `CHANGELOG.md` 缺少对应版本区块，发布会直接失败。

## 应用内更新

桌面端已接通 GitHub Release 更新链路：

- 顶栏显示当前版本
- 顶栏可手动检查更新
- 只识别正式版 Release
- 下载 MSI 后自动拉起安装器并关闭当前应用

默认更新仓库配置位于 `MinoLink/appsettings.json`：

```json
"ReleaseUpdate": {
  "ApiBaseUrl": "https://api.github.com/",
  "GitHubOwner": "Hopesy",
  "GitHubRepo": "MinoLink"
}
```

## 日志

- 运行日志同时输出到控制台和文件
- 默认目录：

```text
MinoLink/bin/<配置>/<TFM>/logs/
```

- 文件名：

```text
MinoLink-YYYYMMDD.log
```
