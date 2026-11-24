using MudBlazor;

namespace BorgStudio.Server.Web.Theming;

/// <summary>
/// The colors of the BorgStudio app (dark), plus a light variant with the same accents,
/// darkened where they would be too pale on white.
/// </summary>
public static class BorgStudioTheme
{
    // BorgStudio app palette (src/BorgStudio.App/App.axaml in the app repository).
    private const string Green = "#01FF95";
    private const string Pink = "#FF53A0";
    private const string Orange = "#FF9800";
    private const string Red = "#FF5252";
    private const string Background = "#141414";
    private const string Surface = "#1F1F1F";
    private const string Bar = "#181818";
    private const string Line = "#2D2D2D";
    private const string Text = "#EAEAEA";
    private const string TextSecondary = "#9C9C9C";

    private static readonly string[] FontFamily =
        ["system-ui", "-apple-system", "Segoe UI", "Roboto", "Helvetica Neue", "Arial", "sans-serif"];

    public static MudTheme Theme { get; } = new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = Green,
            PrimaryContrastText = Background,
            Secondary = Pink,
            SecondaryContrastText = Background,
            Tertiary = Orange,
            Info = "#3987E5",
            Success = Green,
            SuccessContrastText = Background,
            Warning = Orange,
            WarningContrastText = Background,
            Error = Red,
            Background = Background,
            BackgroundGray = Bar,
            Surface = Surface,
            AppbarBackground = Bar,
            AppbarText = Text,
            DrawerBackground = Bar,
            DrawerText = Text,
            DrawerIcon = TextSecondary,
            TextPrimary = Text,
            TextSecondary = TextSecondary,
            ActionDefault = TextSecondary,
            LinesDefault = Line,
            LinesInputs = "#4A4A4A",
            TableLines = Line,
            Divider = Line,
            DividerLight = Line,
        },
        PaletteLight = new PaletteLight
        {
            Primary = "#00875A",
            PrimaryContrastText = "#FFFFFF",
            Secondary = "#C2185B",
            SecondaryContrastText = "#FFFFFF",
            Tertiary = "#B26A00",
            Info = "#1F6FD1",
            Success = "#00875A",
            SuccessContrastText = "#FFFFFF",
            Warning = "#B26A00",
            WarningContrastText = "#FFFFFF",
            Error = "#C62828",
            Background = "#F5F5F5",
            BackgroundGray = "#EDEDED",
            Surface = "#FFFFFF",
            AppbarBackground = "#FFFFFF",
            AppbarText = "#1A1A1A",
            DrawerBackground = "#FFFFFF",
            DrawerText = "#1A1A1A",
            DrawerIcon = "#5F5F5F",
            TextPrimary = "#1A1A1A",
            TextSecondary = "#5F5F5F",
            ActionDefault = "#5F5F5F",
            LinesDefault = "#E0E0E0",
            LinesInputs = "#BDBDBD",
            TableLines = "#E0E0E0",
            Divider = "#E0E0E0",
            DividerLight = "#EDEDED",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = FontFamily },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "6px",
        },
    };
}
