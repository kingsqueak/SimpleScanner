using System.Runtime.InteropServices;
using System.Text;

namespace SimpleScanner.RadioReference;

/// <summary>
/// The RadioReference application key. None is in the source or in a build: whoever has a key
/// enters it in the RadioReference window, which keeps it in a file radioreference.key beside
/// the settings. It can also be given in the environment variable RADIOREFERENCE_KEY, which
/// is used in place of the one kept.
/// </summary>
public static class RrKey
{
    private const string FileName = "radioreference.key";

    public static string? Find() => Find(AppFiles.Folder);

    /// <summary>The key to use: the environment's, else the user's own in <paramref name="folder"/>; null when there is none.</summary>
    public static string? Find(string folder) =>
        Clean(Environment.GetEnvironmentVariable("RADIOREFERENCE_KEY")) ?? Own(folder);

    /// <summary>The key the user entered, kept in <paramref name="folder"/>; null when there is none.</summary>
    public static string? Own(string folder)
    {
        try
        {
            string beside = Path.Combine(folder, FileName);
            return File.Exists(beside) ? Clean(File.ReadAllText(beside)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Keep the user's own key in <paramref name="folder"/>, readable by this user only; an empty
    /// key removes the one kept. False when the file could not be written or removed.
    /// </summary>
    public static bool Keep(string? key, string folder)
    {
        try
        {
            string beside = Path.Combine(folder, FileName);
            if (Clean(key) is not { } text)
            {
                File.Delete(beside);
                return true;
            }
            Directory.CreateDirectory(folder);
            File.WriteAllText(beside, text + "\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(beside, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>
/// Keeps the user's RadioReference password between runs, in the macOS login keychain. On
/// other systems nothing is kept yet and the password is asked for at each run.
/// </summary>
public static class RrPassword
{
    private const string Service = "SimpleScanner RadioReference";
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public static bool CanKeep => OperatingSystem.IsMacOS();

    public static string? Read(string username, string service = Service)
    {
        if (!CanKeep || username.Length == 0) return null;
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

    /// <summary>Keep the password, in place of any kept before for this account. False when it could not be kept.</summary>
    public static bool Keep(string username, string password, string service = Service)
    {
        if (!CanKeep || username.Length == 0) return false;
        Forget(username, service);
        try
        {
            byte[] s = Encoding.UTF8.GetBytes(service), a = Encoding.UTF8.GetBytes(username), p = Encoding.UTF8.GetBytes(password);
            return SecKeychainAddGenericPassword(0, (uint)s.Length, s, (uint)a.Length, a, (uint)p.Length, p, 0) == 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    public static void Forget(string username, string service = Service)
    {
        if (!CanKeep || username.Length == 0) return;
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
