using System.Runtime.InteropServices;

namespace GamePingBooster.Service.Update;

/// <summary>
/// Starts a program on a signed-in user's desktop, as that user, from the service.
///
/// Needed once per update. Setup closes the app before it copies anything, and when the SERVICE ran setup
/// there is no unelevated process of the user's left to start it again - setup's own runasoriginaluser
/// would start it as SYSTEM, on no desktop anyone can see. So the new service, once it is up, starts the
/// app in the session it was open in (AutoUpdater.FinishPreviousAttempt).
///
/// The token WTSQueryUserToken returns is the user's ordinary one - the filtered token for an administrator
/// with UAC on - so the app comes back exactly as unprivileged as it always runs. LocalSystem holds the
/// SeTcbPrivilege the call needs.
/// </summary>
internal static partial class UserSession
{
    /// <summary>The Terminal Services session of the process at the other end of a named pipe, or null.</summary>
    public static uint? OfPipeClient(SafeHandle pipe)
    {
        return GetNamedPipeClientSessionId(pipe.DangerousGetHandle(), out var session) ? session : null;
    }

    /// <summary>Starts <paramref name="exe"/> in <paramref name="session"/> as its user. Null when it started, otherwise why not.</summary>
    public static unsafe string? Start(uint session, string exe)
    {
        if (!WTSQueryUserToken(session, out var token))
        {
            return $"no user token for session {session} (error {Marshal.GetLastPInvokeError()})";
        }

        nint environment = 0;
        var desktop = Marshal.StringToHGlobalUni(@"winsta0\default");
        var exePtr = Marshal.StringToHGlobalUni(exe);
        // CreateProcessW may write to the command line, so it is a buffer of its own.
        var commandLine = Marshal.StringToHGlobalUni($"\"{exe}\"");
        var directory = Marshal.StringToHGlobalUni(Path.GetDirectoryName(exe)!);
        try
        {
            // The user's own environment (their %TEMP%, %LOCALAPPDATA%), not the service's.
            if (!CreateEnvironmentBlock(out environment, token, false)) environment = 0;

            var startup = new StartupInfo { cb = (uint)sizeof(StartupInfo), lpDesktop = desktop };
            const uint CreateUnicodeEnvironment = 0x00000400;
            if (!CreateProcessAsUserW(token, exePtr, commandLine, 0, 0, false,
                    environment != 0 ? CreateUnicodeEnvironment : 0, environment, directory,
                    &startup, out var info))
            {
                return $"CreateProcessAsUser failed (error {Marshal.GetLastPInvokeError()})";
            }
            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
            return null;
        }
        finally
        {
            if (environment != 0) DestroyEnvironmentBlock(environment);
            Marshal.FreeHGlobal(desktop);
            Marshal.FreeHGlobal(exePtr);
            Marshal.FreeHGlobal(commandLine);
            Marshal.FreeHGlobal(directory);
            CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public nint lpReserved2;
        public nint hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint hProcess;
        public nint hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientSessionId(nint pipe, out uint sessionId);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQueryUserToken(uint sessionId, out nint token);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateEnvironmentBlock(out nint environment, nint token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [LibraryImport("userenv.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyEnvironmentBlock(nint environment);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessAsUserW(
        nint token, nint applicationName, nint commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, nint environment,
        nint currentDirectory, StartupInfo* startupInfo, out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
