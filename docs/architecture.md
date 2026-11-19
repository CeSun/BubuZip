# 架构说明

EclipseArchive 是独立的 .NET 10 应用仓库，首版目标为 macOS 13 及更新版本。EclipseUI 通过 Git submodule 引用，应用源代码与框架源代码分别维护。

当前已连接文件浏览 UI、SharpCompress 流式引擎、磁盘文件工作流与 macOS 原生桌面服务。操作布局参考 NanaZip 的菜单、工具栏和文件管理组织，样式使用 EclipseUI Fluent。

## 当前工程

```text
EclipseArchive/
├── EclipseArchive.slnx
├── src/
│   ├── EclipseArchive.Core/        流式引擎、磁盘文件服务与进度模型
│   ├── EclipseArchive/             主窗口、文件列表与浏览服务
│   └── EclipseArchive.Desktop/     macOS 入口、原生桌面服务与预览
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

`src/EclipseArchive.Desktop` 是可执行项目，包含应用生命周期对象，负责 macOS 平台、Vello 渲染、HarfBuzz 文本、主题和服务的装配。`MacDesktopServices` 通过 AppKit/Objective-C 提供原生选择、保存和信息/确认面板，并实现 Finder 显示、系统打开与废纸篓操作。原生对话框及桌面窗口在 Cocoa 主线程调用。

启动参数 `--path folder` 指定初始目录；`--frames 3` 用于运行有限循环次数后退出；`--shot image.png` 使用与窗口相同的 Vello Sparse 软件栅格后端导出测试预览，不创建桌面窗口。

构建入口为 `dotnet build EclipseArchive.slnx`，运行入口为 `dotnet run --project src/EclipseArchive.Desktop`，测试命令为 `dotnet test EclipseArchive.slnx`。窗口启动检查与 Core 行为测试分别验证应用入口和压缩引擎。

## 框架依赖

`external/EclipseUI` 的上游地址为 <https://github.com/CeSun/EclipseUI.git>，当前提交固定为 `a724920613405845953aec215b9bafe7d9c5a554`。

应用通过子模块内的项目引用使用框架。主仓库的子模块记录决定实际框架版本，构建与旁边的 EclipseUI 检出目录无关。这样不同开发者和构建环境可以还原相同依赖，也不会意外使用框架工作目录中的未提交修改。

首次检出使用 `git clone --recurse-submodules`。已有检出使用：

```bash
git submodule update --init --recursive
```

升级框架时，先在子模块中取得并切换到目标提交，构建及验证应用，再由应用主仓库记录新的子模块提交。常规初始化与还原遵循主仓库固定版本。

当前原生对话框由应用的桌面服务提供，EclipseUI 子模块保持独立。窗口、布局、输入和主题能力由框架提供，归档操作与文件管理交互由应用维护。

应用使用的 EclipseUI 子模块提供 `VelloSurfaceOptions.UseSoftwareRasterizer`，本应用显式开启它，以避开当前 VelloSharp GPU 栅格路径在本机重复绘制时复现的黑块。`VelloSparseBackend` 通过 `SparseRenderContext` 在 CPU 上生成 RGBA 像素；Metal 负责纹理上传和全屏三角形呈现。变化帧完整软件栅格化并上传，空闲帧仍由宿主跳过，元素绘制命令缓存继续保留；此模式不使用 GPU 局部光栅或原生场景片段缓存。软件后端尚无模糊指令，阴影仅绘制偏移后的轮廓，能力标识不包含 `Blur`。

窗口像素回归使用真实 `MainWindow` 的布局与裁剪，分别覆盖浅色、深色以及 1 倍、2 倍设备缩放。每组检查首次绘制及选择变化前后的八个连续帧，断言背景无大面积透明缺失、重复状态的像素哈希一致；文字替身用于隔离系统字体差异。原生窗口启动检查另行验证 macOS 纹理呈现路径。

## 界面与浏览

主窗口包含菜单、右键菜单、地址栏、状态栏和八个工具栏命令：添加、提取、测试、复制、移动、删除、信息、打开。六列列表显示名称、大小、压缩后、类型、修改时间和属性，支持排序、Cmd/Shift 多选、双击或 Return 打开，以及后退、上级、个人文件夹、桌面和下载目录导航。

`BrowserService` 将本地目录或归档目录转换为 `BrowserSnapshot`。归档路径使用 `archive.zip!/folder/` 表示；缺少显式目录条目的归档也会合成虚拟目录。浏览归档不要求先将整包解压到磁盘。

添加对话框选择源文件、目标路径和 ZIP/7z 格式；7z 可设置密码及加密文件名。提取支持所选条目、所选目录及整包内容，测试逐条读取文件。操作在后台执行，进度按条目更新，用户可取消。

ZIP/7z 支持归档内添加、删除和同目录重命名，其他格式与加密 ZIP 保持只读。添加保留源顶层名称，支持在当前归档子目录内添加；同名文件默认拒绝，只有勾选替换后才能覆盖。归档内“复制”转为提取所选内容，“移动”尚未提供。打开归档内文件会先提取副本交由系统默认应用处理，副本不会自动回写。废纸篓命令仅针对本地文件，并在确认后通过系统 API 执行。

## Core 与压缩引擎

`src/EclipseArchive.Core` 定义 `IArchiveEngine`，由 `SharpCompressArchiveEngine` 实现。该项目不依赖 EclipseUI，压缩操作仅使用 SharpCompress 托管库，不调用压缩命令行工具，也不加载原生压缩库。

NuGet 版本固定为 `SharpCompress 0.51.0-beta.174`，不使用浮动版本。该包标记的仓库提交为 `b2fade6400cf6b3fe851c1fe9773f19b5b20a586`，对应官方 GitHub 的 `0.51.0-beta2`。NuGet 包版本与 GitHub 标签名称不同，升级时应同时核对包版本、仓库提交和行为测试。

| 接口 | 输入与行为 |
|---|---|
| `CreateAsync` | 输出流、`ArchiveWriteEntry` 列表、`ArchiveFormat.Zip` 或 `ArchiveFormat.SevenZip`、可选密码和文件名隐藏选项 |
| `ListAsync` | 输入流及可选密码，返回归档条目列表 |
| `ExtractEntryAsync` | 输入流、归档条目路径、输出流及可选密码，将指定条目内容写入输出流 |

`ArchiveWriteEntry` 包含 `EntryPath`、`Content` 和可选的 `LastModified`；`Content = null` 表示目录。归档内路径由调用者提供。

调用者负责所传流的生命周期，操作结束后这些流保持打开，同一流不应被并发操作共享。列目录和提取的输入流需要可读、可定位；复用同一流时，调用者应在读取前自行设置 `Position = 0`。创建归档需要空的可写输出流，可定位的输出流需位于起点；7z 输出必须可定位，以便归档完成时回填文件头。

操作接受 `CancellationToken`，并在后台执行压缩或解压工作，以避免 CPU 密集过程阻塞 UI。调用者应等待操作完整结束后再使用输出结果；直接使用流式接口时，失败或取消后应丢弃不完整输出。

7z 写入使用 LZMA2 非固实模式，可设置密码，并通过 `encryptFileNames` 加密归档头以隐藏文件名。ZIP 的密码写入请求会抛出 `NotSupportedException`。列目录与提取接口可将密码传入读取选项，以读取对应的加密归档。

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

`ArchiveFileService.CreateAsync` 递归枚举源文件与目录并调用引擎创建 ZIP/7z；`ExtractAsync` 预检归档路径后提取所选内容或整包；`TestAsync` 将文件内容读取到空输出流。三者接受进度通知和取消令牌。

创建与提取要求目标不存在，且目标父目录已存在。服务先写同级临时文件或目录，完整成功并再次检查取消状态后，再提交到最终目标；失败或取消时清理临时输出，不覆盖已有内容或合并已有目录。

在 macOS 等 Unix 平台，提取暂存目录在写入内容前设为 `0700`，提交后的目标根目录保留该权限；归档编辑工作目录和打开归档内文件的副本目录也使用 `0700`，限制其他用户访问提取出的明文。

文件预检拒绝源路径和目标路径中的符号链接、归档内链接、绝对或父级跳转路径、重复条目，以及大小写/Unicode 规范化后冲突的路径。权限不足等文件系统错误由操作结果报告。

本地复制、移动由窗口文件管理工作流执行，拒绝已有目标、符号链接和将目录放入自身的情况。复制先写临时项目再提交；批量文件管理操作逐项执行。`MacDesktopServices.Trash` 使用系统废纸篓 API，失败抛出文件操作错误，不回退为永久删除。

`ArchiveEditService` 在原包同目录创建独立工作目录，提取后实施添加、删除或重命名，再重建并校验归档。临时目录在提取明文之前设置为仅当前用户可访问。提交前重新计算原包 SHA-256，外部更改会中止更新；成功时原子替换，失败或取消保留原包。7z 重写保留密码并加密归档头；不能安全保留的权限或元数据会在修改前拒绝。

ZIP 重建后仅补写中央目录的创建平台和属性字段，不改动压缩数据或本地文件头。重命名同步映射原属性，新加入的条目记录源文件权限；支持 ZIP64 目录定位。ZIP 注释和无法安全保留的 7z 权限会使修改提前停止；任意私有额外字段不在保留承诺内。

`ArchivePaths.ResolveUserPath` 仅规范确认指向 `/private/tmp`、`/private/var`、`/private/etc` 的 macOS 系统入口，不解析其他用户链接。源、目标与归档编辑路径在包含关系比较前使用同一规范形式，避免系统别名绕过自包含检查。

UI 在同步上下文中接收进度和操作结果，更新状态或显示可重试的错误。`BackgroundOperations` 将整个工作回调放在线程池运行并跟踪，停止后拒绝新任务；窗口释放时请求取消并等待所有后台清理结束，不依赖已退出的 UI 消息循环。正式提交是操作成功的边界，提交后的取消不会把已保存结果报告为失败。非密码导致的归档打开错误直接显示错误，只在密码异常时显示解锁面板。

## macOS 发布

应用通过 `./eng/package-macos.sh` 调用子模块内的打包器，并传入本应用的项目路径、名称、可执行文件名和输出位置。打包器将自包含发布产物、SDL3、HarfBuzz 及原生依赖组装到 `.app`，校验依赖架构并执行签名。

开发时可使用 `brew install sdl3 harfbuzz` 安装依赖。打包后的应用使用包内运行时和原生依赖，用户无需安装这些开发依赖。

默认输出为 `out/macos/EclipseArchive.app`，最低系统版本为 macOS 13。支持 `osx-arm64` 和 `osx-x64` 两种独立构建；各自需要匹配架构的原生库并单独验收。

默认 ad hoc 签名用于本地运行验证。通过包装脚本透传 `--sign-identity` 可使用 Developer ID 签名，透传 `--notary-profile` 可使用已配置的钥匙串公证凭据执行上传、公证和装订。仓库不包含开发者证书或公证凭据。
