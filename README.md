# Codex History Archiver

Windows 上的 Codex 本地历史会话批量归档、恢复工具。**非官方项目，当前为 `0.1.0-preview`。**

[English](README.en.md) · [更新记录](CHANGELOG.md) · [MIT 许可证](LICENSE)

适合需要批量整理本地历史，或在某些账户配置下遇到桌面归档失败的用户。工具使用已安装 Codex 的 `app-server` 接口，不包含 Codex 程序，不会修复桌面客户端自己的归档按钮。

## 功能

- 搜索标题或 ID，批量勾选、归档和恢复。
- 自动查找本机 Codex 程序，也可手动选择程序和会话目录。
- 每次操作前核对条数和标题，逐条验证操作结果。
- 批量处理可停止；当前一条会完成请求和结果核对，随后停止。
- 对被其他进程占用的会话报告失败；结果不明时停止，不自动重试。

## 系统与兼容性

- Windows 10 / 11，.NET Framework 4.8。
- 已安装并使用过 Codex，需要可执行的 `codex.exe` 和现有本地会话目录。
- 当前原生归档协议的实机验证范围：Codex CLI `0.158.0-alpha.2.1`，桌面版 `26.924.2738.0`。
- 其他版本尚未逐一验证。工具使用部分实验性字段，Codex 更新可能改变接口。
- 界面目前为简体中文。只处理本机可读的用户会话；云端 ChatGPT 会话、子代理会话、缺失历史文件的旧索引不在处理范围内。

## 使用

1. 解压 Windows 发布包，运行 `CodexHistoryArchiver.exe`。不需要管理员权限。
2. 首次打开后核对设置里的会话目录和 Codex 程序。默认目录使用 `CODEX_HOME`，未设置时为当前用户的 `.codex`。保存过的设置优先于默认值；启动参数仅覆盖本次运行。
3. 等其他 Codex 任务完成，**通过应用菜单完全退出 Codex 桌面应用**，保持本工具打开。其他 CLI 会话也可能持有占用锁。
4. 搜索并勾选会话，点击归档，核对确认框中的标题和数量。
5. 操作完成后刷新列表。需要恢复时，切换“显示已归档（可恢复）”并选择会话。
6. 关闭本工具，再打开 Codex 检查结果。

“全选当前结果”只勾选当前筛选结果。改变搜索或切换列表会清空勾选。可以按 Ctrl / Shift 高亮多行，再点击“勾选高亮行”。项目中的会话也会显示，请核对工作目录。归档操作针对整条会话，不是移除某一个侧栏快捷入口。

高级启动参数（路径含空格时使用引号）：

```powershell
.\CodexHistoryArchiver.exe --home "D:\CodexData" --codex-exe "D:\Tools\Codex\codex.exe"
```

## 本地数据与工作方式

工具只允许初始化、列出会话、读取元数据、归档和恢复这几类请求。读取元数据时使用 `includeTurns: false`，不请求聊天正文，也不发送模型 turn。它不直接修改 SQLite 或历史文件，不读取认证文件来复制凭据，不清理项目或 Git worktree。

它会启动你选择的 Codex 程序；该程序仍按自己的本地配置运行。本工具没有独立的分析上报或联网更新功能，不能替 Codex 子进程承诺完全离线。

设置和运行记录保存在 `%LOCALAPPDATA%\CodexHistoryArchiver`。日志只包含时间、会话 ID、动作和结果，不保存标题、正文、凭据或原始服务错误。日志和设置属于个人本地数据，请不要加入公开仓库或问题反馈。反馈错误时只提供版本、错误提示及复现步骤即可。

每条成功操作都要核对最终状态。遇到超时或核对失败，先刷新并检查未归档、已归档两种列表。退出工具会关闭它启动的服务；等待结束失败时，只结束该子进程，不结束其他应用。

## 从源码构建

在 Windows PowerShell 中，从源码根目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package.ps1
```

构建使用 Windows .NET Framework 编译器，不下载 NuGet 包。结果在 `dist`。`.csproj` 供已安装 .NET Framework 4.8 开发工具的 IDE / MSBuild 使用。

自动测试使用模拟本地服务和临时目录，不接触你的真实会话。它验证客户端行为，不能代替每个 Codex 版本的真实集成验证。仓库中的 GitHub Actions 在 Windows 上构建并运行这些测试；它不会自动发布 Release。

## 发布到 GitHub

只上传这个独立源码目录的源码、文档和构建配置。`dist`、日志、设置、`.codex`、数据库及凭据已列入 `.gitignore`。不要上传用于排查问题的其他工作目录或从 Codex 安装包提取的文件。

运行打包脚本得到源码 ZIP、Windows ZIP 和 SHA-256 清单。在自己的 GitHub 仓库提交源码后，可以创建 `v0.1.0-preview` **预发布**，将 Windows ZIP 和校验清单附到 Release。README 的版本验证范围应随实际测试结果更新。

项目按 MIT 许可证提供。Codex 名称仅用于说明兼容对象；本项目与 OpenAI 无隶属、赞助或官方支持关系。Codex 本身及其商标不属于本项目的许可证范围。

