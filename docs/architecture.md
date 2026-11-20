# 架构说明

EclipseArchive 是独立的 .NET 10 应用仓库，支持 Windows、Linux 和 macOS 13 及更新版本。EclipseUI 通过 Git submodule 引用，应用源代码与框架源代码分别维护。

应用首页提供新建压缩、打开压缩和设置入口。选择压缩包后显示归档内容，归档浏览、SharpCompress 流式引擎、磁盘文件工作流、系统集成与跨平台桌面服务相互独立。

## 当前工程

```text
EclipseArchive/
├── EclipseArchive.slnx
├── src/
│   ├── EclipseArchive.Core/        流式引擎、磁盘文件服务与进度模型
│   ├── EclipseArchive/             主窗口、文件列表与浏览服务
│   └── EclipseArchive.Desktop/     跨平台入口、原生桌面服务与预览
├── tests/
│   ├── EclipseArchive.Core.Tests/  引擎与文件服务测试
│   └── EclipseArchive.Ui.Tests/    浏览和主窗口交互测试
├── external/
│   └── EclipseUI/                  Git 子模块
├── eng/
│   └── package-macos.sh            应用打包入口
└── docs/
    └── architecture.md
```

`src/EclipseArchive` 包含 `MainWindow.ui` 与对应交互代码、`FileListBox`、`BrowserService`，以及 `IDesktopServices` 接口。它引用 Core 和子模块中的 Runtime、Rendering、主题及 `.ui` 编译生成器。

`ArchivePane` 保存当前归档的浏览快照、密码、排序、历史与选择。`MainWindow.NavigateToAsync` 拒绝本地目录；上一级在归档根目录停止，切换归档时清空历史，路径栏为只读，返回首页清空当前归档。后台操作及模态对话框期间禁止切换操作目标。`ArchiveControls` 提供跨平台矢量图标、平面命令按钮、只读路径栏和列分隔线。

归档浏览界面的参考来自相邻 NanaZip 仓库的 `NanaZip.Modern/MainWindowToolBarPage.xaml`、`StatusBarTemplate.xaml` 和 `Documents/MainWindowLightMode.png`、`MainWindowDarkMode.png`。应用保留 EclipseArchive 名称和现有 ZIP/7z 能力，标题栏和文件选择由宿主操作系统提供。性能测试使用内存中的 4 MiB 数据执行 ZIP 压缩、解压并校验，数值不等同于 NanaZip 的 LZMA 基准。

`src/EclipseArchive.Desktop` 是可执行项目，根据系统装配 Win32、Linux SDL3 或 macOS 宿主，以及 Vello 渲染、HarfBuzz 文本、主题和桌面服务。`MacDesktopServices` 通过 AppKit/Objective-C 提供原生面板及废纸篓，Cocoa 调用保持在主线程；`WindowsDesktopServices` 使用 Windows PowerShell 的 WinForms 文件面板、Win32 信息框和 Shell 回收站；`LinuxDesktopServices` 使用 `zenity`、`gio` 和 `xdg-open`。Windows 和 Linux 的选择面板在后台等待，界面线程继续处理窗口消息，面板打开期间禁止重复操作；macOS 原生面板继续运行在 Cocoa 主线程。外部程序参数通过参数数组传递，用户路径不会交给 shell 解释。Linux 窗口使用 Vulkan；Windows 和 macOS 使用 Sparse 软件栅格及系统图形表面。

启动参数接受归档文件路径；`--path folder` 指定创建压缩包时的初始建议目录，不会浏览该目录；`--frames 3` 用于运行有限循环次数后退出；`--shot image.png` 导出离屏测试预览，不创建桌面窗口；`--settings` 进入设置，可与离屏预览组合。`--shell` 携带系统菜单操作及路径，参数经类型校验后转为 `ArchiveShellRequest`。

构建入口为 `dotnet build EclipseArchive.slnx`，运行入口为 `dotnet run --project src/EclipseArchive.Desktop`，测试命令为 `dotnet test EclipseArchive.slnx`。窗口启动检查与 Core 行为测试分别验证应用入口和压缩引擎。

## 框架依赖

`external/EclipseUI` 的上游地址为 <https://github.com/CeSun/EclipseUI.git>，当前提交固定为 `41cb24c82fe20d9289969f581078970a22011424`。

