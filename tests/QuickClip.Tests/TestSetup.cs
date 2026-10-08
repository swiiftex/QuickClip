using System.Runtime.CompilerServices;

namespace QuickClip.Tests;

internal static class TestSetup
{
    /// <summary>Tests read the user's real settings but never write them (their QuickClip may be running).</summary>
    [ModuleInitializer]
    internal static void KeepSettingsReadOnly() => AppSettings.SaveDisabled = true;
}
