# Claude Tools

Windows 上的 Claude 多账号切换与用量管理工具：一键切换 Claude 桌面 App 的登录账号，切换时自动带上会话记录和客户端设置，同时提供余量、Token 用量、会话记录和缓存清理等功能。

> 非官方工具，与 Anthropic 无关。Claude 是 Anthropic 的商标。请只用它管理你自己的账号，并遵守 Anthropic 的服务条款。

## 功能

- **桌面 App 账号切换**：每个账号保存一套独立的登录数据，切换时自动关闭并重新打开 Claude 桌面 App，无需重新登录；新账号走官方登录流程。
- **会话记录跟随**：切换时把上一个账号的会话记录同步到新账号，也可以手动合并所有账号的会话；客户端里删除的会话不会被同步「复活」。
- **客户端设置跟随**：字号（界面缩放）、主题、窗口布局、MCP 配置等随账号切换带过去；只合并设置项，不复制登录令牌。
- **余量与推荐**：读取桌面 App 本地记录的 5 小时 / 7 天配额，显示每个账号的余量，一键切到余量最多的账号。
- **用量统计**：读取本机 `~/.claude/projects` 的对话记录，按消息去重，统计 Token、请求数、缓存命中率和估算成本；支持按时间、模型、账号筛选，含请求日志和可编辑的定价表。
- **终端 Claude Code 多账号**：每个账号独立的 `CLAUDE_CONFIG_DIR`，共享同一份历史对话；可检测并一键安装 Claude Code。
- **缓存清理**：清理桌面 App 和 Claude Code 的缓存（网页缓存、脚本编译缓存、GPU / 着色器缓存、遥测缓存等），不动登录信息、会话和设置。
- **其他**：托盘常驻与右键快速切换、开机自启、操作日志、存储扫描、邮箱脱敏、深浅色主题、界面字号。

## 系统要求

- Windows 10 / 11
- .NET Framework 4.8（Windows 自带）
- Microsoft Edge（Windows 自带，用作界面窗口）
- Claude 桌面 App（Squirrel 安装版，位于 `%LOCALAPPDATA%\AnthropicClaude`）

## 下载使用

到 [Releases](../../releases) 下载 `Claude Tools.exe`，放在任意文件夹直接运行，无需安装。

## 从源码构建

不需要安装任何 SDK，使用 Windows 自带的 C# 编译器：

```powershell
powershell -ExecutionPolicy Bypass -File src\build.ps1
```

生成的 `Claude Tools.exe` 位于仓库根目录。想换图标，把 `icon.png` 放进 `src` 后重新构建。

## 工作原理

- 界面是内嵌在 exe 里的网页（`src/ui.html`），用 Edge 应用模式窗口显示；后台是本地 HTTP 服务（只监听 `localhost`，请求需带随机令牌）。
- 桌面 App 账号：当前账号的数据在 `%APPDATA%\Claude`，其他账号存放在 `%APPDATA%\Claude-profiles\<id>`。切换时关闭 App，把两个目录对换后重新打开；任一步失败会回滚。
- 终端账号：`%USERPROFILE%\.claude-accounts\<id>`，其中 `projects` 等目录通过目录联接指向 `~/.claude`，所有账号共用历史对话。
- 本工具自身数据（设置、日志、定价）：`%LOCALAPPDATA%\ClaudeSwitcher`。

## 源码结构

| 文件 | 说明 |
|---|---|
| `src/Server.cs` | 程序入口、托盘、本地服务、接口 |
| `src/Desktop.cs` | 桌面 App 账号存档、切换、会话 / 设置同步、合并 |
| `src/ClaudeSwitcher.cs` | 终端 Claude Code 账号 |
| `src/Extras.cs` | 设置、日志、配额、会话列表、存储扫描、缓存清理 |
| `src/Usage.cs` | 用量统计与定价 |
| `src/ui.html` | 界面 |
| `src/build.ps1` | 生成图标并编译 |

## 注意事项

- 切换账号、合并会话、清理当前账号缓存时会关闭 Claude 桌面 App，进行中的对话会中断。
- 余量数据来自桌面 App 本地记录，只有用过的账号才会更新。
- 用量统计里的「成本」按 API 公开价格估算，订阅账号实际不按此计费。
- 本工具会读写 Claude 桌面 App 的本地数据目录。使用风险自负，建议重要数据先备份。

## English

**Claude Tools** is an unofficial Windows utility for switching between multiple Claude accounts in the Claude desktop app (no re-login needed), carrying sessions and client settings across accounts, and viewing quota, token usage, sessions and cache cleanup. Not affiliated with Anthropic. Build with `powershell -ExecutionPolicy Bypass -File src\build.ps1` (uses the C# compiler bundled with Windows).

## 许可证

[MIT](LICENSE)
