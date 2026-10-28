using MudBlazor;

namespace Earmarked.Web.Services;

/// <summary>
/// The one MudBlazor theme, shared by the main and account layouts so the sign-in page matches the app.
/// Colours live here and nowhere else: pages use <see cref="Color"/> and app.css uses the <c>--mud-palette-*</c> variables.
/// </summary>
public static class AppTheme
{
    // System fonts only: the app is self-hosted and loads nothing from other sites.
    private static readonly string[] Sans = ["Inter", "ui-sans-serif", "system-ui", "-apple-system", "Segoe UI", "Roboto", "Helvetica Neue", "Arial", "sans-serif"];

    public static MudTheme Theme { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#6246EA",
            PrimaryDarken = "#4C32D1",
            PrimaryLighten = "#8B76F2",
            Secondary = "#E4572E",
            Tertiary = "#E8A23B",
            Info = "#2F80ED",
            Success = "#1F9D6B",
            Warning = "#D98A0B",
            Error = "#D64545",
            Background = "#F6F5FA",
            BackgroundGray = "#EEECF5",
            Surface = "#FFFFFF",
            AppbarBackground = "rgba(246,245,250,0.72)",
            AppbarText = "#1B1830",
            DrawerBackground = "#F6F5FA",
            DrawerText = "#3B3754",
            DrawerIcon = "#6B6785",
            TextPrimary = "#1B1830",
            TextSecondary = "#6B6785",
            ActionDefault = "#6B6785",
            LinesDefault = "#E6E3F0",
            LinesInputs = "#D3CFE2",
            TableLines = "#EEECF5",
            TableHover = "rgba(98,70,234,0.04)",
            Divider = "#E6E3F0",
            Skeleton = "rgba(98,70,234,0.08)",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#9B8BFF",
            PrimaryDarken = "#7F6CFA",
            PrimaryLighten = "#B9ADFF",
            PrimaryContrastText = "#120F24",
            Secondary = "#FF8061",
            Tertiary = "#F5B851",
            Info = "#5AA2FF",
            Success = "#3CCB8F",
            Warning = "#F2B33D",
            Error = "#FF6B6B",
            Background = "#0E0D14",
            BackgroundGray = "#15131D",
            Surface = "#18161F",
            AppbarBackground = "rgba(14,13,20,0.72)",
            AppbarText = "#ECEAF5",
            DrawerBackground = "#0E0D14",
            DrawerText = "#C9C5DB",
            DrawerIcon = "#8F8AA8",
            TextPrimary = "#ECEAF5",
            TextSecondary = "#9A95B3",
            ActionDefault = "#9A95B3",
            LinesDefault = "#2A2735",
            LinesInputs = "#3A3648",
            TableLines = "#24212E",
            TableHover = "rgba(155,139,255,0.06)",
            Divider = "#2A2735",
            Skeleton = "rgba(155,139,255,0.10)",
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "12px",
            DrawerWidthLeft = "248px",
            AppbarHeight = "64px",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = Sans, LetterSpacing = "-0.005em" },
            H4 = new H4Typography { FontFamily = Sans, FontWeight = "700", LetterSpacing = "-0.025em", FontSize = "2rem", LineHeight = "1.2" },
            H5 = new H5Typography { FontFamily = Sans, FontWeight = "700", LetterSpacing = "-0.02em" },
            H6 = new H6Typography { FontFamily = Sans, FontWeight = "650", LetterSpacing = "-0.015em", FontSize = "1.05rem" },
            Subtitle1 = new Subtitle1Typography { FontFamily = Sans, FontWeight = "500" },
            Subtitle2 = new Subtitle2Typography { FontFamily = Sans, FontWeight = "600" },
            Button = new ButtonTypography { FontFamily = Sans, FontWeight = "600", TextTransform = "none", LetterSpacing = "0" },
            Overline = new OverlineTypography { FontFamily = Sans, FontWeight = "700", LetterSpacing = "0.08em" },
        },
    };
}
