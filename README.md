# BubuZip

基于 EclipseUI 的桌面压缩解压应用，支持 Windows、Linux 和 macOS。

启动页提供“新建压缩”“打开压缩”和“设置”入口。打开压缩包后，可以浏览其中的目录、提取内容、测试归档，以及修改受支持的 ZIP/7z；包内的压缩文件可继续逐层打开，浏览范围限定在归档内。设置中可切换外观、关联压缩文件格式，以及注册或移除系统文件管理器的右键菜单。

压缩引擎固定引用 NuGet `SharpCompress 0.51.0-beta.174` 和 `SharpZipLib 1.4.2`，使用托管库完成压缩解压，无需安装命令行压缩工具或原生压缩库。EclipseUI 通过 Git submodule 单独维护。

应用图标使用 NanaZip 正式版原图，由 Shomnipotence 设计；来源及 CC BY-ND 4.0 许可见 [图标说明](assets/icons/NOTICE.md)。macOS 应用包和 Windows 可执行文件均包含对应格式的图标。

仓库为 [CeSun/BubuZip](https://github.com/CeSun/BubuZip)，构建入口为 `BubuZip.slnx`，生成的应用与可执行文件名称为 `BubuZip`（Windows 为 `BubuZip.exe`）。项目和命名空间统一使用 BubuZip，UI 程序集名为 `BubuZip.UI`。

从旧项目名升级时，已有外观设置继续使用。Windows 保留已确认的旧版默认关联，并在重新注册菜单时迁移当前安装拥有的旧菜单；macOS 使用新的 `io.bubuzip.app` 应用身份，需要重新关联文件格式并在系统设置中启用新的 BubuZip 访达扩展。

## 当前功能

- “新建压缩”使用带系统标题栏和边框的独立普通窗口设置压缩参数，通过系统选择器添加源文件或文件夹并设置输出路径；确认后在该窗口显示进度。“打开压缩”先选择压缩包，再用新窗口显示其内容，原窗口及其浏览位置保持不变。各窗口可独立使用和关闭。
- 新建压缩采用 NanaZip 风格的顶部输出路径、双栏参数和加密分组；窄窗口自动改为单栏。设置使用“系统 / 右键菜单 / 外观”分页，支持浅色与深色显示。
- 归档浏览器提供添加、提取、测试、复制、删除和信息；支持排序、调整列宽、多选、双击或 Enter 打开归档中的项目。选择其他压缩包可使用“更多 → 文件 → 打开压缩包…”或 Cmd/Ctrl+O。
- 归档路径为只读；上一级、Backspace 和历史导航可在嵌套压缩包之间返回，在最外层归档根目录无法继续进入系统文件夹。返回首页会清除当前归档与导航历史。
- 创建支持 ZIP、7z、TAR、TAR.GZ、TAR.BZ2、TAR.LZ、TAR.ZST，以及单文件 GZ、BZ2、LZ、ZST。ZIP/7z 可设置密码；ZIP 支持 AES-256 或 ZipCrypto（密码限 ASCII），7z 可加密文件名。
- 读取支持 ZIP、7z、RAR、TAR 及 GZip/BZip2/XZ/LZip/Zstandard/LZW 包装的 TAR，单文件 GZ、BZ2、XZ、LZ、ZST、`.Z`，以及 SharpCompress 支持的 ARC、ARJ、ACE 子集。提取支持完整路径、相对于当前目录和不包含目录三种模式。
- 压缩和解压成功后保留 100% 进度窗口，可确认关闭或主动打开输出文件夹，应用内继续停留在当前压缩包；失败或取消时不显示成功状态。
- ZIP/7z 内部添加文件、删除条目和 F2 重命名；支持子目录添加、目录合并和明确选择后替换同名文件。
- 后台操作、按条目更新的进度与取消；文件/文件夹选择、保存面板，以及信息和确认对话框。
- 设置提供支持格式的全选与单独勾选、关联操作、系统实际状态刷新，以及访达／资源管理器菜单的注册和移除。系统确认步骤与平台支持范围见下文。
- 外观设置中的浅色、深色或跟随系统会持久保存，并同步到已打开的应用窗口和新启动的窗口（包括独立压缩包、创建、设置和进度窗口）；Windows 原生标题栏同步跟随窗口主题。

单文件 GZ、BZ2、LZ、ZST 创建只能选择一个文件；文件夹或多个文件请使用对应的 TAR 组合格式。单文件格式可能根据压缩包文件名恢复条目名；GZ 的非 ASCII 名称也采用此方式，重命名压缩包会影响恢复的文件名。TAR.XZ 和 TAR.Z 仅支持读取。当前工作流不支持多卷归档；ACE 仅支持未压缩条目，ARC/ARJ 的可用压缩方法受 SharpCompress 限制，部分 ARC 条目的大小可能显示为零。

嵌套压缩包使用临时副本进行只读浏览；如需修改，请先提取。临时副本在返回首页、切换外层归档或关闭应用时清理。

ZIP/7z 修改会先在独立工作目录重建、校验，确认原包未被其他程序更改后，再替换原包；失败或取消保留原包。加密 ZIP 和其他格式保持只读，无法保留的权限或元数据会拒绝编辑。7z 修改保留所提供的密码，并加密文件名。

ZIP 修改保留条目的原始属性与创建平台标识，包含 Unix 执行权限、访问权限和 DOS 属性；新加入的文件记录源权限。带 ZIP 注释的归档，以及包含当前无法保留的 7z 权限属性的归档暂不允许修改。重建不承诺保留任意格式私有的额外字段。

打开归档内文件时先提取副本，再交给系统默认应用；编辑这个副本不会自动回写。Windows 中，归档内“复制”及文件列表的 Ctrl+C 会把所选文件或文件夹提取到系统临时目录，写入文件剪贴板，可在资源管理器中粘贴。复制保留当前目录下的文件夹结构，加密内容可输入密码后重试；成功的临时副本在窗口关闭后继续保留，供后续粘贴使用，由系统临时文件清理回收。其他平台尚未接入文件剪贴板，可使用“提取所选项目…”。“移动”暂不提供。

常规压缩和提取目标必须是新路径，不覆盖已有文件或合并已有目录。系统菜单的“提取到此处”允许使用压缩包所在的现有目录，但其中与归档内容同名的顶层文件或文件夹会使整次操作停止；不会覆盖或合并。`ArchiveFileService` 先写临时输出，预检并完整提取成功后再提交；失败或取消时清理临时输出。文件工作流拒绝符号链接、不安全路径以及冲突的归档路径。

macOS 和 Linux 上的解压暂存目录、归档编辑工作目录及打开文件的副本目录仅允许当前用户访问（`0700`）。解压完成后的目标根目录保留这一权限。

macOS 的 `/tmp`、`/var`、`/etc` 系统入口在确认真实指向 `/private` 对应目录后会被规范化，普通用户符号链接仍按原策略拒绝。

关闭窗口会取消并等待后台任务清理完成；正式输出已经提交后，以成功状态为准。整包提取和校验使用批量读取，solid 7z/RAR 保持顺序解码，避免为每个文件重复打开归档。

Windows 和 macOS 使用 Vello Sparse 在 CPU 上绘制画面，再由 Direct3D 12 或 Metal 呈现；Linux 使用 Vello 的 Vulkan 渲染后端，要求可用的 Vulkan 驱动（包括软件驱动）。界面变化时重绘，空闲时跳过绘制。离屏预览在 Windows/macOS 使用 Sparse，在 Linux 使用 Vello CPU 后端。

## 开发环境

- .NET 10 SDK；64 位 Windows、Linux 或 macOS 13 及更新版本。目前原生渲染依赖不提供 32 位构建。
- Windows：Direct3D 12、Windows PowerShell 5.1（用于文件选择对话框）。
- Linux：SDL3、HarfBuzz、Vulkan 驱动、`zenity` 和 `xdg-open`（xdg-utils），以及可用的 X11 或 Wayland 桌面。请通过发行版的软件仓库安装这些组件；不同发行版的 SDL3 包名和版本可能不同。
- macOS 开发运行所需的 SDL3、HarfBuzz：

  ```bash
  brew install sdl3 harfbuzz
  ```

- macOS 生成包含访达扩展的 `.app` 需要安装 Xcode，并通过 `xcode-select` 选用对应的开发目录。

macOS 的 Homebrew 依赖用于开发运行和构建时收集原生库。`.app` 打包流程会将 SDL3、HarfBuzz 及所需原生依赖收入应用，并包含自包含 .NET 运行时；分发后的 macOS 应用不要求安装 Homebrew 或 .NET SDK。Windows/Linux 的文件选择器一次选择一组文件或一个文件夹；可在创建或添加对话框中多次选择，合并文件和文件夹，也可清空后重新选择。

## 获取项目

项目目前为本地独立仓库。创建远程仓库后，可将下面的占位地址替换为实际应用仓库地址：

```bash
git clone --recurse-submodules <应用仓库地址> BubuZip
cd BubuZip
```

对于已有的本地检出，初始化并还原子模块：

```bash
git submodule update --init --recursive
```

EclipseUI 位于 `external/EclipseUI`，来源为 <https://github.com/CeSun/EclipseUI.git>，当前固定提交为 `8883952554a6d56ab20360e61c29679b56c3df47`。主仓库记录具体子模块提交，构建使用该版本；应用不依赖相邻的 `../EclipseUI` 工作目录及其未提交修改。

## 构建与运行

在项目根目录执行：

```bash
dotnet build BubuZip.slnx
dotnet run --project src/BubuZip.Desktop
```

使用 `--create` 可直接打开新建压缩窗口。启动时可传入压缩包路径，直接打开并浏览内容（包含空格的路径请加引号）；多个压缩包各自使用独立窗口：

```bash
dotnet run --project src/BubuZip.Desktop -- "/path/to/archive.zip"
```

支持在 Finder 或 Windows 资源管理器的“打开方式”中手动选择应用。macOS 在应用已经运行时，也能接收新的打开请求；正在操作或显示对话框时，请求会等待当前流程结束。加密压缩包会提示输入密码，无法读取的文件会在应用内显示错误。应用不会在启动时自动接管文件关联；可在“设置”中主动关联所选格式。

压缩和解压使用独立进度窗口，支持取消、查看结果和打开输出文件夹。从访达或资源管理器右键菜单执行操作时，只显示操作小窗口；需要选择参数或输入密码时也在小窗口内完成。

macOS 对带下载隔离标记、且保存了单文件“始终使用此应用打开”设置的文件，可能在交给应用之前显示“Apple 无法验证此文件是否包含恶意软件”。Apple 已记录这一系统行为（[官方讨论](https://developer.apple.com/forums/thread/795994)）。此时可先启动 BubuZip，再通过“打开压缩”选择文件；应用不会自动删除下载隔离标记或更改系统安全设置。

指定新建压缩包时的初始建议目录：

```bash
dotnet run --project src/BubuZip.Desktop -- --path "$HOME/Downloads"
```

运行解决方案中的测试：

```bash
dotnet test BubuZip.slnx
```

启动并运行有限帧数，用于检查应用窗口能否正常初始化和退出：

```bash
dotnet run --project src/BubuZip.Desktop -- --frames 3
```

导出离屏 PNG 预览，用于测试布局，不创建桌面窗口：

```bash
dotnet run --project src/BubuZip.Desktop -- --path "$HOME/Downloads" --shot out/preview.png
dotnet run --project src/BubuZip.Desktop -- --path "$HOME/Downloads" --shot out/preview-dark.png --dark
```

离屏预览不打开桌面窗口；默认浅色，`--dark` 输出深色预览。有限帧启动与离屏预览分别用于检查窗口生命周期和布局；Core 与 UI 行为测试通过解决方案测试命令运行。窗口像素回归覆盖浅色、深色及 1 倍、2 倍设备缩放，检查连续帧和选择变化后的画面完整性与像素稳定性。

可使用 .NET 发布 Windows 和 Linux 的自包含目录（在项目根目录运行，目标运行时也可选择对应的 `win-arm64` 或 `linux-arm64`）：

```bash
dotnet publish src/BubuZip.Desktop -c Release -r win-x64 --self-contained true -o out/windows-x64
dotnet publish src/BubuZip.Desktop -c Release -r linux-x64 --self-contained true -o out/linux-x64
```

Windows 11 一级右键菜单还需要原生扩展和稀疏身份包。在 Windows 上安装 Visual Studio C++ 工具、CMake 和 Windows SDK 后，用以下脚本发布完整目录（ARM64 使用 `-Architecture arm64`）：

```powershell
./eng/package-windows.ps1 -Architecture x64
```

输出包含 `BubuZip.exe`、`BubuZipShell.dll` 和 `BubuZip.ContextMenu.msix`。仅运行 `dotnet publish` 不会构建菜单组件。默认 MSIX 尚未签名，不能注册一级菜单；发布时传入 `-Publisher 'CN=你的签名主体' -CertificateThumbprint '证书指纹'`，使用当前用户证书存储中相匹配的代码签名证书签名，或在后续发布流程中单独签名。目标系统必须信任签名证书。应用会报告注册错误，不会自动开启开发者模式、安装证书或更改安全策略。Windows 10 使用经典菜单，不需要身份包。

发布目录包含 .NET 运行时，但不捆绑上述 Linux 桌面组件或图形驱动。将 Linux 目录从 Windows 复制到 Linux 文件系统后，如执行权限未保留，可运行 `chmod +x out/linux-x64/BubuZip`。Windows ARM64、Linux ARM64 的原生依赖需要在相应设备上单独验证。

## 设置与系统右键菜单

从首页点击“设置”，或在归档浏览界面的“更多 → 工具 → 设置…”打开独立设置窗口，关闭后返回原主窗口。关闭主窗口时，已打开的设置或进度窗口继续运行；最后一个窗口关闭后程序退出，主动退出应用则关闭所有窗口。外观可选择跟随系统、浅色或深色，并同步到主窗口及后续进度窗口。文件关联和右键菜单为独立操作，打开设置本身不会更改系统配置。

在“文件格式关联”中选择需要的扩展名，点击“关联所选格式”；可全选或取消全选。状态只显示系统当前实际使用 BubuZip 的默认关联，注册支持或打开系统设置不会被计为关联成功。macOS 会注册应用声明的文档类型，再按类型请求默认打开程序；不写入单个文件的“始终使用此应用打开”设置。Windows 会为当前用户注册所选格式的打开能力，并进入系统“默认应用”页面，仍需用户在 Windows 中确认；应用不修改受保护的默认选择。完成系统确认后，点击“刷新状态”核对结果。Linux 暂不提供这些系统集成设置。

“注册右键菜单”在系统文件管理器中提供一个 BubuZip 子菜单，功能参考 NanaZip，并只列出当前应用已实现的操作：

| 命令 | 行为 |
|---|---|
| 打开压缩包 | 在应用内浏览选中的归档 |
| 提取文件… | 打开提取设置，允许选择新输出目录和密码 |
| 提取到此处 | 直接提取到压缩包所在目录；先检查所有顶层项目的名称冲突，不覆盖或合并已有项目 |
| 提取到同名文件夹 | 直接创建新文件夹；同名目录已存在时使用带序号的新名称 |
| 测试压缩包 | 读取并校验归档内容，不输出提取文件 |
| 添加到压缩包… | 将所选文件或文件夹预填到创建对话框，再选择格式、路径和密码 |
| 压缩为同名 ZIP／7z 文件 | 直接创建真正的 ZIP／7z 归档；已有目标不覆盖，使用带序号的新名称 |

提取和测试类命令只针对归档显示，普通文件与文件夹提供压缩命令。快捷操作仍使用相同的后台进度、取消、密码输入和错误提示流程。

macOS 应用包包含 `BubuZipFinder.appex`。点击注册后会打开访达扩展管理页面，用户需要在系统设置中启用“BubuZip 菜单”；仅注册不等于已启用。启用后菜单作用于当前用户主目录和 `/Volumes` 下的磁盘，范围之外不显示。扩展通过系统文档打开事件把操作交给主程序，不依赖扩展启动参数；菜单操作各自打开一个应用窗口，也可在主程序运行时发起。临时请求仅从扩展的私有目录读取，校验后消费一次，过期或来源不符的请求不会执行。写入或启动失败会显示提示。点击“移除右键菜单”会停用扩展并移除其注册，保留应用包内的扩展，之后可重新注册。

Windows 11 使用原生 `IExplorerCommand` 扩展和稀疏身份包，在一级右键菜单提供 BubuZip 子菜单；Windows 10 使用当前用户的经典级联菜单。当前支持单个文件或文件夹，多选时不显示。旧版用户需要更新完整发布目录后，再点击“注册右键菜单”；只有现代菜单注册成功才会移除当前安装拥有的经典菜单。系统缓存可能导致新菜单暂不显示，可重启资源管理器或注销后重新登录。注册必须从 Windows 发布版 `BubuZip.exe` 启动，不能从 `dotnet` 开发宿主注册。移除菜单只删除本应用当前可执行文件拥有的菜单和身份包注册，不修改文件关联。

设置页会区分“已注册”“等待系统确认”和“已启用”。系统关联和扩展启用需要在对应操作系统上验收，后端及界面测试不代替真实系统确认。应用不自动清除下载隔离标记或降低系统安全检查。

可直接进入设置，或导出设置界面的离屏预览：

```bash
dotnet run --project src/BubuZip.Desktop -- --settings
dotnet run --project src/BubuZip.Desktop -- --settings --shot out/settings.png
```

## Core 流式接口

`IArchiveEngine` 与实现 `SharpCompressArchiveEngine` 位于 `src/BubuZip.Core`，不依赖 UI 框架。

| 接口 | 用途 |
|---|---|
| `CreateAsync` | 将 `ArchiveWriteEntry` 列表写入 ZIP、7z、TAR 组合或单文件压缩流 |
| `ListAsync` | 从输入流读取归档条目列表，可提供密码 |
| `ExtractEntryAsync` | 按条目路径读取归档内容并写入调用者提供的输出流，可提供密码 |

可选的 `IBatchArchiveEngine.ExtractEntriesAsync` 在同一归档会话内处理多个条目；目标流通过回调按需打开，由引擎逐条释放，输入归档流仍由调用者持有。

`ArchiveWriteEntry` 包含归档内路径、内容流和可选修改时间；`Content = null` 表示目录。所有调用者提供的流均保持打开。创建归档需要空输出流；列目录与提取需要从归档起点读取，调用者在复用流时应自行设置 `Position = 0`。操作在后台执行并接受 `CancellationToken`；失败或取消后应丢弃不完整输出。

7z 使用 LZMA2 非固实写入，压缩级别映射到字典和快字节参数，支持密码和 `encryptFileNames` 文件名隐藏。ZIP 支持存储和 Deflate 压缩，以及 AES-256 或 ZipCrypto 加密；为保持读取兼容，ZipCrypto 创建时的密码仅支持 ASCII。输入归档的密码通过列目录与提取接口传入。TAR 写入需要可定位且位于开头的文件输入流；GZ、BZ2、LZ、ZST 只接受一个文件。TAR 组合格式通过顺序读取，外层压缩流先完整校验；展平路径后若出现同名冲突，提取会拒绝执行。

流式引擎只读写调用者提供的流；同项目中的 `ArchiveFileService` 负责实际磁盘文件枚举、创建、提取、读取测试、路径预检、临时输出和提交，`ArchiveEditService` 负责原包修改。UI 调用文件服务并显示操作状态。版本及接口边界见 [架构说明](docs/architecture.md)。

## 打包 macOS 应用

```bash
./eng/package-macos.sh
```

默认生成 `out/macos/BubuZip.app`，按本机架构构建并使用本地 ad hoc 签名。最低系统版本为 macOS 13。该签名用于本地验证；对外分发可透传 Developer ID 与可选公证参数：

```bash
./eng/package-macos.sh \
  --sign-identity "Developer ID Application: Your Name (TEAMID)" \
  --notary-profile YOUR_KEYCHAIN_PROFILE
```

`--notary-profile` 使用预先配置的钥匙串凭据，并会上传应用进行公证。仅传 Developer ID 签名参数不会自动上传。

常规打包和 `./eng/package-macos-nativeaot.sh` 的 NativeAOT 打包都包含文件类型声明与访达扩展；扩展在签名和可选公证之前收入应用包。打包不会更改本机文件关联或自动启用访达扩展。

可分别构建 Apple Silicon 和 Intel 版本：

```bash
./eng/package-macos.sh --rid osx-arm64
./eng/package-macos.sh --rid osx-x64 --library-dir /path/to/intel/libraries
```

每个版本需要相同架构的 SDL3、HarfBuzz 及其依赖。Intel 包应使用 Intel 原生库并在对应设备验收。输出已存在时，可传 `--force`，打包器会在新包验证成功后替换旧包。

## 目录

| 路径 | 用途 |
|---|---|
| `src/BubuZip.Core` | SharpCompress 流式引擎、磁盘文件工作流及进度模型，无 UI 依赖 |
| `src/BubuZip` | 主窗口、文件列表、浏览服务、设置与桌面／系统集成接口 |
| `src/BubuZip.Desktop` | 跨平台桌面入口、系统对话框、文件关联、系统菜单注册与离屏预览 |
| `tests/BubuZip.Core.Tests` | 流式引擎与文件服务测试 |
| `tests/BubuZip.Ui.Tests` | 文件浏览和主窗口交互测试 |
| `external/EclipseUI` | 固定提交的 EclipseUI Git 子模块 |
| `eng/package-macos.sh` | 复用 EclipseUI 打包器的应用入口 |
| `eng/macos-finder-extension` | 原生 Finder Sync 扩展及构建配置 |
| `docs/architecture.md` | 工程边界及后续扩展方式 |

项目边界与数据流见 [架构说明](docs/architecture.md)。
