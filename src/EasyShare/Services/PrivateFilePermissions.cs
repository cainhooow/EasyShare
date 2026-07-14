using System.Security.AccessControl;
using System.Security.Principal;

namespace EasyShare.Services;

/// <summary>
/// Applies a current-user-only ACL when Windows permits it. Storage operations do not
/// fail if an administrator, filesystem, or test environment does not support ACLs.
/// </summary>
internal static class PrivateFilePermissions
{
    public static bool TryHardenDirectory(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var user = WindowsIdentity.GetCurrent().User;
            if (user is null)
            {
                return false;
            }

            var directory = new DirectoryInfo(path);
            var security = directory.GetAccessControl(AccessControlSections.Access);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            PurgeExistingRules(security);
            security.SetOwner(user);
            security.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            directory.SetAccessControl(security);
            return true;
        }
        catch (SystemException)
        {
            return false;
        }
    }

    public static bool TryHardenFile(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var user = WindowsIdentity.GetCurrent().User;
            if (user is null)
            {
                return false;
            }

            var file = new FileInfo(path);
            var security = file.GetAccessControl(AccessControlSections.Access);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            PurgeExistingRules(security);
            security.SetOwner(user);
            security.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            file.SetAccessControl(security);
            return true;
        }
        catch (SystemException)
        {
            return false;
        }
    }

    private static void PurgeExistingRules(FileSystemSecurity security)
    {
        var identities = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Select(rule => rule.IdentityReference)
            .OfType<SecurityIdentifier>()
            .Distinct()
            .ToArray();
        foreach (var identity in identities)
        {
            security.PurgeAccessRules(identity);
        }
    }
}
