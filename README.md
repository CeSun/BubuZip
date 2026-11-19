# EclipseArchive

基于 EclipseUI 的桌面压缩解压应用，首版面向 macOS。

当前提供本地文件与归档浏览、ZIP/7z 创建、所选内容或整包提取、读取测试，以及本地复制、移动和废纸篓操作。操作布局参考 NanaZip 的功能组织，外观沿用 EclipseUI Fluent 主题，支持跟随系统、浅色和深色模式。

压缩引擎固定引用 NuGet `SharpCompress 0.51.0-beta.174`，使用托管库完成压缩解压，无需安装命令行压缩工具或原生压缩库。EclipseUI 通过 Git submodule 单独维护。

## 当前功能

- 窗口内菜单、右键菜单，以及添加、提取、测试、复制、移动、删除、信息、打开八个工具栏命令。
- 地址栏、后退/上级导航和六列文件列表：名称、大小、压缩后、类型、修改时间、属性。支持排序、Cmd/Shift 多选、双击或 Return 打开本地目录与归档虚拟目录。
- ZIP/7z 创建设置；7z 支持密码保护与加密文件名。可提取所选条目、文件夹或整包内容到新目录，并批量读取归档进行校验。
- ZIP/7z 内部添加文件、删除条目和 F2 重命名；支持子目录添加、目录合并和明确选择后替换同名文件。
- 后台操作、按条目更新的进度与取消；原生文件/文件夹选择、保存面板，以及信息和确认对话框。
- Finder 显示当前位置，本地文件复制、移动和移到系统废纸篓。本地文件的“删除”执行废纸篓操作，归档内的“删除”需单独确认。

ZIP/7z 修改会先在独立工作目录重建、校验，确认原包未被其他程序更改后，再替换原包；失败或取消保留原包。加密 ZIP 和其他格式保持只读，无法保留的权限或元数据会拒绝编辑。7z 修改保留所提供的密码，并加密文件名。

ZIP 修改保留条目的原始属性与创建平台标识，包含 Unix 执行权限、访问权限和 DOS 属性；新加入的文件记录源权限。带 ZIP 注释的归档，以及包含当前无法保留的 7z 权限属性的归档暂不允许修改。重建不承诺保留任意格式私有的额外字段。

打开归档内文件时先提取副本，再交给系统默认应用；编辑这个副本不会自动回写。归档内“复制”进入所选内容提取流程，“移动”暂不提供。

压缩和提取目标必须是新路径，不覆盖已有文件或合并已有目录。`ArchiveFileService` 先写临时输出，成功后再提交到目标；失败或取消时清理临时输出。文件工作流拒绝符号链接、不安全路径以及冲突的归档路径。

macOS 上的解压暂存目录、归档编辑工作目录及打开文件的副本目录仅允许当前用户访问（`0700`）。解压完成后的目标根目录保留这一权限。

macOS 的 `/tmp`、`/var`、`/etc` 系统入口在确认真实指向 `/private` 对应目录后会被规范化，普通用户符号链接仍按原策略拒绝。

关闭窗口会取消并等待后台任务清理完成；正式输出已经提交后，以成功状态为准。整包提取和校验使用批量读取，solid 7z/RAR 保持顺序解码，避免为每个文件重复打开归档。

窗口当前使用 Vello Sparse 在 CPU 上绘制完整画面，再由 Metal 上传纹理并呈现，避开本机 VelloSharp GPU 栅格路径中复现的大面积黑块。界面变化时重绘整帧，空闲时跳过绘制；`--shot` 预览使用同一软件栅格后端。当前软件后端的阴影保留偏移轮廓，不提供模糊效果。

## 开发环境

- macOS 13 或更新版本。
- .NET 10 SDK。
- 开发运行所需的 SDL3、HarfBuzz：

  ```bash
  brew install sdl3 harfbuzz
  ```

- 生成 `.app` 需要 Xcode Command Line Tools。

Homebrew 依赖用于开发运行和构建时收集原生库。打包流程会将 SDL3、HarfBuzz 及所需原生依赖收入 `.app`，并包含自包含 .NET 运行时；分发后的应用不要求用户安装 Homebrew 或 .NET SDK。

## 获取项目

项目目前为本地独立仓库。创建远程仓库后，可将下面的占位地址替换为实际应用仓库地址：

```bash
git clone --recurse-submodules <应用仓库地址> EclipseArchive
cd EclipseArchive
```

对于已有的本地检出，初始化并还原子模块：

```bash
git submodule update --init --recursive
```

EclipseUI 位于 `external/EclipseUI`，来源为 <https://github.com/CeSun/EclipseUI.git>，当前固定提交为 `a724920613405845953aec215b9bafe7d9c5a554`。主仓库记录具体子模块提交，构建使用该版本；应用不依赖相邻的 `../EclipseUI` 工作目录及其未提交修改。

