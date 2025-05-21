// ---------- AIFileOrganizer.cs ----------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// OpenXML & PdfPig
using DocumentFormat.OpenXml.Packaging;
using PdfDocument = UglyToad.PdfPig.PdfDocument;
using PdfPage = UglyToad.PdfPig.Content.Page;
using WordText = DocumentFormat.OpenXml.Wordprocessing.Text;

// DI / Logging
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Mscc-GenerativeAI
using Mscc.GenerativeAI;

namespace AI_bestandsorganizer
{
    // ------- Settings-POCO (appsettings.json → "AIOrganizer") -------
    // NOTE: You'll need to add this property to your AIOrganizerSettings class.
    /*
    public class AIOrganizerSettings
    {
        public string? ModelName { get; set; } = "gemini-pro";
        public List<string>? SupportedExtensions { get; set; }
        public Dictionary<string, string>? Categories { get; set; }
        public string FallbackCategory { get; set; } = "Uncategorized";
        public int MaxPromptChars { get; set; } = 8000;
        public bool EnableDescriptiveFilenames { get; set; } = false; // <-- ADD THIS LINE
    }
    */

    // NEW: Delegate for handling filename confirmation (UI-agnostic)
    /// <summary>
    /// Represents a method that prompts the user to confirm or modify a suggested filename.
    /// </summary>
    /// <param name="originalFilenameBase">The original filename without extension (e.g., "document").</param>
    /// <param name="suggestedFilenameBase">The AI-suggested filename without extension (e.g., "Project_Report_Q3_2023").</param>
    /// <param name="progress">An optional progress reporter for outputting messages to the user.</param>
    /// <returns>
    /// The final chosen filename without extension.
    /// Return <paramref name="suggestedFilenameBase"/> to accept AI suggestion.
    /// Return <paramref name="originalFilenameBase"/> to keep the original name.
    /// Return a new string for a custom name.
    /// </returns>
    public delegate Task<string> FilenameConfirmationHandler(string originalFilenameBase, string suggestedFilenameBase, IProgress<string>? progress);


    // ----------------------------------------------------------------
    //  AIFileOrganizer  –  organiseert bestanden m.b.v. Gemini-API
    // ----------------------------------------------------------------
    public class AIFileOrganizer
    {
        private readonly AIOrganizerSettings _settings;
        private readonly ILogger<AIFileOrganizer> _logger;
        private readonly HashSet<string> _supported;
        private readonly GoogleAI _google;

