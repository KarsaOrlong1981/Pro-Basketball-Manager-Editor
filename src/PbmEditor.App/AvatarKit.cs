using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PbmEditor.Core;

namespace PbmEditor.App;

/// <summary>
/// Re-creates the game's 2D player avatar (Humains_SVC.GetAvatar in the game code) from the layer images in
/// PBM2025_Data\images\decor\faces: each layer is multiplied by a tint colour and alpha-composited in a fixed order.
/// The game shows this avatar for players without a photo file.
/// </summary>
public static class AvatarKit
{
    const int Size = 350;

    static readonly Dictionary<string, float[]?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string? FacesDir
    {
        get
        {
            var game = ModCreator.FindGameDir();
            if (game == null) return null;
            var d = Path.Combine(game, "PBM2025_Data", "images", "decor", "faces");
            return Directory.Exists(d) ? d : null;
        }
    }

    // colour tables copied from Couleurs_Peau_CONSTANTES / Coiffures_CONSTANTES in the game code
    static (float R, float G, float B) Skin(int t) => t switch
    {
        0 => (242 / 255f, 214 / 255f, 174 / 255f),
        10 => (234 / 255f, 199 / 255f, 148 / 255f),
        20 => (190 / 255f, 157 / 255f, 107 / 255f),
        30 => (0.5607f, 0.4431f, 0.2745f),
        40 => (0.3882f, 0.2784f, 0.1176f),
        50 => (0.2784f, 0.2235f, 0.1451f),
        60 => (0.9882f, 0.7803f, 0.4784f),
        _ => (0f, 0f, 0f),
    };

    static (float R, float G, float B) SkinShadow(int t) => t switch
    {
        0 or 10 or 60 => (0.6784f, 0.5098f, 0.27058f),
        20 => (0.52156f, 0.396f, 0.2039f),
        30 => (0.3882f, 0.2523f, 0.1333f),
        40 => (0.247f, 0.1647f, 0.0431f),
        50 => (0.1529f, 0.1098f, 0.0431f),
        _ => (1f, 1f, 1f),
    };

    static (float R, float G, float B) Hair(int t) => t switch
    {
        1 => (0.2235f, 0.1765f, 0.1098f),
        2 => (0.498f, 0.3608f, 0.1608f),
        3 => (0.8157f, 0.6509f, 0f),
        4 => (0.6745f, 0.3647f, 0f),
        5 => (0.4549f, 0.4549f, 0.4549f),
        6 => (0.9137f, 0.8667f, 0.8039f),
        _ => (0.031372f, 0.031372f, 0.035294f),
    };

    static (float R, float G, float B) FromArgb(string? text)
    {
        var (_, r, g, b) = JerseyKit.ParseArgb(text);
        return (r / 255f, g / 255f, b / 255f);
    }

