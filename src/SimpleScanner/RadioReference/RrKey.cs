using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace SimpleScanner.RadioReference;

/// <summary>
/// This program's RadioReference application key. It is not in the source: a build takes it
/// from the file radioreference.key at the top of the repository (which git ignores) when that
/// file is there. A copy built without it can be given one in the environment variable
/// RADIOREFERENCE_KEY, or in a file radioreference.key beside the settings.
/// </summary>
public static class RrKey
{
    public static string? Find()
    {
        if (Clean(Environment.GetEnvironmentVariable("RADIOREFERENCE_KEY")) is { } fromEnv) return fromEnv;
        try
        {
            string beside = Path.Combine(AppFiles.Folder, "radioreference.key");
            if (File.Exists(beside) && Clean(File.ReadAllText(beside)) is { } fromFile) return fromFile;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        using var built = Assembly.GetExecutingAssembly().GetManifestResourceStream("radioreference.key");
        if (built is null) return null;
        using var reader = new StreamReader(built);
        return Clean(reader.ReadToEnd());
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
