#if NET8_0_OR_GREATER
#nullable disable
#endif
using System;
using System.IO;
using System.Xml;

namespace Massdex.SyncShark
{
    /// <summary>Loads setup messages shared by the updater, standalone shell and PowerShell installer.</summary>
    public static class SetupMessages
    {
        private static XmlDocument messages;

        public static void Load(string path)
        {
            using (var input = File.OpenRead(path)) Load(input);
        }

        private static void Load(Stream input)
        {
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
                document.Load(reader);
            messages = document;
        }

        public static string Text(string key, params object[] arguments)
        {
            if (messages == null)
            {
                using (var input = typeof(SetupMessages).Assembly.GetManifestResourceStream("setup-strings.xml"))
                    Load(input);
            }
            var entry = messages.DocumentElement.SelectSingleNode("message[@key='" + key + "']");
            if (entry == null) throw new InvalidDataException(key);
            return String.Format(System.Globalization.CultureInfo.InvariantCulture, entry.InnerText, arguments);
        }
    }
}