    static float[]? Load(string path)
    {
        if (Cache.TryGetValue(path, out var cached)) return cached;
        float[]? px = null;
        if (File.Exists(path))
        {
            try
            {
                var src = new FormatConvertedBitmap(ImageTools.Decode(File.ReadAllBytes(path)), PixelFormats.Bgra32, null, 0);
                if (src.PixelWidth == Size && src.PixelHeight == Size)
                {
                    var bytes = new byte[Size * Size * 4];
                    src.CopyPixels(bytes, Size * 4, 0);
                    px = new float[bytes.Length];
                    for (var i = 0; i < bytes.Length; i += 4)
                    {
                        px[i] = bytes[i + 2] / 255f; px[i + 1] = bytes[i + 1] / 255f; px[i + 2] = bytes[i] / 255f; px[i + 3] = bytes[i + 3] / 255f;
                    }
                }
            }
            catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException) { px = null; }
        }
        return Cache[path] = px;
    }

    // same maths as ConstruireImageAPartirDuneColorMatrix, ColorMergeType.Over
    static void Over(float[] dst, float[]? layer, (float R, float G, float B) tint)
    {
        if (layer == null) return;
        for (var i = 0; i < dst.Length; i += 4)
        {
            var a = layer[i + 3];
            if (a <= 0f) continue;
            float r = layer[i] * tint.R, g = layer[i + 1] * tint.G, b = layer[i + 2] * tint.B;
            var outA = 1f - (1f - a) * (1f - dst[i + 3]);
            var k1 = a / outA;
            var k2 = dst[i + 3] * (1f - a) / outA;
            dst[i] = r * k1 + dst[i] * k2;
            dst[i + 1] = g * k1 + dst[i + 1] * k2;
            dst[i + 2] = b * k1 + dst[i + 2] * k2;
            dst[i + 3] = outA;
        }
    }

    // 3D match view: hairstyle -> hair model(s), copied from Human3DController.SetHaircut in the game code.
    // Styles missing here have no 3D hair model (bare head).
    static readonly Dictionary<int, string> Hair3D = new()
    {
        [2] = "afro", [3] = "small mohawk", [4] = "messy", [6] = "afro", [7] = "messy long", [8] = "dreads", [9] = "tied+short",
        [10] = "long", [11] = "short", [12] = "short", [13] = "iroquois", [14] = "half long", [17] = "afro", [18] = "afro",
        [100] = "ponytail+short", [101] = "tied+short", [102] = "dreads", [103] = "half long", [104] = "tied+short",
        [105] = "half long", [106] = "half long", [107] = "long", [108] = "afro", [109] = "afro",
    };

    static bool Same(float[] a, float[] b)
    {
        for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>Explains why a hairstyle may look like another one: the game itself reuses pictures and models.</summary>
    public static string HairNote(bool female, int style, int beard)
    {
        var notes = new List<string>();
        var root = FacesDir;
        if (root != null)
        {
            var dir = Path.Combine(root, female ? "women" : "men");
            var mine = Load(Path.Combine(dir, $"hair-{style}.png"));
            if (mine == null) notes.Add(Loc.T("2D avatar: the game has no picture for this style."));
            else if (mine.Where((v, i) => i % 4 == 3).All(a => a == 0f)) notes.Add(Loc.T("2D avatar: this style draws no hair."));
            else
            {
                var other = Enumerable.Range(0, 120).Where(n => n < style && Load(Path.Combine(dir, $"hair-{n}.png")) is { } o && Same(o, mine)).Cast<int?>().FirstOrDefault();
                if (other != null) notes.Add(Loc.F("2D avatar: same picture as style {0}.", other));
            }
        }
        if (!Hair3D.TryGetValue(style, out var model)) notes.Add(Loc.T("3D match view: no hair model for this style (bare head)."));
        else
        {
            var twin = Hair3D.Where(kv => kv.Key < style && kv.Value == model && (kv.Key >= 100) == (style >= 100)).Select(kv => (int?)kv.Key).FirstOrDefault();
            if (twin != null) notes.Add(Loc.F("3D match view: same hair model as style {0}.", twin));
        }
        if (beard is >= 1 and <= 3) notes.Add(Loc.T("3D match view: beards 1-3 are not drawn."));
        return string.Join("\n", notes);
    }

    /// <param name="homeArgb">team home colour (signed ARGB text), shirt colour 1; null/0-team = white</param>
    public static BitmapSource? Render(bool female, string? homeArgb, string? awayArgb, int skin, int hairColor, int beard, int hairstyle,
        bool staff = false, bool hasTeam = true)
    {
        var root = FacesDir;
        if (root == null) return null;
        var dir = Path.Combine(root, female ? "women" : "men");
        var white = (1f, 1f, 1f);
        var hair = Hair(hairColor);
        var dst = new float[Size * Size * 4];
        for (var i = 0; i < dst.Length; i += 4) { dst[i] = dst[i + 1] = dst[i + 2] = 1f; dst[i + 3] = 0f; }

        Over(dst, Load(Path.Combine(dir, "circle-bg.png")), (0.5725f, 0.5725f, 0.5725f));
        Over(dst, Load(Path.Combine(dir, "skin.png")), Skin(skin));
        Over(dst, Load(Path.Combine(dir, "eyes_white.png")), white);
        Over(dst, Load(Path.Combine(dir, "eyes.png")), white);
        if (beard != 0) Over(dst, Load(Path.Combine(dir, $"beard-{beard}.png")), hair);
        Over(dst, Load(Path.Combine(dir, "eyebrows.png")), hair);
        Over(dst, Load(Path.Combine(dir, "hairline.png")), white);
        Over(dst, Load(Path.Combine(dir, $"hair-{hairstyle}.png")), hair);
        if (!staff)
        {
            Over(dst, Load(Path.Combine(dir, "shirt-color1.png")), homeArgb == null ? white : FromArgb(homeArgb));
            Over(dst, Load(Path.Combine(dir, "shirt-color2.png")), awayArgb == null ? white : FromArgb(awayArgb));
        }
        else
        {
            // coaches and staff wear a jacket; the tie takes a dark team colour (game: Couleurs.GetGrisFromColor < 200)
            static float Grey((float R, float G, float B) c) => 255f * (c.R + c.G + c.B) / 3f;
            var tie = (0f, 0f, 0f);
            if (hasTeam && homeArgb != null && awayArgb != null)
            {
                if (Grey(FromArgb(homeArgb)) < 200f) tie = FromArgb(homeArgb);
                else if (Grey(FromArgb(awayArgb)) < 200f) tie = FromArgb(awayArgb);
            }
            Over(dst, Load(Path.Combine(dir, "jacket_undershirt.png")), (0.6706f, 0.8431f, 0.9137f));
            Over(dst, Load(Path.Combine(dir, "jacket_tie.png")), tie);
            Over(dst, Load(Path.Combine(dir, "jacket.png")), (0f, 0f, 0f));
            Over(dst, Load(Path.Combine(dir, "jacket_shirt_shadow.png")), (0.30196f, 0.4157f, 0.4627f));
        }
        Over(dst, Load(Path.Combine(dir, "shadow.png")), SkinShadow(skin));
        Over(dst, Load(Path.Combine(dir, "circle-top.png")), white);

        var bytes = new byte[dst.Length];
        for (var i = 0; i < dst.Length; i += 4)
        {
            bytes[i] = (byte)Math.Clamp(dst[i + 2] * 255f + 0.5f, 0, 255);
            bytes[i + 1] = (byte)Math.Clamp(dst[i + 1] * 255f + 0.5f, 0, 255);
            bytes[i + 2] = (byte)Math.Clamp(dst[i] * 255f + 0.5f, 0, 255);
            bytes[i + 3] = (byte)Math.Clamp(dst[i + 3] * 255f + 0.5f, 0, 255);
        }
        var bmp = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgra32, null, bytes, Size * 4);
        bmp.Freeze();
        return bmp;
    }
}
