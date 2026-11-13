# AppKeeper V1.0

AppKeeper 是 Windows 10/11 x64 的全屏应用启动器，界面参考 macOS Launchpad。

## 使用

1. 在 [Actions](https://github.com/kangxls-ai/AppKeeper/actions) 打开成功的构建，下载 `AppKeeper-win-x64`，解压后将 `AppKeeper.exe` 放进可写目录运行。
2. 从托盘或用 `Alt + Space` 呼出鼠标所在屏幕上的启动器。如果快捷键冲突，在设置中录入新组合。按 `Esc` 或点击空白处隐藏。
3. 在「待整理」查看从开始菜单、Win32 App Paths 和 Windows AppsFolder 自动发现的入口。右键可移到应用页、重命名、更换图标或移除。路径失效和已卸载的入口仍留在原位并标记不可用。
4. 拖到图标左右边缘排序，拖到中央创建文件夹或加入现有文件夹。拖至屏幕左右边缘可翻页。在文件夹内可排序或拖到「拖出至应用」。输入中文、全拼或首字母可搜索所有区域。
5. 单击图标启动，单击分组打开原页上的文件夹面板。使用「添加」或从资源管理器拖入文件、快捷方式和文件夹。网址可通过「添加」录入。

登录自启动默认开启，可在设置中关闭。托盘菜单提供退出和重新扫描。程序使用独立的应用入口，整理操作只写入 AppKeeper 的数据文件。

## 备份和迁移

`AppKeeper.data.json` 和 `AppKeeper.data.json.bak` 与 EXE 同目录。每次保存会保留上一份有效配置。主文件损坏时会尝试从备份恢复。设置中的导出文件包含分组、排序、快捷键及自定义图标。可导入此文件，或先退出 AppKeeper 后复制整个目录，再从新位置运行。

## 构建

需要 .NET 10 SDK。Windows Actions 在推送、拉取请求及手动运行时编译、自检并上传 x64 产物。也可在本地运行：

```powershell
dotnet publish src/AppKeeper/AppKeeper.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o artifacts/win-x64
```

自检覆盖 500 个入口的拼音搜索、分组与排序、扫描合并、备份恢复及导出验证。手工验收步骤见 [验收清单](ACCEPTANCE.md)。

拼音数据来源为 [Unicode 16.0 Unihan](https://www.unicode.org/Public/16.0.0/ucd/Unihan.zip) 的 `kMandarin` 和 `kHanyuPinyin`，许可随项目和 EXE 提供。拼音检索使用单字读音及其组合。
