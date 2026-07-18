using PCL.N.Plugin;

namespace Cn.Pcln.Terracotta.Services;

public sealed class TerracottaLocalizer(IPluginLocalizationService localization)
{
    public string Get(string key, string fallback) => localization.GetString(key, fallback);

    public string Format(string key, string fallback, params object?[] arguments) =>
        localization.FormatString(key, fallback, arguments);
}