        public AIFileOrganizer(
            IOptions<AIOrganizerSettings> settings,
            GoogleAI google,
            ILogger<AIFileOrganizer> logger)
        {
            _settings  = settings.Value ?? throw new ArgumentNullException(nameof(settings));
            _google    = google       ?? throw new ArgumentNullException(nameof(google));
            _logger    = logger       ?? throw new ArgumentNullException(nameof(logger));

            _supported = new HashSet<string>(
                (_settings.SupportedExtensions ?? new()).Select(e => e.ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);
        }

        // ---------------- PUBLIC ----------------
        public async Task<(int processed, int moved)> OrganizeAsync(
            string srcDir,
            string dstDir,
            FilenameConfirmationHandler? confirmFilename = null, // NEW PARAMETER
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            srcDir = Path.GetFullPath(srcDir);
            dstDir = Path.GetFullPath(dstDir);

            var src = new DirectoryInfo(srcDir);
            if (!src.Exists) throw new DirectoryNotFoundException(src.FullName);

            Directory.CreateDirectory(dstDir);

            int processed = 0, moved = 0;

            foreach (var fileInfo in src.EnumerateFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!_supported.Contains(fileInfo.Extension.ToLowerInvariant()))
                {
                    progress?.Report($"Skipping {fileInfo.Name} (unsupported extension)");
                    continue;
                }

                processed++;
                progress?.Report($"📄 {fileInfo.Name} lezen …");

                string category = _settings.FallbackCategory;
                string targetLabel = $"0. {_settings.FallbackCategory}";
                string finalFilenameBase = Path.GetFileNameWithoutExtension(fileInfo.Name); // Default to original name

                try
                {
                    string text = await ExtractTextAsync(fileInfo).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        category = await ClassifyAsync(text, cancellationToken).ConfigureAwait(false);

                        // NEW: Generate descriptive filename and get user confirmation
                        if (_settings.EnableDescriptiveFilenames && confirmFilename != null)
                        {
                            progress?.Report($"📝 Generating descriptive filename for '{fileInfo.Name}'...");
                            string suggestedFilename = await GenerateFilenameAsync(text, fileInfo.Name, cancellationToken).ConfigureAwait(false);

                            // Call the external handler to get the final desired filename from the user
                            finalFilenameBase = await confirmFilename(
                                Path.GetFileNameWithoutExtension(fileInfo.Name),
                                suggestedFilename,
                                progress);

                            // Basic validation for the returned filename
                            finalFilenameBase = SanitizeFilename(finalFilenameBase);
                            if (string.IsNullOrWhiteSpace(finalFilenameBase))
                            {
                                // If the handler returned an invalid name, revert to original
                                finalFilenameBase = Path.GetFileNameWithoutExtension(fileInfo.Name);
                                progress?.Report($"⚠️ Invalid name chosen. Reverting to original name for '{fileInfo.Name}'.");
                            }
                        }
                    }

                    targetLabel = _settings.Categories.TryGetValue(category, out var mapped)
                                  ? mapped
                                  : $"0. {_settings.FallbackCategory}";
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Classificatie/naamgeneratie mislukt voor {File}", fileInfo.Name);
                    progress?.Report($"❌ Fout bij verwerken van {fileInfo.Name}: {ex.Message}");
                    // Continue to move with original name if classification/name gen failed
                }

                string targetDir = Path.Combine(dstDir, targetLabel);
                Directory.CreateDirectory(targetDir);

                // Use the finalFilenameBase for the destination path
                string dest = Path.Combine(targetDir, finalFilenameBase + fileInfo.Extension);
                int c = 1;
                while (File.Exists(dest))
                {
                    // If a file with the same new name exists, append a number
                    dest = Path.Combine(targetDir, $"{finalFilenameBase}_{c++}{fileInfo.Extension}");
                }

                try
                {
                    fileInfo.MoveTo(dest);
                    moved++;
                    progress?.Report($"✅ {fileInfo.Name} → {Path.GetFileName(dest)} (naar {targetLabel})");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Verplaatsen mislukt voor {File}", fileInfo.Name);
                    progress?.Report($"❌ Fout bij verplaatsen van {fileInfo.Name}: {ex.Message}");
                }
            }

            progress?.Report($"Organisatie klaar – {processed} verwerkt, {moved} verplaatst.");
            return (processed, moved);
        }

        // ---------------- GEMINI ----------------
        private async Task<string> ClassifyAsync(string text, CancellationToken ct)
        {
            string catList = string.Join('\n', _settings.Categories.Keys);
            string prompt =
                $"Classificeer dit document in één categorie:\n{catList}\n- {_settings.FallbackCategory} (fallback)\n\n" +
                text[..Math.Min(text.Length, _settings.MaxPromptChars)];

            var model = _google.GenerativeModel(_settings.ModelName);
            var result = await model.GenerateContent(prompt, cancellationToken: ct);
            string? ans = result.Text?.Trim();

            return !string.IsNullOrEmpty(ans) && _settings.Categories.ContainsKey(ans)
                 ? ans
                 : _settings.FallbackCategory;
        }

