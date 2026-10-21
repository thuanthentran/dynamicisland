namespace DynamicIsland.Platform;

/// <summary>Detects whether the process runs with package identity (sparse package registered and launched from it).</summary>
internal static class PackageIdentity
{
    public static unsafe bool IsPresent
    {
        get
        {
            uint length = 0;
            return NativeMethods.GetCurrentPackageFullName(ref length, null) != NativeMethods.APPMODEL_ERROR_NO_PACKAGE;
        }
    }
}
