// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RECompose.Presentation.Services;

/// <summary>Secrets such as the Anthropic API key, kept outside settings.json so they survive updates (AS-1).</summary>
public interface ISecretStore
{
    string? Get(string name);

    /// <summary>Stores a secret; null or empty deletes it.</summary>
    void Set(string name, string? value);
}

/// <summary>Secrets for tests and for the session only.</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = [];

    public string? Get(string name) => _values.GetValueOrDefault(name);

    public void Set(string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
            _values.Remove(name);
        else
            _values[name] = value;
    }
}

/// <summary>
/// The OS credential store: Windows Credential Manager, the macOS Keychain. Elsewhere a file only the user can read in the
/// data folder.
/// </summary>
public sealed partial class OsSecretStore(string dataRoot) : ISecretStore
{
    private const string Service = "RECompose";

    public string? Get(string name)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return WindowsRead($"{Service}/{name}");
            if (OperatingSystem.IsMacOS())
                return Security($"find-generic-password -s {Service} -a {name} -w", out var output) ? output.TrimEnd('\n') : null;
            var file = FileFor(name);
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
        {
            AppLog.Error($"Reading {name}", e);
            return null;
        }
    }

    public void Set(string name, string? value)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsWrite($"{Service}/{name}", value);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            // The secret goes over stdin (security -i), never on a command line other processes can see.
            var ok = string.IsNullOrEmpty(value)
                ? Security($"delete-generic-password -s {Service} -a {name}", out _) || true
                : Security($"add-generic-password -U -s {Service} -a {name} -w \"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"", out _);
            if (!ok)
                throw new IOException("The Keychain did not store the key.");
            return;
        }

        // shortcut: a 0600 file on Linux, no libsecret; upgrade when Linux builds ship.
        var file = FileFor(name);
        if (string.IsNullOrEmpty(value))
        {
            File.Delete(file);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, value);
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private string FileFor(string name) => Path.Combine(dataRoot, "secrets", name);

    private static bool Security(string command, out string output)
    {
        var start = new ProcessStartInfo("/usr/bin/security", "-i")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start) ?? throw new IOException("security did not start");
        process.StandardInput.WriteLine(command);
        process.StandardInput.Close();
        output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(5000);
        return process.ExitCode == 0 && error.Length == 0;
    }

    // ---- Windows Credential Manager ------------------------------------------------------------------

    private sealed class Win32Exception(string message) : Exception(message);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
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

    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;

#pragma warning disable SYSLIB1054 // DllImport: the credential struct holds strings, and LibraryImport would need unsafe code
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
#pragma warning restore SYSLIB1054

    private static string? WindowsRead(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var pointer))
            return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return credential.CredentialBlobSize == 0 ? null : Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    private static void WindowsWrite(string target, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            CredDelete(target, CredTypeGeneric, 0);
            return;
        }

        var bytes = Encoding.Unicode.GetBytes(value);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception($"Credential Manager did not store the key (error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }
}
