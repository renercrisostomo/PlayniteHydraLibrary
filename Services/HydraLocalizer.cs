using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Xml;
using Playnite.SDK;

namespace HydraPlaynite.Services
{
    public class HydraLocalizer
    {
        private const string FallbackLanguageFile = "en_US.xaml";

        private readonly Dictionary<string, string> translations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly string localizationDirectory;
        private readonly IPlayniteAPI api;
        private readonly ILogger logger;

        public HydraLocalizer(IPlayniteAPI api, string pluginDirectory, ILogger logger)
        {
            this.api = api;
            this.logger = logger;
            this.localizationDirectory = Path.Combine(pluginDirectory ?? string.Empty, "Localization");
            Reload();
        }

        public void Reload()
        {
            translations.Clear();

            // 1. Carrega sempre o Inglês (en_US.xaml) como base de segurança
            LoadXamlDictionaryFile(Path.Combine(localizationDirectory, FallbackLanguageFile));

            // 2. Sobrepõe com o arquivo do idioma ativo no Playnite (ex: pt_BR.xaml)
            string activeLanguage = ResolveActiveLanguageCode();
            if (!string.Equals(activeLanguage, "en_US", StringComparison.OrdinalIgnoreCase))
            {
                string targetFile = FindBestLocalizationFile(activeLanguage);
                if (!string.IsNullOrEmpty(targetFile))
                {
                    LoadXamlDictionaryFile(targetFile);
                }
            }

            RegisterInWpfResources();
        }

        public string Get(string key, string defaultText = null)
        {
            if (string.IsNullOrEmpty(key))
                return defaultText ?? string.Empty;

            string translated;
            if (translations.TryGetValue(key, out translated) && !string.IsNullOrEmpty(translated))
                return translated;

            return defaultText ?? key;
        }

        public string Format(string key, params object[] args)
        {
            string pattern = Get(key, key);
            try
            {
                return string.Format(pattern, args);
            }
            catch
            {
                return pattern;
            }
        }

        public static Dictionary<string, string> ReadDictionaryFromXamlFile(string filePath)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return result;

            XmlDocument doc = new XmlDocument();
            doc.Load(filePath);
            if (doc.DocumentElement == null)
                return result;

            foreach (XmlNode node in doc.DocumentElement.ChildNodes)
            {
                if (node.NodeType != XmlNodeType.Element || node.Attributes == null)
                    continue;

                string key = ReadKeyAttribute(node);
                if (!string.IsNullOrEmpty(key))
                {
                    result[key] = node.InnerText ?? string.Empty;
                }
            }
            return result;
        }

        private string FindBestLocalizationFile(string languageCode)
        {
            if (string.IsNullOrEmpty(languageCode) || !Directory.Exists(localizationDirectory))
                return null;

            string exactFile = Path.Combine(localizationDirectory, languageCode + ".xaml");
            if (File.Exists(exactFile))
                return exactFile;

            string languagePrefix = languageCode.Split('_', '-')[0];
            if (!string.IsNullOrEmpty(languagePrefix))
            {
                string[] matchingFiles = Directory.GetFiles(localizationDirectory, languagePrefix + "*.xaml");
                if (matchingFiles.Length > 0)
                    return matchingFiles[0];
            }

            return null;
        }

        private string ResolveActiveLanguageCode()
        {
            try
            {
                if (api != null && api.ApplicationSettings != null && !string.IsNullOrWhiteSpace(api.ApplicationSettings.Language))
                {
                    return api.ApplicationSettings.Language.Trim().Replace('-', '_');
                }
            }
            catch { }

            try
            {
                string systemLang = CultureInfo.CurrentUICulture.Name;
                if (!string.IsNullOrWhiteSpace(systemLang))
                    return systemLang.Trim().Replace('-', '_');
            }
            catch { }

            return "en_US";
        }

        private void LoadXamlDictionaryFile(string filePath)
        {
            try
            {
                Dictionary<string, string> loaded = ReadDictionaryFromXamlFile(filePath);
                foreach (KeyValuePair<string, string> kvp in loaded)
                {
                    translations[kvp.Key] = kvp.Value;
                }
            }
            catch (Exception ex)
            {
                if (logger != null)
                {
                    logger.Warn(ex, "HydraLocalizer: Falha ao carregar arquivo de idioma " + filePath);
                }
            }
        }

        private static string ReadKeyAttribute(XmlNode node)
        {
            foreach (XmlAttribute attr in node.Attributes)
            {
                if (string.Equals(attr.LocalName, "Key", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(attr.Name, "x:Key", StringComparison.OrdinalIgnoreCase))
                {
                    return attr.Value;
                }
            }
            return null;
        }

        private void RegisterInWpfResources()
        {
            try
            {
                Application app = Application.Current;
                if (app == null || app.Dispatcher == null)
                    return;

                app.Dispatcher.Invoke(new Action(() =>
                {
                    if (app.Resources == null)
                        return;

                    foreach (KeyValuePair<string, string> entry in translations)
                    {
                        app.Resources[entry.Key] = entry.Value;
                    }
                }));
            }
            catch { }
        }
    }
}
