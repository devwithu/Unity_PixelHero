using PixelHero.Server.Js;

namespace PixelHero.Server.Common
{
    /// <summary>
    /// common/i18n t() for the English strings the server itself uses (an item plugin and log lines). The table is
    /// the flat "ns:KEY" -> text JSON that Tools/fixtures/gen_message_cases.mts generates from common/i18n
    /// (Assets/PixelHero/Localization/server.en.json). A missing key gives the key without its namespace, as i18next does.
    /// </summary>
    public static class I18n
    {
        static JsObject _table = new();

        public static void Load(string serverEnJsonPath)
        {
            if (File.Exists(serverEnJsonPath)) _table = JsData.ParseObject(File.ReadAllText(serverEnJsonPath));
        }

        public static string T(string key)
        {
            if (_table[key] is string text) return text;

            var colon = key.IndexOf(':');
            return colon >= 0 ? key.Substring(colon + 1) : key;
        }
    }
}