应用通过子模块内的项目引用使用框架。主仓库的子模块记录决定实际框架版本，构建与旁边的 EclipseUI 检出目录无关。这样不同开发者和构建环境可以还原相同依赖，也不会意外使用框架工作目录中的未提交修改。

首次检出使用 `git clone --recurse-submodules`。已有检出使用：

```bash
git submodule update --init --recursive
```

升级框架时，先在子模块中取得并切换到目标提交，构建及验证应用，再由应用主仓库记录新的子模块提交。常规初始化与还原遵循主仓库固定版本。

当前原生对话框由应用的桌面服务提供，EclipseUI 子模块保持独立。窗口、布局、输入和主题能力由框架提供，归档操作与文件管理交互由应用维护。

应用使用的 EclipseUI 子模块提供 `VelloSurfaceOptions.UseSoftwareRasterizer`。Windows 和 macOS 使用软件栅格化并由对应平台的图形表面呈现；Linux 使用 Vulkan 后端。软件后端尚无模糊指令，阴影仅绘制偏移后的轮廓，能力标识不包含 `Blur`。

窗口像素回归使用真实 `MainWindow` 的布局与裁剪，分别覆盖浅色、深色以及 1 倍、2 倍设备缩放。每组检查首次绘制及选择变化前后的八个连续帧，断言背景无大面积透明缺失、重复状态的像素哈希一致；文字替身用于隔离系统字体差异。原生窗口启动检查另行验证 macOS 纹理呈现路径。

## 界面与浏览

首页不显示文件列表。打开压缩包后，单面板提供菜单、右键菜单、只读路径栏、状态栏及归档操作。六列列表显示名称、大小、压缩后、类型、修改时间和属性，支持排序、多选、双击或 Enter 打开。后退与上级仅在当前压缩包内导航。

`BrowserService` 提供归档快照；主窗口仅调用其归档浏览入口。归档路径使用 `archive.zip!/folder/` 表示；缺少显式目录条目的归档也会合成虚拟目录。嵌套压缩文件按需提取到私有临时目录并以只读方式逐层打开，显示路径使用 `outer.zip!/inner.7z!/`；每层保存父归档位置及密码，返回可跨层，最外层根目录不可进入系统目录。临时副本在返回首页、切换外层归档或关闭应用时清理；提取默认保存到最外层压缩包所在目录。浏览不要求先将整包解压到磁盘。

添加对话框选择源文件、目标路径以及 ZIP、7z、TAR、TAR.GZ、TAR.BZ2、TAR.LZ、TAR.ZST、GZ、BZ2、LZ 或 ZST；单文件流仅接受一个源文件。ZIP、7z 和 GZip 可选压缩级别，ZIP 支持 AES-256 和 ZipCrypto，7z 可加密文件名。提取支持所选条目、所选目录及整包内容，并可选择完整路径、相对于当前目录或不包含目录；测试逐条读取文件。操作在后台执行，进度按条目更新，用户可取消。桌面主界面的压缩、解压通过 `IArchiveOperationWindows` 显示独立原生模态窗口，`ArchiveOperationWindow` 拥有任务、进度、取消和结果状态；关闭时同步等待后台清理，主窗口随后恢复。成功后保留 100% 和成功状态，可确认关闭或主动打开输出文件夹；不自动弹出输出目录。Win32 次窗口使用 `quitOnDestroy=false`，关闭时不向父窗口消息循环发送退出消息。

右键菜单的操作进入 `OperationOnly` 小窗口，不显示首页或归档浏览面板；需要参数或密码时也在该窗口内处理。关闭按钮将释放窗口的工作投递到下一批 UI 回调，当前原生消息批次内的鼠标及焦点事件仍使用有效界面；标题栏关闭后由宿主循环退出后的清理流程取消并等待任务。Windows 在启动参数解析后即可选择模式。macOS 的 `MacArchiveLifetime` 先创建隐藏原生窗口，等待初始文档或正常启动通知；`PrepareInitialRequests` 同步校验、解码和重排请求，再初始化最终窗口。后台操作仅在正式 UI 同步上下文中启动，避免主界面先闪现。普通启动及普通归档打开仍显示完整主界面。

