# 右键添加入口（Windows 1.3.4 / Mac 1.4.3）

Windows 绿色版正常启动后，在当前用户的 `Software\Classes\*\shell\EasyAppKeeper.Add` 与 `Directory` 下注册“添加到 EasyAppKeeper”，不需要管理员权限；设置可关闭。Windows 11 的传统菜单可能位于“显示更多选项”。保持 EXE 单文件分发。移动 EXE 后运行一次即可更新菜单路径；删除 EXE 前先关闭菜单选项。

商店版用 MSIX 的 `windows.fileExplorerContextMenus` 与原生 x64 `IExplorerCommand` COM 扩展注册文件、文件夹菜单。扩展只读取选择和启用标记，写入持久队列并激活包的 AUMID；不在 Explorer 中加载 .NET。设置关闭时扩展返回 `ECS_HIDDEN`。由包管理器注册与移除，不写绿色版右键注册表。开发构建需要 Visual Studio C++ x64 工具；`Build-StorePackage.ps1` 自动构建并打入 DLL。

多选后可选择全部、现有标签或创建新标签。所有源文件保留原位。请求保存在所属版本数据目录下的 `entry-inbox`，由单一主进程合并处理；绿色版和商店版使用不同进程实例范围，防止互相截获。保存成功或明确取消后才确认消费，异常退出可重试；非法请求隔离为 `.invalid`。每批最多 1000 项，超出时请分批。

Windows 传统静态菜单受系统选择数量限制，绿色版建议每批不超过 100 项；商店版 COM 菜单每批最多 1000 项。CI 通过 Shell 的真实 `IContextMenu` 调用验证绿色版多选交付。

CI 验证包安装与激活、原生 COM 的标题/图标/开关、多选文件及目录交付、冷启动/后台二次交付、对真实 WPF 确认窗口的 UI Automation、去重与新标签持久化。Mac 使用 Automator 快速操作，另见 `../mac/README.md`。系统菜单的实际位置仍需目标 Windows/macOS 实机验收。
