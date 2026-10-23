using System.Globalization;

namespace Flip;

internal static class UiText
{
    internal static bool IsItalian(AppPreferences preferences) => preferences.Language switch
    {
        "it" => true,
        "en" => false,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("it", StringComparison.OrdinalIgnoreCase)
    };

    internal static string Get(AppPreferences preferences, string italian, string english) => IsItalian(preferences) ? italian : english;
}