ZIP/7z 支持归档内添加、删除和同目录重命名，其他格式与加密 ZIP 保持只读。添加保留源顶层名称，支持在当前归档子目录内添加；同名文件默认拒绝，只有勾选替换后才能覆盖。归档内“复制”转为提取所选内容。打开归档内文件会先提取副本交由系统默认应用处理，副本不会自动回写。

## 设置与系统集成

共享 UI 通过 `IShellIntegrationService` 查询、注册文件关联和文件管理器菜单，不访问注册表或平台 API。`ShellIntegrationStatus.AssociatedExtensions` 仅包含系统实际认定默认由 EclipseArchive 打开的格式；菜单状态分别记录安装与启用。设置打开时只查询状态，用户点击按钮后才更改系统配置。操作异步执行并防止重复提交，成功后重新查询；失败显示错误，系统确认未完成时不显示为全部关联或菜单已启用。主窗口的关闭、操作目标切换与请求队列遵循设置忙碌状态。

`ArchiveShellFormats.Extensions` 是跨平台支持格式目录，设置、系统注册和参数验证共用；macOS 包声明也保持相同范围。仅声明当前读取器支持的格式。`MacShellIntegration` 注册当前 `.app`，按包内文档类型设置默认角色处理程序，再读取系统结果；不写单文件 `com.apple.LaunchServices.OpenWith`，不清除下载隔离属性。对于已有单文件绑定与下载隔离组合引发的系统拦截，应用无法在未收到请求之前处理该提示，仍可通过应用内打开文件。

`WindowsShellIntegration` 仅在 HKCU 注册本应用 ProgID、Capabilities 和可用打开程序，再打开 Windows 默认应用页面让用户确认；不生成或覆盖受保护的 `UserChoice`。状态通过系统有效 ProgID 查询，区分候选注册与实际默认应用。菜单写在文件与目录的经典级联 shell verb 中，Windows 11 位于“显示更多选项”；当前采用单选模型，用户路径作为独立的引用参数传入可执行文件，卸载仅删除当前可执行文件拥有的菜单键。Windows 发布版之外的开发宿主不可注册。

macOS 的原生 Finder Sync 扩展位于 `eng/macos-finder-extension`，随 `.app` 放入 `Contents/PlugIns/EclipseArchiveFinder.appex`。注册后必须由用户在系统扩展页面启用；扩展监控真实用户主目录与 `/Volumes`，菜单只在范围内显示。访达跨进程传递菜单时不保留任意 `representedObject`，因此菜单项用支持的 `tag` 编码操作和菜单上下文，点击时重新取得 Finder 选择；不依赖原菜单项实例或自定义对象。

扩展处于沙盒中，`NSWorkspace.OpenConfiguration.arguments` 会被系统忽略。操作通过私有请求文档交付：扩展在自身容器的应用支持目录写入受限权限、随机命名的 `.eclipsearchive-request`，再用 `NSWorkspace.open(_:withApplicationAt:configuration:)` 向包含它的应用发送标准文档打开事件。普通文件打开与请求文档均由应用层 SDL 文件事件监听接收；请求文档由 `MacFinderRequestFile` 解码为 `ArchiveShellRequest`，保留现有 UI 队列与后台流程。公开 CLI 及 Windows 注册表菜单继续使用 `--shell` 参数。

请求文档单独声明为本应用拥有的私有数据类型，不加入 25 种归档格式关联列表，也不注册可从网页调用的 URL 操作协议。读取器只接受扩展私有目录中的直属请求，检查文件名、所有者、权限、链接、大小、时效、协议版本、操作白名单和路径；成功读取后删除，不能重放。来源不符、过期或不合法的请求不会执行。扩展写入和启动失败会记录日志并显示提示，主程序解码或执行失败沿用应用内错误显示。原生请求与解析测试不代替真实 Finder 点击验收；本机界面操作权限不可用时，应明确记录此验证范围。

菜单功能参考本地 NanaZip 的命令表：打开归档、提取文件、提取到此处、提取到同名文件夹、测试、添加到归档，以及直接创建 ZIP／7z。邮箱、校验和及应用未实现的命令不展示。`MainWindow.RequestShellAction` 将请求排入线程安全队列，在 UI 上下文且没有后台操作、选择器、模态框或错误提示时处理；压缩合并所选源，其他命令按归档依次执行。普通添加预填创建对话框，直接 ZIP／7z 命令调用真实文件服务，提取与测试共用现有密码、进度、取消和错误流程。后端及无头 UI 测试不代替目标系统上的默认应用确认、Finder 扩展启用与 Explorer 菜单实机验收。

