// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace SimpleScanner.RadioReference;

/// <summary>
/// The RadioReference application key, which tells RadioReference which program is asking (they
/// use it to count each program's use). It is not in the source: a build takes it from the
/// environment variable RADIOREFERENCE_KEY or the git-ignored file radioreference.key at the top
/// of the repository, and puts it in the program (see SimpleScanner.csproj). The release packages
/// are built with it. When the program is run, RADIOREFERENCE_KEY in the environment is used in
/// place of the one built in.
/// </summary>
public static class RrKey
{
    /// <summary>The key to use: the environment's, else the one built in; null when there is neither.</summary>
    public static string? Find() => Clean(Environment.GetEnvironmentVariable("RADIOREFERENCE_KEY")) ?? BuiltIn;

    /// <summary>The key this build carries; null when it was built without one.</summary>
    public static string? BuiltIn { get; } = Clean(typeof(RrKey).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "RadioReferenceKey")?.Value);

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>Where a RadioReference password is kept between runs, by user name.</summary>
public interface IRrLogins
{
    /// <summary>A short name for where it is kept, for the window's words ("the macOS keychain").</summary>
    string Where { get; }
    string? Read(string username);
    /// <summary>Keep the password, in place of any kept before for this account. False when it could not be kept.</summary>
    bool Keep(string username, string password);
    void Forget(string username);
}

public static class RrLogins
{
    /// <summary>This system's own place: the macOS login keychain, the Windows Credential Manager, else a file readable only by this user.</summary>
    public static IRrLogins ForThisComputer(string folder) =>
        OperatingSystem.IsMacOS() ? new KeychainLogins()
        : OperatingSystem.IsWindows() ? new WindowsLogins()
        : new FileLogins(folder);

    internal const string Service = "SimpleScanner RadioReference";
}

/// <summary>Kept only while the program runs: for tests.</summary>
public sealed class MemoryLogins : IRrLogins
{
    private readonly Dictionary<string, string> _kept = [];
    public string Where => "memory";
    public string? Read(string username) => _kept.GetValueOrDefault(username);
    public bool Keep(string username, string password)
    {
        if (username.Length == 0) return false;
        _kept[username] = password;
        return true;
    }
    public void Forget(string username) => _kept.Remove(username);
}

/// <summary>
/// Linux and the like: a file radioreference.login beside the settings, readable only by this
/// user (mode 600), holding the user name and the password. Not encrypted.
/// </summary>
public sealed class FileLogins(string folder) : IRrLogins
{
    private string File => Path.Combine(folder, "radioreference.login");
    public string Where => "a file readable only by you";

    public string? Read(string username)
    {
        try
        {
            if (username.Length == 0 || !System.IO.File.Exists(File)) return null;
            var lines = System.IO.File.ReadAllText(File).Split('\n', 2);
            return lines.Length == 2 && lines[0] == username ? lines[1].TrimEnd('\n') : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public bool Keep(string username, string password)
    {
        if (username.Length == 0 || username.Contains('\n')) return false;
        try
        {
            Directory.CreateDirectory(folder);
            // Made readable by this user only before anything is written to it.
            using (var f = new FileStream(File, FileMode.Create, FileAccess.Write))
            {
                if (!OperatingSystem.IsWindows()) System.IO.File.SetUnixFileMode(File, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                f.Write(Encoding.UTF8.GetBytes(username + "\n" + password + "\n"));
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public void Forget(string username)
    {
        try
        {
            if (Read(username) is not null) System.IO.File.Delete(File);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>The macOS login keychain, as a generic password of the service "SimpleScanner RadioReference".</summary>
public sealed class KeychainLogins(string service = RrLogins.Service) : IRrLogins
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public string Where => "the macOS keychain";

    public string? Read(string username)
    {
        if (!OperatingSystem.IsMacOS() || username.Length == 0) return null;
        try
        {
            byte[] s = Encoding.UTF8.GetBytes(service), a = Encoding.UTF8.GetBytes(username);
            if (SecKeychainFindGenericPassword(0, (uint)s.Length, s, (uint)a.Length, a, out uint length, out nint data, 0) != 0) return null;
            try
            {
                var bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, (int)length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally { SecKeychainItemFreeContent(0, data); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    public bool Keep(string username, string password)
    {
        if (!OperatingSystem.IsMacOS() || username.Length == 0) return false;
        Forget(username);
        try
        {
            byte[] s = Encoding.UTF8.GetBytes(service), a = Encoding.UTF8.GetBytes(username), p = Encoding.UTF8.GetBytes(password);
            return SecKeychainAddGenericPassword(0, (uint)s.Length, s, (uint)a.Length, a, (uint)p.Length, p, 0) == 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    public void Forget(string username)
    {
        if (!OperatingSystem.IsMacOS() || username.Length == 0) return;
        try
        {
            byte[] s = Encoding.UTF8.GetBytes(service), a = Encoding.UTF8.GetBytes(username);
            if (SecKeychainFindGenericPassword(0, (uint)s.Length, s, (uint)a.Length, a, 0, 0, out nint item) != 0 || item == 0) return;
            SecKeychainItemDelete(item);
            CFRelease(item);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    }

    [DllImport(Security)]
    private static extern int SecKeychainAddGenericPassword(nint keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, uint passwordLength, byte[] password, nint itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainFindGenericPassword(nint keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, out uint passwordLength, out nint passwordData, nint itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainFindGenericPassword(nint keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, nint passwordLength, nint passwordData, out nint itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainItemFreeContent(nint attributes, nint data);

    [DllImport(Security)]
    private static extern int SecKeychainItemDelete(nint itemRef);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint obj);
}

/// <summary>
/// The Windows Credential Manager (Control Panel › Credential Manager › Windows Credentials), as
/// a generic credential named "SimpleScanner RadioReference:&lt;user name&gt;", kept for this user on
/// this computer.
/// </summary>
public sealed class WindowsLogins(string service = RrLogins.Service) : IRrLogins
{
    private const int GenericType = 1;          // CRED_TYPE_GENERIC
    private const int PersistLocalMachine = 2;  // CRED_PERSIST_LOCAL_MACHINE: this user, this computer

    public string Where => "the Windows Credential Manager";

    private string Target(string username) => $"{service}:{username}";

    public string? Read(string username)
    {
        if (!OperatingSystem.IsWindows() || username.Length == 0) return null;
        try
        {
            if (!CredReadW(Target(username), GenericType, 0, out nint found)) return null;
            try
            {
                var cred = Marshal.PtrToStructure<Credential>(found);
                if (cred.CredentialBlob == 0 || cred.CredentialBlobSize == 0) return "";
                return Marshal.PtrToStringUni(cred.CredentialBlob, cred.CredentialBlobSize / 2);
            }
            finally { CredFree(found); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    public bool Keep(string username, string password)
    {
        if (!OperatingSystem.IsWindows() || username.Length == 0) return false;
        nint blob = Marshal.StringToHGlobalUni(password);
        try
        {
            var cred = new Credential
            {
                Type = GenericType,
                TargetName = Target(username),
                UserName = username,
                CredentialBlob = blob,
                CredentialBlobSize = password.Length * 2,
                Persist = PersistLocalMachine,
            };
            return CredWriteW(ref cred, 0);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
        finally
        {
            // The password is wiped from this copy before it is let go.
            for (int i = 0; i < password.Length * 2; i++) Marshal.WriteByte(blob, i, 0);
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Forget(string username)
    {
        if (!OperatingSystem.IsWindows() || username.Length == 0) return;
        try { CredDeleteW(Target(username), GenericType, 0); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public nint CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, int type, int flags, out nint credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref Credential credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint buffer);
}
