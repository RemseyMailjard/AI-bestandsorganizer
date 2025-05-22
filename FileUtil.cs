using System;
using System.Text;
using System.Text.RegularExpressions;

namespace AI_bestandsorganizer
{
   
    public static class FileUtils
    {
        // Tekens die niet in bestandsnamen mogen (Windows)
        private static readonly char[] InvalidFileNameChars = System.IO.Path.GetInvalidFileNameChars();

        /// <summary>
        /// Sanitize a string so that it is a safe filename (excl. extension)
        /// </summary>
        public static string SanitizeAsFilename(string inputFilenameWithoutExtension)
        {
            return SanitizeFilename(inputFilenameWithoutExtension);
        }

        /// <summary>
        /// Verwijdert ongeoorloofde karakters uit een bestandsnaam.
        /// </summary>
        internal static string SanitizeFilename(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "_";

            // Vervang alle ongeoorloofde karakters door een underscore
            var sanitized = new StringBuilder(input.Length);
            foreach (var c in input)
            {
                if (Array.IndexOf(InvalidFileNameChars, c) >= 0 || char.IsControl(c))
                    sanitized.Append('_');
                else
                    sanitized.Append(c);
            }

            // Optioneel: trim whitespace en punten aan begin/einde
            string result = sanitized.ToString().Trim(' ', '.');

            // Voorkom lege naam
            if (string.IsNullOrWhiteSpace(result))
                return "_";

            return result;
        }
    }

}