## Core 与压缩引擎

`src/EclipseArchive.Core` 定义 `IArchiveEngine`，由 `SharpCompressArchiveEngine` 实现。该项目不依赖 EclipseUI，压缩操作使用 SharpCompress 和 SharpZipLib 托管库，不调用压缩命令行工具，也不加载原生压缩库。

NuGet 版本固定为 `SharpCompress 0.51.0-beta.174`，不使用浮动版本。该包标记的仓库提交为 `b2fade6400cf6b3fe851c1fe9773f19b5b20a586`，对应官方 GitHub 的 `0.51.0-beta2`。NuGet 包版本与 GitHub 标签名称不同，升级时应同时核对包版本、仓库提交和行为测试。

| 接口 | 输入与行为 |
|---|---|
| `CreateAsync` | 输出流、`ArchiveWriteEntry` 列表、ZIP/7z/TAR 组合或单文件流格式、可选密码和文件名隐藏选项 |
| `ListAsync` | 输入流及可选密码，返回归档条目列表 |
| `ExtractEntryAsync` | 输入流、归档条目路径、输出流及可选密码，将指定条目内容写入输出流 |

`ArchiveWriteEntry` 包含 `EntryPath`、`Content` 和可选的 `LastModified`；`Content = null` 表示目录。归档内路径由调用者提供。

调用者负责所传流的生命周期，操作结束后这些流保持打开，同一流不应被并发操作共享。TAR 的各文件输入流必须可定位且位于起点。列目录和提取的输入流需要可读、可定位；复用同一流时，调用者应在读取前自行设置 `Position = 0`。创建归档需要空的可写输出流，可定位的输出流需位于起点；7z 输出必须可定位，以便归档完成时回填文件头。

操作接受 `CancellationToken`，并在后台执行压缩或解压工作，以避免 CPU 密集过程阻塞 UI。调用者应等待操作完整结束后再使用输出结果；直接使用流式接口时，失败或取消后应丢弃不完整输出。

7z 写入使用 LZMA2 非固实模式，级别映射到字典和快字节参数；可设置密码，并通过 `encryptFileNames` 加密归档头。ZIP 支持存储、Deflate、AES-256 和 ZipCrypto 写入，ZipCrypto 创建密码限 ASCII。列目录与提取接口可传入密码读取加密归档。

读取器依据内容选择随机归档接口或顺序 reader；压缩 TAR 的外层流在提交前完整解码并校验，ARC/ARJ/ACE 的条目校验其 CRC。单文件 BZip2、XZ、LZip、Zstandard 使用原始压缩流，并在缺少内置名称时从源文件名生成条目名。部分 ARC 不提供条目大小，界面可能显示零。多卷归档暂不支持；ACE 仅支持存储条目，ARC/ARJ 部分方法受上游实现限制。

流式提取只向调用者提供的输出流写入数据；磁盘目录、文件路径和输出提交策略由 `ArchiveFileService` 处理。`ArchiveEntryInfo` 保留归档内路径、大小、加密状态、修改时间和链接信息，供浏览及文件预检使用。

`SharpCompressArchiveEngine` 同时实现可选的 `IBatchArchiveEngine`。批量目标由条目名与输出流工厂组成，引擎按需打开并释放每条输出流；源归档流保持打开。ZIP 复用同一个归档对象，7z/solid RAR 使用顺序 reader 保留解码字典并校验条目内容。文件服务保留整包安全预检，随后一次批量处理选中的文件；其他引擎实现可回退到单条接口。

`tests/EclipseArchive.Core.Tests` 保存流式引擎和文件服务测试，`tests/EclipseArchive.Ui.Tests` 保存浏览和主窗口交互测试。

## 磁盘文件工作流

依赖方向为：

```text
EclipseArchive.Desktop → macOS 平台
EclipseArchive.Desktop → EclipseArchive → EclipseUI
EclipseArchive → EclipseArchive.Core → SharpCompress
MacDesktopServices → IDesktopServices（定义于共享 UI）
```

`ArchiveFileService.CreateAsync` 递归枚举源文件与目录并调用引擎创建所选格式；`ExtractAsync` 预检归档路径后提取所选内容或整包；`TestAsync` 将文件内容读取到空输出流。三者接受进度通知和取消令牌。

