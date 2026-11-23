# EasyAppKeeper Mac 1.4.0

Apple Silicon 测试版，最低 macOS 14。用户目标实机为 M5 Pro、macOS 26.7.3。Windows 版仍使用现有 WPF 项目与版本号，Mac 版使用独立 Avalonia 界面并直接链接同一份布局、拼音、飞鸽传书和日程同步源文件。

## 下载与使用

在成功的 GitHub Actions `Build EasyAppKeeper Mac` 中下载 `EasyAppKeeper-mac-arm64`。提供 `.app` ZIP 与 DMG；打开 DMG 后将 `EasyAppKeeper.app` 拖到 Applications。首次运行需遵循 macOS 的开发者验证提示，按 Apple 提供的针对单个应用的允许打开流程操作，不需关闭系统整体保护。

本阶段仅使用 ad-hoc 签名保证 Apple Silicon 可执行文件完整性，未使用 Developer ID，未公证；它不是可免开发者验证提示的正式发行包。后续取得付费 Apple Developer Program 账号后可接入 Developer ID 签名及 notarytool。

默认 Option+Space 呼出，可在偏好设置中录入 Option / Command / Control 加字母或空格的新组合。关闭按钮、Esc 隐藏主窗口；从菜单栏退出才结束进程。桌面浮球可拖动并贴边露出半个，悬停打开快捷应用；可关闭。窗口大小与浮球位置保存到本机。

## 功能与平台差异

- 扫描 `/Applications`、`/System/Applications`、两处 Utilities 和 `~/Applications` 的 `.app`，通过 NSWorkspace 提取图标。支持拖入应用包、文件和文件夹，或手动添加文件、文件夹与网址。
- 分页图标网格、拼音搜索、检索历史、最近打开与锁定、自定义标签、标签拖拽、标签内独立排序、分组与移出分组。应用与文件交给 macOS 默认程序打开。
- 最近访问仅记录从本应用打开的入口，不采集其他软件或 Finder 的系统历史。近七天 TOP 50、最近 7/30 天及全部、类型与拼音筛选、隐藏、排除、暂停和清空。保留最近 90 天、最多 20,000 条本应用访问事件。
- 飞鸽传书沿用 Windows TLS、配对与文件协议，可发现 Windows/Mac 对端，核对六位校验码后确认。支持离线文字排队、文件夹 ZIP、确认接收、自动接收上限、断点续传、未读、文字部分/全文复制、文件复制到 Finder。ZIP 不自动解压。文件复制由原生 NSPasteboard 写入文件 URL。
- 日程使用同一任务操作数据库与原 MarkDo v4 手机协议，包括待办、月历、开始、完成反馈、编辑、删除、Markdown 导出与手机扫码/手动配对。手机端为另装的兼容 MarkDo Android 应用，不承诺 iOS。Mac 使用系统 SQLite，手机凭据由 AES-GCM 加密，保护密钥放在当前用户的 macOS Keychain。
- 数据默认 `~/Library/Application Support/EasyAppKeeper`，接收目录默认其下 `share`，可修改。配置自动保留上次备份，布局支持导入导出；完整备份应退出后复制整个数据目录。配对密钥依赖钥匙串，跨用户/电脑迁移后需重新配对。
- 登录启动默认关闭，由偏好设置创建/移除当前用户的 `~/Library/LaunchAgents/io.kangxls.EasyAppKeeper.plist`，下次登录生效。移动 `.app` 后请重新设置登录启动。
- 不申请辅助功能或完全磁盘访问来读取其他软件历史，不自动改防火墙；局域网功能可能需要用户允许 macOS 网络访问。

## 构建与验证

在 Apple Silicon Mac 上安装 .NET 10 SDK 和 Xcode Command Line Tools：

```bash
bash packaging/mac/build.sh
```

脚本编译共享 C# 与原生 Swift 组件，创建 `.app`、ad-hoc 签名及签名校验，在真实 macOS 上运行配对/排队文字/ZIP/续传/未读、SQLite、手机加密同步自检，再生成 ZIP 和 DMG。Actions 还验证 NSWorkspace 应用扫描。

人工验收：

- [ ] M5 Pro 目标系统启动、菜单栏、Dock、退出与第二次打开。
- [ ] Option+Space 及修改/冲突提示；不同输入法下录入与触发。
- [ ] 浮球四边、不同 DPI/多屏工作区、窗口缩放/最小化/尺寸重启恢复。
- [ ] Finder 拖入 `.app`、文件/文件夹，排序、分组、移出，标签独立顺序与备份恢复。
- [ ] Finder 实际粘贴文件、文字部分选择，传输进度和新消息不打断选区。
- [ ] Windows 与 Mac 真实局域网发现、双向配对、文本、超过上限文件、断网续传、未读前后台切换。
- [ ] 原 MarkDo Android 实机扫码/手动配对、双向同步、重启后钥匙串读取和撤销。
- [ ] 菜单栏驻留、下次登录启动、Applications 目录移动后的处理。

自动化协议与打包验证不能代替上述真实桌面、局域网和手机验收。

云端桌面检查使用软件渲染，避免虚拟 Mac 的 Metal 着色器编译超时；正常启动按 Metal、OpenGL、软件渲染顺序选择。可用 `--software-rendering` 显式选择兼容模式，M5 Pro 的实际图形表现仍需实机验收。