        // NEW: Method to generate a descriptive filename using Gemini
        private async Task<string> GenerateFilenameAsync(string text, string originalFilename, CancellationToken ct)
        {
            // Truncate text for filename generation prompt if it's very long, to focus on key info.
            // Using a smaller section than MaxPromptChars might be beneficial for filename tasks.
            const int filenamePromptTextLength = 2000; // Adjust as needed
            string relevantText = text[..Math.Min(text.Length, filenamePromptTextLength)];

            string prompt =
                "Suggest a very concise, descriptive, and human-readable filename (without extension) for the following document content.\n" +
                "The original filename was '{originalFilenameBase}'.\n" +
                "Ensure the suggested name is suitable for a file path, avoiding special characters like \\ / : * ? \" < > | and starting/ending spaces.\n" +
                "Keep it under 60 characters if possible, preferably using underscores or hyphens for spaces.\n" +
                "Only provide the filename, nothing else.\n\n" +
                relevantText;

            var model = _google.GenerativeModel(_settings.ModelName);
            var result = await model.GenerateContent(prompt, cancellationToken: ct);
            string? ans = result.Text?.Trim();

            if (string.IsNullOrEmpty(ans))
            {
                _logger.LogWarning("AI failed to suggest a filename for {OriginalFile}. Falling back to original base name.", originalFilename);
                return Path.GetFileNameWithoutExtension(originalFilename); // Fallback to original base name
            }

            // Sanitize the AI's suggestion
            string sanitizedAns = SanitizeFilename(ans);
            return string.IsNullOrEmpty(sanitizedAns) ? Path.GetFileNameWithoutExtension(originalFilename) : sanitizedAns;
        }

        // NEW: Helper method to sanitize a filename string
        // Made public static so it can be re-used by the console app for user input sanitization if desired.
        public static string SanitizeFilename(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename)) return string.Empty;

            // Define invalid characters for filenames and paths
            char[] invalidChars = Path.GetInvalidFileNameChars()
                                  .Concat(Path.GetInvalidPathChars())
                                  .Distinct()
                                  .ToArray();

            // Replace invalid characters with an underscore
            string sanitized = new string(filename.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());

            // Remove leading/trailing spaces, replace multiple spaces/underscores with single
            sanitized = sanitized.Trim()
                                 .Replace(" ", "_") // Replace spaces with underscores
                                 .Replace("__", "_") // Replace double underscores from previous step
                                 .Replace("__", "_"); // Handle triple/more underscores

            // Ensure it doesn't start or end with an underscore (if it wasn't already)
            if (sanitized.StartsWith("_")) sanitized = sanitized.Substring(1);
            if (sanitized.EndsWith("_")) sanitized = sanitized.Substring(0, sanitized.Length - 1);

            // Limit length to avoid path issues (e.g., 100-150 characters is a good practical limit)
            if (sanitized.Length > 100)
            {
                sanitized = sanitized.Substring(0, 100);
                // Ensure it doesn't end with an underscore if truncated
                if (sanitized.EndsWith("_")) sanitized = sanitized.Substring(0, sanitized.Length - 1);
            }

            return sanitized;
        }

        // ---------------- TEXT EXTRACT ----------------
        private static async Task<string> ExtractTextAsync(FileInfo fi)
        {
            string ext = fi.Extension.ToLowerInvariant();
            if (ext is ".txt" or ".md")
                return await File.ReadAllTextAsync(fi.FullName).ConfigureAwait(false);

            if (ext == ".docx")
            {
                var sb = new StringBuilder();
                try
                {
                    using var doc = WordprocessingDocument.Open(fi.FullName, false);
                    if (doc.MainDocumentPart?.Document?.Body != null)
                    {
                        foreach (WordText t in doc.MainDocumentPart.Document.Body.Descendants<WordText>())
                            sb.Append(t.Text).Append(' ');
                    }
                }
                catch (Exception ex)
                {
                    // Log or handle error if docx extraction fails (e.g., corrupted file)
                    Console.Error.WriteLine($"Error extracting text from DOCX {fi.Name}: {ex.Message}");
                }
                return sb.ToString();
            }

            if (ext == ".pdf")
            {
                var sb = new StringBuilder();
                try
                {
                    using PdfDocument pdf = PdfDocument.Open(fi.FullName);
                    foreach (PdfPage p in pdf.GetPages())
                        sb.Append(p.Text).Append(' ');
                }
                catch (Exception ex)
                {
                    // Log or handle error if pdf extraction fails (e.g., encrypted/corrupted file)
                    Console.Error.WriteLine($"Error extracting text from PDF {fi.Name}: {ex.Message}");
                }
                return sb.ToString();
            }

            return string.Empty;
        }
    }
}