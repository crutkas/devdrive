using System.Globalization;
using System.Xml.Linq;

namespace DevDriveCore.Tests;

/// <summary>
/// Guards the one colour in the app that says how much space you have left.
/// </summary>
/// <remarks>
/// <para>
/// Free space was drawn with <c>SmDividerBrush</c>, which measured <b>1.12:1</b> against the card
/// header it sits on. WCAG 1.4.11 requires <b>3:1</b> for a graphical object that carries meaning.
/// The single quantity this whole application exists to increase was the one band on the bar that
/// could not be seen.
/// </para>
/// <para>
/// A divider brush is <i>designed</i> to be barely there, so it was not a careless value — it was
/// the wrong role. Nothing in C# can catch that, which is why this reads the shipped dictionary.
/// </para>
/// </remarks>
[TestClass]
public sealed class ThemeContrastTests
{
    /// <summary>WCAG 1.4.11 minimum for graphical objects and user-interface components.</summary>
    private const double MinimumGraphicalContrast = 3.0d;

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [TestMethod]
    public void TheFreeSpaceChannelIsVisibleOnEverySurfaceItSitsOn()
    {
        foreach (string theme in new[] { "Default", "Light" })
        {
            string free = Colour(theme, "SmFreeTrackBrush");

            foreach (string surfaceKey in new[] { "SmCardBrush", "SmCardHeaderBrush" })
            {
                double ratio = Contrast(free, Colour(theme, surfaceKey));

                Assert.IsGreaterThanOrEqualTo(
                    MinimumGraphicalContrast,
                    ratio,
                    $"{theme}/SmFreeTrackBrush is {ratio:0.00}:1 against {surfaceKey}. " +
                    $"WCAG 1.4.11 requires {MinimumGraphicalContrast:0.0}:1 for a band that carries meaning.");
            }
        }
    }

    /// <summary>
    /// The counter-test. Without it the assertion above could pass because the arithmetic is
    /// generous rather than because the colour is visible.
    /// </summary>
    /// <remarks>
    /// This pins the original defect as a measurement rather than an anecdote: the divider brush
    /// really does fail, by a wide margin, on the surface the free channel is drawn on. If this
    /// ever starts passing, the maths has stopped being able to tell invisible from visible and
    /// the test above is worthless.
    /// </remarks>
    [TestMethod]
    public void TheDividerBrushWouldStillFailIfItWereUsedForFreeSpace()
    {
        double ratio = Contrast(Colour("Default", "SmDividerBrush"), Colour("Default", "SmCardHeaderBrush"));

        Assert.IsLessThan(
            1.5d,
            ratio,
            $"SmDividerBrush measures {ratio:0.00}:1 on the card header. It was 1.12:1 when the " +
            "free-space channel used it, which is the defect this suite exists to hold shut.");
    }

    /// <summary>
    /// High contrast is deliberately excluded above because its colours are system tokens with no
    /// literal value to measure. What can be checked is that free space does not resolve to the
    /// same token as the band beside it.
    /// </summary>
    [TestMethod]
    public void HighContrastDoesNotPaintFreeSpaceTheSameAsTheUsedBand()
    {
        string free = Colour("HighContrast", "SmFreeTrackBrush");
        string divider = Colour("HighContrast", "SmDividerBrush");

        Assert.AreNotEqual(free, divider, StringComparer.OrdinalIgnoreCase);
        StringAssert.Contains(free, "GrayText");
    }

    // ---- reading the dictionary -------------------------------------------------------------

    private static string Colour(string themeName, string key)
    {
        XElement theme = ThemeDictionary(themeName);

        XElement? brush = theme
            .Descendants()
            .FirstOrDefault(e =>
                e.Name.LocalName == "SolidColorBrush" &&
                (string?)e.Attribute(Xaml + "Key") == key);

        Assert.IsNotNull(brush, $"{themeName} has no SolidColorBrush named {key}.");

        string? colour = (string?)brush.Attribute("Color");
        Assert.IsFalse(string.IsNullOrWhiteSpace(colour), $"{themeName}/{key} has no Color.");

        return colour!;
    }

    private static XElement ThemeDictionary(string themeName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Themes", "StorageManagerTheme.xaml");
        Assert.IsTrue(File.Exists(path), $"The theme dictionary was not copied to the test output: {path}");

        XElement root = XDocument.Load(path).Root!;

        XElement? themes = root
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "ResourceDictionary.ThemeDictionaries");

        Assert.IsNotNull(themes, "StorageManagerTheme.xaml has no ThemeDictionaries.");

        XElement? dictionary = themes
            .Elements()
            .FirstOrDefault(e => (string?)e.Attribute(Xaml + "Key") == themeName);

        Assert.IsNotNull(dictionary, $"StorageManagerTheme.xaml has no {themeName} theme dictionary.");

        return dictionary;
    }

    // ---- WCAG 2.x relative luminance ---------------------------------------------------------

    private static double Contrast(string first, string second)
    {
        double a = RelativeLuminance(first);
        double b = RelativeLuminance(second);

        (double lighter, double darker) = a >= b ? (a, b) : (b, a);

        return (lighter + 0.05d) / (darker + 0.05d);
    }

    private static double RelativeLuminance(string hex)
    {
        string value = hex.TrimStart('#');

        // #AARRGGBB is accepted so a token that gains an alpha channel does not silently measure
        // the alpha byte as red.
        if (value.Length == 8)
        {
            value = value[2..];
        }

        Assert.AreEqual(6, value.Length, $"{hex} is not a literal colour this test can measure.");

        double r = Channel(value[0..2]);
        double g = Channel(value[2..4]);
        double b = Channel(value[4..6]);

        return (0.2126d * r) + (0.7152d * g) + (0.0722d * b);
    }

    private static double Channel(string pair)
    {
        double c = int.Parse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;

        return c <= 0.04045d ? c / 12.92d : Math.Pow((c + 0.055d) / 1.055d, 2.4d);
    }
}
