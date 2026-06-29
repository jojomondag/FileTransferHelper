using System.Runtime.InteropServices;
using System.Text;

namespace FileTransferHelper.Services;

public static class CredentialStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;

    public static string PasswordFor(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return string.Empty;
        }

        if (!OperatingSystem.IsWindows())
        {
            return string.Empty;
        }

        var credentialPointer = IntPtr.Zero;
        foreach (var target in TargetNames(address))
        {
            if (CredRead(target, CredTypeGeneric, 0, out credentialPointer))
            {
                break;
            }
        }

        if (credentialPointer == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return string.Empty;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }
        catch (Exception exc)
        {
            LogWriter.Write(AppPaths.TransferLogPath, $"Kunde inte läsa lösenord från Credential Manager för {address}: {exc.Message}");
            return string.Empty;
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public static void SavePassword(string address, string password)
    {
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrEmpty(password) || !OperatingSystem.IsWindows())
        {
            return;
        }

        var passwordBytes = Encoding.Unicode.GetBytes(password);
        var blobPointer = Marshal.AllocCoTaskMem(passwordBytes.Length);
        try
        {
            Marshal.Copy(passwordBytes, 0, blobPointer, passwordBytes.Length);
            foreach (var target in TargetNames(address))
            {
                var credential = new NativeCredential
                {
                    Type = CredTypeGeneric,
                    TargetName = target,
                    CredentialBlobSize = passwordBytes.Length,
                    CredentialBlob = blobPointer,
                    Persist = CredPersistLocalMachine,
                    UserName = address
                };

                if (!CredWrite(ref credential, 0))
                {
                    var error = Marshal.GetLastWin32Error();
                    LogWriter.Write(AppPaths.TransferLogPath, $"Kunde inte spara lösenord i Credential Manager för {address}: Win32 {error}");
                }
            }
        }
        catch (Exception exc)
        {
            LogWriter.Write(AppPaths.TransferLogPath, $"Kunde inte spara lösenord i Credential Manager för {address}: {exc.Message}");
        }
        finally
        {
            Marshal.FreeCoTaskMem(blobPointer);
        }
    }

    private static IEnumerable<string> TargetNames(string address)
    {
        yield return $"{AppPaths.AppTitle}:{address}";
        yield return $"{AppPaths.AppTitle}/{address}";
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential credential, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }
}
