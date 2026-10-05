using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using QuickClip.Shell;

namespace QuickClip.Tests;

/// <summary>Keeps packaging/AppxManifest.xml consistent with the app.</summary>
public sealed class MenuPackageTests
{
    private static readonly XNamespace Foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Desktop5 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/5";

    private static XDocument LoadManifest()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "packaging", "AppxManifest.xml"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return XDocument.Load(Path.Combine(dir!.FullName, "packaging", "AppxManifest.xml"));
    }

    [Fact]
    public void Menu_package_covers_the_same_file_types_as_the_classic_entry()
    {
        var types = LoadManifest().Descendants(Desktop5 + "ItemType").Select(e => (string)e.Attribute("Type")!).ToList();
        Assert.Equal(ShellIntegration.Extensions.OrderBy(x => x), types.OrderBy(x => x));
    }

    [Fact]
    public void Package_family_name_matches_the_manifest_identity()
    {
        var identity = LoadManifest().Root!.Element(Foundation + "Identity")!;
        string family = $"{(string)identity.Attribute("Name")!}_{PublisherId((string)identity.Attribute("Publisher")!)}";
        Assert.Equal(ShellIntegration.MenuPackageFamily, family);
    }

    /// <summary>Windows' publisher ID: first 65 bits of SHA-256(UTF-16 publisher) in base32.</summary>
    private static string PublisherId(string publisher)
    {
        byte[] hash = SHA256.HashData(Encoding.Unicode.GetBytes(publisher));
        var bits = new StringBuilder();
        foreach (byte b in hash.AsSpan(0, 8)) bits.Append(Convert.ToString(b, 2).PadLeft(8, '0'));
        bits.Append('0');
        const string alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
        var id = new StringBuilder();
        for (int i = 0; i < 13; i++) id.Append(alphabet[Convert.ToInt32(bits.ToString(i * 5, 5), 2)]);
        return id.ToString();
    }
}