提取路径映射后再次检查冲突，展平后同名文件会被拒绝。创建与提取要求目标不存在，且目标父目录已存在。服务先写同级临时文件或目录，完整成功并再次检查取消状态后，再提交到最终目标；失败或取消时清理临时输出，不覆盖已有内容或合并已有目录。

`ExtractHereAsync` 用于系统菜单的“提取到此处”，其目标为已有的归档父目录。它先在目标目录中的私有暂存目录完成普通提取，再对所有待提交顶层文件与文件夹执行名称冲突和链接预检；存在任意冲突则不发布任何项目，也不合并目录。提交按顶层项移动，过程中失败会撤回本次已移动的项目；若撤回本身失败，保留暂存数据并报告位置，避免错误清理损失内容。取消与读取失败清理暂存输出，用户已有项目保持原样。

在 macOS 等 Unix 平台，提取暂存目录在写入内容前设为 `0700`，提交后的目标根目录保留该权限；归档编辑工作目录和打开归档内文件的副本目录也使用 `0700`，限制其他用户访问提取出的明文。

文件预检拒绝源路径和目标路径中的符号链接、归档内链接、绝对或父级跳转路径、重复条目，以及大小写/Unicode 规范化后冲突的路径。权限不足等文件系统错误由操作结果报告。

主窗口不提供系统目录浏览或本地文件管理。系统文件选择器仅用于选择创建压缩包的源、打开归档及选择输出位置。

`ArchiveEditService` 在原包同目录创建独立工作目录，提取后实施添加、删除或重命名，再重建并校验归档。临时目录在提取明文之前设置为仅当前用户可访问。提交前重新计算原包 SHA-256，外部更改会中止更新；成功时原子替换，失败或取消保留原包。7z 重写保留密码并加密归档头；不能安全保留的权限或元数据会在修改前拒绝。

ZIP 重建后仅补写中央目录的创建平台和属性字段，不改动压缩数据或本地文件头。重命名同步映射原属性，新加入的条目记录源文件权限；支持 ZIP64 目录定位。ZIP 注释和无法安全保留的 7z 权限会使修改提前停止；任意私有额外字段不在保留承诺内。

`ArchivePaths.ResolveUserPath` 仅规范确认指向 `/private/tmp`、`/private/var`、`/private/etc` 的 macOS 系统入口，不解析其他用户链接。源、目标与归档编辑路径在包含关系比较前使用同一规范形式，避免系统别名绕过自包含检查。

UI 在同步上下文中接收进度和操作结果，更新状态或显示可重试的错误。`BackgroundOperations` 将整个工作回调放在线程池运行并跟踪，停止后拒绝新任务；窗口释放时请求取消并等待所有后台清理结束，不依赖已退出的 UI 消息循环。正式提交是操作成功的边界，提交后的取消不会把已保存结果报告为失败。非密码导致的归档打开错误直接显示错误，只在密码异常时显示解锁面板。

## macOS 发布

应用通过 `./eng/package-macos.sh` 复用子模块内的打包能力，并传入本应用的项目路径、名称、可执行文件名和输出位置。打包流程将自包含或 NativeAOT 发布产物、SDL3、HarfBuzz 及原生依赖组装到 `.app`，补齐文档类型声明并构建 Finder Sync 扩展，校验依赖架构，按嵌套顺序执行签名，然后进行可选公证。扩展在最终应用签名和公证之前收入包内，不通过修改已签名安装包来补装。

开发时可使用 `brew install sdl3 harfbuzz` 安装依赖；构建原生访达扩展需要 Xcode 及其 macOS SDK。打包后的普通应用使用包内运行时，NativeAOT 应用不包含托管运行时，两者均使用包内原生依赖，用户无需安装这些开发依赖。打包本身不注册系统默认关联或启用扩展。

默认输出为 `out/macos/EclipseArchive.app`，最低系统版本为 macOS 13。支持 `osx-arm64` 和 `osx-x64` 两种独立构建；各自需要匹配架构的原生库并单独验收。

默认 ad hoc 签名用于本地运行验证。通过包装脚本透传 `--sign-identity` 可使用 Developer ID 签名，透传 `--notary-profile` 可使用已配置的钥匙串公证凭据执行上传、公证和装订。仓库不包含开发者证书或公证凭据。
