using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.AppsStartup;

/// <summary>API tipada, sem shell, sem mudança de UI global e sem remoção de contexto ambíguo.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMsiUninstallApi : IMsiUninstallApi
{
    public bool IsSynthetic => false;
    private static void Guard(string productCode)
    {
        if (!AppsOperationalExecutionGate.PermitsExecution) throw new InvalidOperationException("gate");
        if (!Guid.TryParseExact(productCode, "B", out _)) throw new ArgumentException("invalid-product-code");
    }

    public MsiInstallation? Inspect(string productCode)
    {
        Guard(productCode);
        var product = new StringBuilder(39);
        var sid = new StringBuilder(256);
        uint sidLength = 256;
        var status = MsiEnumProductsExW(productCode, null, 7, 0, product, out var context, sid, ref sidLength);
        if (status == 259) return null; // ERROR_NO_MORE_ITEMS; access errors are never absence.
        if (status != 0) throw new Win32Exception((int)status);
        var secondProduct = new StringBuilder(39);
        sidLength = 256;
        var second = MsiEnumProductsExW(productCode, null, 7, 1, secondProduct, out _, sid, ref sidLength);
        if (second is not (0 or 259)) throw new Win32Exception((int)second);
        var unambiguous = second == 259 && context == 2;
        var name = Property(productCode, context, "InstalledProductName");
        var version = Property(productCode, context, "VersionString");
        var publisher = Property(productCode, context, "Publisher");
        return new(product.ToString(), context == 4 ? AppsScope.Machine : AppsScope.CurrentUser,
            context == 2 ? "UserUnmanaged" : context == 1 ? "UserManaged" : "Machine",
            name, version, publisher, Property(productCode, context, "State") == "5",
            unambiguous && RegistryAllows(productCode, name, version, publisher), unambiguous);
    }

    private static bool RegistryAllows(string code, string name, string version, string publisher)
    {
        var keys = new WindowsAppsRegistrySource().ReadSubKeys(AppsScope.CurrentUser, UninstallRegistryApplicationProvider.UninstallPath);
        var matching = keys?.Where(k => Guid.TryParse(k.KeyName, out var id) && id == Guid.Parse(code)).ToArray();
        if (matching is not { Length: 1 }) return false;
        var values = matching[0].Values;
        return values.TryGetValue("WindowsInstaller", out var installer) && installer is 1 &&
            (!values.TryGetValue("SystemComponent", out var system) || system is 0) &&
            !values.ContainsKey("ParentKeyName") && values.GetValueOrDefault("DisplayName") as string == name &&
            values.GetValueOrDefault("DisplayVersion") as string == version && values.GetValueOrDefault("Publisher") as string == publisher;
    }

    public uint Uninstall(string productCode)
    {
        Guard(productCode);
        if (Inspect(productCode) is not { Installed: true, Eligible: true, Unambiguous: true, Context: "UserUnmanaged" })
            throw new InvalidOperationException("installation-not-proven");
        // INSTALLSTATE_ABSENT=2; install level is ignored for this state. Never set global Installer UI.
        return MsiConfigureProductExW(productCode, 0, 2, "REBOOT=ReallySuppress");
    }

    private static string Property(string productCode, uint context, string name)
    {
        uint size = 0;
        var status = MsiGetProductInfoExW(productCode, null, context, name, null, ref size);
        if (status is not (0 or 234) || size > 32767) throw new Win32Exception((int)status);
        var value = new StringBuilder(checked((int)size + 1));
        size++;
        status = MsiGetProductInfoExW(productCode, null, context, name, value, ref size);
        if (status != 0) throw new Win32Exception((int)status);
        return value.ToString();
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiConfigureProductExW(string productCode, int installLevel, int installState, string commandLine);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiEnumProductsExW(string productCode, string? userSid, uint context, uint index,
        StringBuilder installedCode, out uint installedContext, StringBuilder sid, ref uint sidLength);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiGetProductInfoExW(string productCode, string? userSid, uint context,
        string property, StringBuilder? value, ref uint valueLength);
}
