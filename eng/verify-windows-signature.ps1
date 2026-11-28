param(
    [Parameter(Mandatory)][string[]]$Path,
    [Parameter(Mandatory)][string]$CertificateThumbprint
)
$ErrorActionPreference = 'Stop'
if (!('BubuZip.Signing.NativeTrust' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace BubuZip.Signing {
    public static class NativeTrust {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileInfo {
            public uint Size;
            [MarshalAs(UnmanagedType.LPWStr)] public string Path;
            public IntPtr File, Subject;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct TrustData {
            public uint Size;
            public IntPtr Policy, Sip;
            public uint UI, Revocation, UnionChoice;
            public IntPtr FileInfo;
            public uint StateAction;
            public IntPtr StateData, Url;
            public uint Flags, Context;
            public IntPtr SignatureSettings;
        }
        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern uint WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
        public static uint Verify(string path) {
            var file = new FileInfo { Size = (uint)Marshal.SizeOf(typeof(FileInfo)), Path = path };
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FileInfo)));
            Marshal.StructureToPtr(file, pointer, false);
            var data = new TrustData {
                Size = (uint)Marshal.SizeOf(typeof(TrustData)), UI = 2,
                UnionChoice = 1, FileInfo = pointer, StateAction = 1
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            try { return WinVerifyTrust(new IntPtr(-1), ref action, ref data); }
            finally {
                data.StateAction = 2;
                WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                Marshal.DestroyStructure(pointer, typeof(FileInfo));
                Marshal.FreeHGlobal(pointer);
            }
        }
    }
}
'@
}
foreach ($file in $Path) {
    $fullPath = (Resolve-Path -LiteralPath $file).ProviderPath
    $result = [BubuZip.Signing.NativeTrust]::Verify($fullPath)
    $signature = Get-AuthenticodeSignature -LiteralPath $fullPath
    # PowerShell maps CERT_E_UNTRUSTEDROOT to UnknownError. Accept only that exact
    # WinVerifyTrust result for our pinned self-signed certificate, never arbitrary errors.
    $untrustedSelfSigned = $result -eq 0x800B0109L -and
        $signature.SignerCertificate.Subject -eq $signature.SignerCertificate.Issuer
    if (($result -ne 0 -and !$untrustedSelfSigned) -or
        $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint -or
        !$signature.TimeStamperCertificate) {
        throw ('Signature, signer or timestamp verification failed: {0} (0x{1:X8})' -f $fullPath, $result)
    }
    Write-Host "Verified signature and timestamp: $fullPath"
}
