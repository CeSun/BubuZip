# Windows 自签发布

固定复用同一份证书，避免每次发布都要求用户重新信任。工作流要求配置
`WINDOWS_CERTIFICATE_PFX` 和 `WINDOWS_CERTIFICATE_PASSWORD` 两个 Actions Secrets；
缺失时停止 Windows 构建，不回退到未签名包，也不在 CI 中临时生成证书。

## 首次生成

在 Windows 仓库根目录运行：

```powershell
./eng/new-windows-signing-certificate.ps1
```

默认证书主体为 `CN=BubuZip`，有效期三年。证书及私钥位于当前用户个人证书存储，
并导出到 `%LOCALAPPDATA%\BubuZip\signing`：

- `BubuZip.Signing.pfx`：含私钥的加密证书，保留备份，不能上传到代码仓库或 Release。
- `BubuZip.Signing.cer`：可公开分发的公钥证书。
- `BubuZip.Signing.password.dpapi`：使用 Windows DPAPI 加密的 PFX 密码，只有本机当前用户能解密；跨机器备份时应另行将密码存入密码管理器。

脚本不会安装受信任证书，也不会覆盖已有文件。已有证书直接复用，无需重新生成。

## 配置 GitHub Actions

打开仓库 Settings → Secrets and variables → Actions → New repository secret。
以下命令只复制到剪贴板，粘贴到对应 Secret 后清空剪贴板。

`WINDOWS_CERTIFICATE_PFX`：

```powershell
$signingDirectory = Join-Path $env:LOCALAPPDATA 'BubuZip/signing'
[Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $signingDirectory 'BubuZip.Signing.pfx'))) | Set-Clipboard
```

`WINDOWS_CERTIFICATE_PASSWORD`：

```powershell
$signingDirectory = Join-Path $env:LOCALAPPDATA 'BubuZip/signing'
$password = Get-Content -LiteralPath (Join-Path $signingDirectory 'BubuZip.Signing.password.dpapi') | ConvertTo-SecureString
[PSCredential]::new('signing', $password).GetNetworkCredential().Password | Set-Clipboard
```

两个 Secret 配置完成后运行发布工作流。CI 将证书导入临时 runner 的个人证书存储，
使用证书的 Subject 填写 MSIX Publisher，给 NativeAOT 的 BubuZip.exe、BubuZipShell.dll 及菜单 MSIX
添加 SHA256 签名和 RFC 3161 时间戳，核对签名、签名者和时间戳后再打 ZIP。
完成或失败时清理导入的私钥。第三方运行库保留原状。

包内附 `BubuZip.Signing.cer`、`Windows-Certificate.txt` 和
`Windows-Certificate-Fingerprint.txt`。用户按说明手动将公钥证书安装到
LocalMachine 的 TrustedPeople 后，才能注册 Windows 11 一级菜单。
自签不会自动获得公共信任，也不保证消除 SmartScreen 提示。

## 本地签名

```powershell
./eng/package-windows.ps1 -Architecture x64 -Publisher 'CN=BubuZip' -CertificateThumbprint '证书指纹'
```

`Publisher` 必须与证书的 Subject 一致。签名时需要网络访问时间戳服务，失败会终止打包。

参考：[微软创建自签证书和信任存储说明](https://learn.microsoft.com/en-us/windows/msix/package/create-certificate-package-signing)。
