using System.Globalization;
using System.Resources;

namespace SnapLine.Services;

public static class Localization
{
    private static readonly ResourceManager Resources = new("SnapLine.Resources.Strings", typeof(Localization).Assembly);
    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;
}
