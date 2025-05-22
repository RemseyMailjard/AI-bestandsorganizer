// FileUtils.cs
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AI_bestandsorganizer
{
    public static class FileUtils
    {
        private static readonly char[] InvalidSystemFileNameChars = System.IO.Path.GetInvalidFileNameChars();
        private static readonly char[] _invalidPathSegmentChars = Path.GetInvalidFileNameChars()
            .Concat(new[] { '/', '\\' })
            .Distinct().ToArray();

        public static string SanitizeAsFilename(string inputFilenameWithoutExtension)
        {
            return SanitizeFilename(inputFilenameWithoutExtension);
        }

        /// <summary>
        /// Removes invalid characters from a filename.
        /// </summary>
        public static string SanitizeFilename(string input) // Made public
        {
            if (string.IsNullOrWhiteSpace(input))
                return "_";

            var sanitized = new StringBuilder(input.Length);
            foreach (var c in input)
            {
                if (Array.IndexOf(InvalidSystemFileNameChars, c) >= 0 || char.IsControl(c))
                    sanitized.Append('_');
                else
                    sanitized.Append(c);
            }
            string result = sanitized.ToString().Trim(' ', '.');
            if (string.IsNullOrWhiteSpace(result))
                return "_";
            return result;
        }

        /// <summary>
        /// Sanitizes a single part of a path (a folder name or a filename without extension).
        /// </summary>
        public static string SanitizePathPart(string namePart)
        {
            if (string.IsNullOrWhiteSpace(namePart)) return "_";

            namePart = Regex.Replace(namePart, @"\s+", "_");
            var sb = new StringBuilder();
            foreach (char c in namePart)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == '(' || c == ')')
                {
                    sb.Append(c);
                }
                else if (Array.IndexOf(_invalidPathSegmentChars, c) >= 0 || c < 32)
                {
                    sb.Append('_');
                }
                else
                {
                    sb.Append(c);
                }
            }
            string clean = sb.ToString();
            clean = Regex.Replace(clean, "_+", "_");
            clean = clean.TrimStart('_').TrimEnd('_');

            if (string.IsNullOrWhiteSpace(clean) || clean == "." || clean == "..")
            {
                clean = "_";
            }

            const int MaxPartLength = 100;
            if (clean.Length > MaxPartLength)
            {
                clean = clean.Substring(0, MaxPartLength).TrimEnd('_');
            }
            return string.IsNullOrWhiteSpace(clean) ? "_" : clean;
        }

        /// <summary>
        /// Sanitizes a relative path string, cleaning each part.
        /// </summary>
        public static string SanitizePathStructure(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return "Uncategorized_Path";
            }

            string normalizedPath = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            var parts = normalizedPath.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

            var sanitizedParts = parts
                .Select(SanitizePathPart)
                .Where(p => !string.IsNullOrWhiteSpace(p) && p != "_")
                .ToList();

            if (!sanitizedParts.Any())
            {
                return "Default_Path";
            }

            return string.Join(Path.DirectorySeparatorChar.ToString(), sanitizedParts);
        }
    }
}