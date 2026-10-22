using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CipherVault.Host.Contracts;
using Microsoft.Win32.SafeHandles;

namespace CipherVault.Security.Win;

public static class WindowsProcessIdentityVerifier
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const uint ErrorAccessDenied = 5;
    private const uint ErrorInvalidParameter = 87;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint SeGroupLogonId = 0xC0000000;
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;

    public static ProcessVerificationResult Verify(
        uint processId,
        ProcessVerificationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!OperatingSystem.IsWindows())
        {
            return ProcessVerificationResult.Failed(
                ProcessVerificationStatus.Error,
                "unsupported_platform");
        }

        var handle = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            processId);
        if (handle.IsInvalid)
        {
            var error = (uint)Marshal.GetLastWin32Error();
            handle.Dispose();
            return error switch
            {
                ErrorAccessDenied => ProcessVerificationResult.Failed(
                    ProcessVerificationStatus.AccessDenied,
                    "process_access_denied"),
                ErrorInvalidParameter => ProcessVerificationResult.Failed(
                    ProcessVerificationStatus.ProcessNotFound,
                    "process_not_found"),
                _ => ProcessVerificationResult.Failed(
                    ProcessVerificationStatus.Error,
                    "process_open_failed")
            };
        }

        try
        {
            var result = VerifyHandle(handle, processId, policy);
            if (result.Evidence is null)
            {
                handle.Dispose();
            }
            return result;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static string ComputeFileSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static ProcessVerificationResult VerifyHandle(
        SafeProcessHandle processHandle,
        uint processId,
        ProcessVerificationPolicy policy)
    {
        var imagePath = GetProcessImagePath(processHandle);
        var startedAt = GetProcessStartedAt(processHandle);
        var imageSha256 = ComputeFileSha256(imagePath);
        using var token = OpenToken(processHandle);
        var userSid = GetTokenSid(token, TokenInformationClass.TokenUser);
        var logonSid = GetLogonSid(token);
        var sessionId = GetTokenUInt32(token, TokenInformationClass.TokenSessionId);
        var integrityLevel = GetIntegrityLevel(token);

        string? signerThumbprint = null;
        string? signerSubject = null;
        var signatureRequired = policy.RequireSignature ||
            policy.AllowedSignerThumbprints.Count > 0 ||
            policy.AllowedImages.Any(image =>
                !string.IsNullOrWhiteSpace(image.SignerThumbprint));
        var signatureValid = !signatureRequired ||
            TryGetValidSigner(
                imagePath,
                out signerThumbprint,
                out signerSubject);

        if (!string.IsNullOrWhiteSpace(policy.ExpectedUserSid) &&
            !string.Equals(policy.ExpectedUserSid, userSid, StringComparison.OrdinalIgnoreCase))
        {
            return ProcessVerificationResult.Failed(
                ProcessVerificationStatus.IdentityMismatch,
                "user_sid_mismatch");
        }
        if (!string.IsNullOrWhiteSpace(policy.ExpectedLogonSid) &&
            !string.Equals(policy.ExpectedLogonSid, logonSid, StringComparison.OrdinalIgnoreCase))
        {
            return ProcessVerificationResult.Failed(
                ProcessVerificationStatus.IdentityMismatch,
                "logon_sid_mismatch");
        }
        if (policy.ExpectedSessionId is not null &&
            policy.ExpectedSessionId.Value != sessionId)
        {
            return ProcessVerificationResult.Failed(
                ProcessVerificationStatus.IdentityMismatch,
                "session_id_mismatch");
        }

        var normalizedPath = Path.GetFullPath(imagePath);
        var imageMatched = policy.AllowedImages.Any(image =>
            image.Kind == policy.ExpectedKind &&
            string.Equals(
                Path.GetFullPath(image.FinalImagePath),
                normalizedPath,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                image.ImageSha256,
                imageSha256,
                StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(image.SignerThumbprint) ||
             (signerThumbprint is not null &&
              string.Equals(
                  image.SignerThumbprint,
                  signerThumbprint,
                  StringComparison.OrdinalIgnoreCase))));
        var signerMatched = signerThumbprint is not null &&
            policy.AllowedSignerThumbprints.Contains(
                signerThumbprint,
                StringComparer.OrdinalIgnoreCase);

        if (policy.AllowedImages.Count == 0 &&
            policy.AllowedSignerThumbprints.Count == 0)
        {
            return ProcessVerificationResult.Failed(
                ProcessVerificationStatus.IdentityMismatch,
                "empty_process_policy");
        }
        if (policy.AllowedImages.Count > 0 && !imageMatched)
        {
            return ProcessVerificationResult.Failed(
                ProcessVerificationStatus.HashInvalid,
                "image_mismatch");
        }
        if (policy.RequireSha256 &&
            imageSha256.Length != 64)
        {
            return ProcessVerificationResult.Failed(
                ProcessVerificationStatus.HashInvalid,
                "image_hash_missing");
        }
        if ((policy.RequireSignature || policy.AllowedSignerThumbprints.Count > 0) &&
            (!signatureValid || signerThumbprint is null))
        {
            return ProcessVerificationResult.Failed(
                ProcessVerificationStatus.SignatureInvalid,
                "signature_invalid");
        }
        if (policy.AllowedSignerThumbprints.Count > 0 && !signerMatched)
        {
            return ProcessVerificationResult.Failed(
                ProcessVerificationStatus.SignatureInvalid,
                "signer_not_allowed");
        }

        var identity = new VerifiedProcessIdentity(
            policy.ExpectedKind,
            processId,
            startedAt,
            userSid,
            logonSid,
            sessionId,
            normalizedPath,
            imageSha256,
            signerThumbprint,
            signerSubject,
            integrityLevel);
        return ProcessVerificationResult.Verified(
            new VerifiedProcessEvidence(processHandle, identity));
    }

    private static SafeProcessHandle OpenProcess(
        uint desiredAccess,
        bool inheritHandle,
        uint processId)
    {
        return NativeMethods.OpenProcess(desiredAccess, inheritHandle, processId);
    }

    private static SafeAccessTokenHandle OpenToken(SafeProcessHandle processHandle)
    {
        if (!NativeMethods.OpenProcessToken(
                processHandle,
                TokenQuery,
                out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return token;
    }

    private static string GetProcessImagePath(SafeProcessHandle processHandle)
    {
        var capacity = 32_768u;
        var builder = new StringBuilder((int)capacity);
        if (!NativeMethods.QueryFullProcessImageName(
                processHandle,
                flags: 0,
                builder,
                ref capacity))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return builder.ToString();
    }

    private static DateTimeOffset GetProcessStartedAt(
        SafeProcessHandle processHandle)
    {
        if (!NativeMethods.GetProcessTimes(
                processHandle,
                out var creation,
                out _,
                out _,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var fileTime = ((long)creation.HighDateTime << 32) | creation.LowDateTime;
        return new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime));
    }

    private static string GetTokenSid(
        SafeAccessTokenHandle token,
        TokenInformationClass informationClass)
    {
        using var buffer = GetTokenInformation(token, informationClass);
        var user = Marshal.PtrToStructure<TokenUser>(buffer.DangerousGetHandle());
        return ConvertSid(user.User.Sid);
    }

    private static string GetLogonSid(SafeAccessTokenHandle token)
    {
        using var buffer = GetTokenInformation(
            token,
            TokenInformationClass.TokenGroups);
        var count = Marshal.ReadInt32(buffer.DangerousGetHandle());
        var groupOffset = IntPtr.Size == 8 ? 8 : 4;
        var groupSize = Marshal.SizeOf<SidAndAttributes>();
        for (var index = 0; index < count; index++)
        {
            var pointer = IntPtr.Add(
                buffer.DangerousGetHandle(),
                groupOffset + (index * groupSize));
            var group = Marshal.PtrToStructure<SidAndAttributes>(pointer);
            if ((group.Attributes & SeGroupLogonId) == SeGroupLogonId)
            {
                return ConvertSid(group.Sid);
            }
        }
        throw new InvalidOperationException("Process token does not contain a logon SID.");
    }

    private static uint GetTokenUInt32(
        SafeAccessTokenHandle token,
        TokenInformationClass informationClass)
    {
        using var buffer = GetTokenInformation(token, informationClass);
        return unchecked((uint)Marshal.ReadInt32(buffer.DangerousGetHandle()));
    }

    private static int GetIntegrityLevel(SafeAccessTokenHandle token)
    {
        using var buffer = GetTokenInformation(
            token,
            TokenInformationClass.TokenIntegrityLevel);
        var label = Marshal.PtrToStructure<TokenMandatoryLabel>(
            buffer.DangerousGetHandle());
        var countPointer = NativeMethods.GetSidSubAuthorityCount(label.Label.Sid);
        if (countPointer == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var count = Marshal.ReadByte(countPointer);
        if (count == 0)
        {
            return 0;
        }
        var valuePointer = NativeMethods.GetSidSubAuthority(
            label.Label.Sid,
            (uint)(count - 1));
        return valuePointer == IntPtr.Zero
            ? throw new Win32Exception(Marshal.GetLastWin32Error())
            : Marshal.ReadInt32(valuePointer);
    }

    private static SafeHGlobalHandle GetTokenInformation(
        SafeAccessTokenHandle token,
        TokenInformationClass informationClass)
    {
        _ = NativeMethods.GetTokenInformation(
            token,
            informationClass,
            IntPtr.Zero,
            0,
            out var requiredLength);
        if (requiredLength == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var buffer = Marshal.AllocHGlobal((int)requiredLength);
        try
        {
            if (!NativeMethods.GetTokenInformation(
                    token,
                    informationClass,
                    buffer,
                    requiredLength,
                    out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return new SafeHGlobalHandle(buffer);
        }
        catch
        {
            Marshal.FreeHGlobal(buffer);
            throw;
        }
    }

    private static string ConvertSid(IntPtr sid)
    {
        if (!NativeMethods.ConvertSidToStringSid(sid, out var stringSid))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            return Marshal.PtrToStringUni(stringSid)
                ?? throw new InvalidOperationException("SID conversion returned null.");
        }
        finally
        {
            NativeMethods.LocalFree(stringSid);
        }
    }

    private static bool TryGetValidSigner(
        string imagePath,
        out string? thumbprint,
        out string? subject)
    {
        thumbprint = null;
        subject = null;
        if (!VerifyAuthenticode(imagePath))
        {
            return false;
        }

        try
        {
#pragma warning disable SYSLIB0057
            using var certificate = X509Certificate.CreateFromSignedFile(imagePath);
#pragma warning restore SYSLIB0057
            using var certificate2 = X509CertificateLoader.LoadCertificate(
                certificate.GetRawCertData());
            thumbprint = certificate2.GetCertHashString(HashAlgorithmName.SHA256);
            subject = certificate2.Subject;
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool VerifyAuthenticode(string imagePath)
    {
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = imagePath
        };
        var fileInfoPointer = Marshal.AllocHGlobal(
            Marshal.SizeOf<WinTrustFileInfo>());
        var dataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = fileInfoPointer,
                dwStateAction = WtdStateActionVerify
            };
            Marshal.StructureToPtr(data, dataPointer, fDeleteOld: false);
            var action = WinTrustActionGenericVerifyV2;
            var result = NativeMethods.WinVerifyTrust(
                IntPtr.Zero,
                ref action,
                dataPointer);

            data.dwStateAction = WtdStateActionClose;
            Marshal.StructureToPtr(data, dataPointer, fDeleteOld: false);
            _ = NativeMethods.WinVerifyTrust(
                IntPtr.Zero,
                ref action,
                dataPointer);
            return result == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(dataPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    private enum TokenInformationClass
    {
        TokenUser = 1,
        TokenGroups = 2,
        TokenSessionId = 12,
        TokenIntegrityLevel = 25
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenUser
    {
        public SidAndAttributes User;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public SidAndAttributes Label;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    private sealed class SafeHGlobalHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeHGlobalHandle(IntPtr pointer)
            : base(ownsHandle: true)
        {
            SetHandle(pointer);
        }

        protected override bool ReleaseHandle()
        {
            Marshal.FreeHGlobal(handle);
            return true;
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeProcessHandle OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint processId);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageName(
            SafeProcessHandle process,
            uint flags,
            StringBuilder imagePath,
            ref uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetProcessTimes(
            SafeProcessHandle process,
            out FileTime creationTime,
            out FileTime exitTime,
            out FileTime kernelTime,
            out FileTime userTime);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(
            SafeProcessHandle process,
            uint desiredAccess,
            out SafeAccessTokenHandle token);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetTokenInformation(
            SafeAccessTokenHandle token,
            TokenInformationClass informationClass,
            IntPtr information,
            uint informationLength,
            out uint returnLength);

        [DllImport(
            "advapi32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ConvertSidToStringSid(
            IntPtr sid,
            out IntPtr stringSid);

        [DllImport("advapi32.dll")]
        public static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

        [DllImport("advapi32.dll")]
        public static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

        [DllImport("kernel32.dll")]
        public static extern IntPtr LocalFree(IntPtr memory);

        [DllImport("wintrust.dll", PreserveSig = true, SetLastError = true)]
        public static extern uint WinVerifyTrust(
            IntPtr windowHandle,
            ref Guid actionId,
            IntPtr trustData);
    }
}