## 构建与运行

在项目根目录执行：

```bash
dotnet build EclipseArchive.slnx
dotnet run --project src/EclipseArchive.Desktop
```

指定初始浏览目录：

```bash
dotnet run --project src/EclipseArchive.Desktop -- --path "$HOME/Downloads"
```

运行解决方案中的测试：

```bash
dotnet test EclipseArchive.slnx
```

启动并运行有限帧数，用于检查应用窗口能否正常初始化和退出：

```bash
dotnet run --project src/EclipseArchive.Desktop -- --frames 3
```

导出离屏 PNG 预览，用于测试布局，不创建桌面窗口：

```bash
dotnet run --project src/EclipseArchive.Desktop -- --path "$HOME/Downloads" --shot out/preview.png
```

有限帧启动与离屏预览分别用于检查窗口生命周期和布局；Core 与 UI 行为测试通过解决方案测试命令运行。窗口像素回归覆盖浅色、深色及 1 倍、2 倍设备缩放，检查连续帧和选择变化后的画面完整性与像素稳定性。

## Core 流式接口

`IArchiveEngine` 与实现 `SharpCompressArchiveEngine` 位于 `src/EclipseArchive.Core`，不依赖 UI 框架。

| 接口 | 用途 |
|---|---|
| `CreateAsync` | 将 `ArchiveWriteEntry` 列表写入输出流，支持 ZIP 和 7z |
| `ListAsync` | 从输入流读取归档条目列表，可提供密码 |
| `ExtractEntryAsync` | 按条目路径读取归档内容并写入调用者提供的输出流，可提供密码 |

可选的 `IBatchArchiveEngine.ExtractEntriesAsync` 在同一归档会话内处理多个条目；目标流通过回调按需打开，由引擎逐条释放，输入归档流仍由调用者持有。

`ArchiveWriteEntry` 包含归档内路径、内容流和可选修改时间；`Content = null` 表示目录。所有调用者提供的流均保持打开。创建归档需要空输出流；列目录与提取需要从归档起点读取，调用者在复用流时应自行设置 `Position = 0`。操作在后台执行并接受 `CancellationToken`；失败或取消后应丢弃不完整输出。

7z 使用 LZMA2 非固实写入，支持密码和 `encryptFileNames` 文件名隐藏（加密归档头）。ZIP 当前支持普通写入；请求创建密码 ZIP 时抛出 `NotSupportedException`。输入归档的密码通过列目录与提取接口传入。

流式引擎只读写调用者提供的流；同项目中的 `ArchiveFileService` 负责实际磁盘文件枚举、创建、提取、读取测试、路径预检、临时输出和提交，`ArchiveEditService` 负责原包修改。UI 调用文件服务并显示操作状态。版本及接口边界见 [架构说明](docs/architecture.md)。

## 打包 macOS 应用

```bash
./eng/package-macos.sh
```

默认生成 `out/macos/EclipseArchive.app`，按本机架构构建并使用本地 ad hoc 签名。最低系统版本为 macOS 13。该签名用于本地验证；对外分发可透传 Developer ID 与可选公证参数：

```bash
./eng/package-macos.sh \
  --sign-identity "Developer ID Application: Your Name (TEAMID)" \
  --notary-profile YOUR_KEYCHAIN_PROFILE
```

`--notary-profile` 使用预先配置的钥匙串凭据，并会上传应用进行公证。仅传 Developer ID 签名参数不会自动上传。

可分别构建 Apple Silicon 和 Intel 版本：

```bash
./eng/package-macos.sh --rid osx-arm64
./eng/package-macos.sh --rid osx-x64 --library-dir /path/to/intel/libraries
```

每个版本需要相同架构的 SDL3、HarfBuzz 及其依赖。Intel 包应使用 Intel 原生库并在对应设备验收。输出已存在时，可传 `--force`，打包器会在新包验证成功后替换旧包。

## 目录

| 路径 | 用途 |
|---|---|
| `src/EclipseArchive.Core` | SharpCompress 流式引擎、磁盘文件工作流及进度模型，无 UI 依赖 |
| `src/EclipseArchive` | 主窗口、文件列表、浏览服务和桌面服务接口 |
| `src/EclipseArchive.Desktop` | macOS 入口、原生对话框、系统文件操作与离屏预览 |
| `tests/EclipseArchive.Core.Tests` | 流式引擎与文件服务测试 |
| `tests/EclipseArchive.Ui.Tests` | 文件浏览和主窗口交互测试 |
| `external/EclipseUI` | 固定提交的 EclipseUI Git 子模块 |
| `eng/package-macos.sh` | 复用 EclipseUI 打包器的应用入口 |
| `docs/architecture.md` | 工程边界及后续扩展方式 |

项目边界与数据流见 [架构说明](docs/architecture.md)。
