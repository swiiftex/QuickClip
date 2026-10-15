using System.Reflection;
using System.Runtime.InteropServices;

namespace QuickClip.Shell;

/// <summary>
/// The "clip saved" sound: a camera shutter (Assets\clip-saved.wav), rather than a Windows system sound, which is
/// what its message boxes and errors make.
/// </summary>
internal static class ClipSound
{
    // Pinned for good: PlaySound reads the WAV from memory while it plays in the background.
    private static readonly Lazy<GCHandle> Wav = new(() => GCHandle.Alloc(Load(), GCHandleType.Pinned));

    public static void Play() => PlaySound(Wav.Value.AddrOfPinnedObject(), IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);

    /// <summary>The sound as a WAV file.</summary>
    internal static byte[] Load()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("QuickClip.clip-saved.wav")!;
        using var wav = new MemoryStream();
        resource.CopyTo(wav);
        return wav.ToArray();
    }

    private const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_MEMORY = 0x4;

    [DllImport("winmm.dll")] private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
}
