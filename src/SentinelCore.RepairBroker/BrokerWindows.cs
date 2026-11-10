using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SentinelCore.RepairBroker;

internal static class BrokerWindows
{
    internal const string AppName = "SentinelCoreAI.exe";
    internal const string BrokerName = "SentinelCore.RepairBroker.exe";
    internal const string PipePrefix = "SentinelCore.Repair.";
    // Future packaging must pin the reviewed standalone AOT image before the launcher can elevate it.
    private static readonly string? BrokerImageSha256 = null;

    internal static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SentinelCoreAI");

    internal static FileStream OpenPinnedBrokerImage(string imagePath)
    {
        if (BrokerImageSha256 is null)
            throw new UnauthorizedAccessException("Imagem do broker ainda não aprovada para elevação.");
        var image = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(image)), BrokerImageSha256,
                    StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Imagem do broker diverge da distribuição aprovada.");
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    internal static string InstalledImage(string name)
    {
        var path = Path.Combine(InstallDirectory, name);
        if (!Path.IsPathFullyQualified(path) ||
            (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new UnauthorizedAccessException("Imagem do broker ou cliente fora da instalação confiável.");
        for (var directory = new DirectoryInfo(InstallDirectory); directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Instalação do broker contém redirecionamento.");
        }
        return path;
    }

    internal static string CurrentSid()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return identity.User?.Value ?? throw new UnauthorizedAccessException("SID indisponível.");
    }

    internal static int CurrentSession()
    {
        using var current = Process.GetCurrentProcess();
        return current.SessionId;
    }

    internal static NamedPipeServerStream CreateLocalPipe(string pipeName, string sid)
    {
        const uint DuplexOverlappedFirst = 0x00000003 | 0x40000000 | 0x00080000;
        const uint RejectRemoteClients = 0x00000008;
        var descriptor = $"D:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GA;;;{new SecurityIdentifier(sid).Value})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(descriptor, 1, out var security, out _))
            throw new Win32Exception();
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = security
            };
            var handle = CreateNamedPipeW(@"\\.\pipe\" + pipeName, DuplexOverlappedFirst,
                RejectRemoteClients, 1, 4096, 4096, 0, ref attributes);
            if (handle.IsInvalid)
            {
                var error = new Win32Exception();
                handle.Dispose();
                throw error;
            }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(security); }
    }

    internal static SafeProcessHandle Authenticate(PipeStream pipe, bool peerIsServer, int expectedPid,
        int expectedSession, string expectedSid, string imageName, out bool elevated)
    {
        uint pid, session = 0;
        var handle = pipe.SafePipeHandle;
        var found = peerIsServer
            ? GetNamedPipeServerProcessId(handle, out pid) && GetNamedPipeServerSessionId(handle, out session)
            : GetNamedPipeClientProcessId(handle, out pid) && GetNamedPipeClientSessionId(handle, out session);
        if (!found || pid != expectedPid || session != expectedSession || expectedSession <= 0)
            throw new UnauthorizedAccessException("PID ou sessão do par IPC divergente.");

        const uint QueryLimitedAndSynchronize = 0x00001000 | 0x00100000;
        var process = OpenProcess(QueryLimitedAndSynchronize, false, pid);
        try
        {
            var image = new StringBuilder(32768);
            var length = image.Capacity;
            if (process.IsInvalid || !QueryFullProcessImageNameW(process, 0, image, ref length) ||
                !string.Equals(image.ToString(), InstalledImage(imageName), StringComparison.OrdinalIgnoreCase) ||
                !OpenProcessToken(process, 0x0008, out var token))
                throw new UnauthorizedAccessException("Imagem ou token do par IPC indisponível.");
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            {
                if (!string.Equals(identity.User?.Value, expectedSid, StringComparison.Ordinal) ||
                    !GetTokenInformation(token, 20, out var tokenElevation, sizeof(int), out _))
                    throw new UnauthorizedAccessException("Conta ou elevação do par IPC divergente.");
                elevated = tokenElevation != 0;
            }
            if (!IsAlive(process))
                throw new UnauthorizedAccessException("Par IPC já encerrado.");
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    internal static bool IsAlive(SafeProcessHandle process) => WaitForSingleObject(process, 0) == 0x102;

    internal static string? VolumeIdentity(string root)
    {
        var mountName = new StringBuilder(64);
        return GetVolumeNameForVolumeMountPointW(root, mountName, mountName.Capacity) ? mountName.ToString() : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr Descriptor;
        internal int InheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode,
        uint maxInstances, uint outBuffer, uint inBuffer, uint timeout, ref SecurityAttributes attributes);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision,
        out IntPtr descriptor, out uint length);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint session);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint session);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder image, ref int length);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out int value, int length,
        out int returnedLength);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string root, StringBuilder name, int capacity);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    internal static extern uint GetOEMCP();
}
