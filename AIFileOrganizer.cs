// ---------- AIFileOrganizer.cs ----------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

using DocumentFormat.OpenXml.Packaging;
using PdfDocument = UglyToad.PdfPig.PdfDocument;
using PdfPage = UglyToad.PdfPig.Content.Page;
using WordText = DocumentFormat.OpenXml.Wordprocessing.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Mscc.GenerativeAI;

namespace AI_bestandsorganizer
{
    public delegate Task<string> FilenameConfirmationHandler(string originalFilenameBase, string suggestedFilenameBase, IProgress<string>? progress);

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

        public async Task<(int processed, int moved, long totalTokensUsed)> OrganizeAsync(
            string srcDir,
            string dstDir,
            FilenameConfirmationHandler? confirmFilename = null,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            srcDir = Path.GetFullPath(srcDir);
            dstDir = Path.GetFullPath(dstDir);

            var src = new DirectoryInfo(srcDir);
            if (!src.Exists) throw new DirectoryNotFoundException(src.FullName);

            Directory.CreateDirectory(dstDir);

            int processed = 0, moved = 0;
            long totalTokensUsed = 0;

            foreach (var fileInfo in src.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!_supported.Contains(fileInfo.Extension.ToLowerInvariant()))
                {
                    progress?.Report($"Skipping {fileInfo.Name} (unsupported extension)");
                    continue;
                }

                processed++;
                progress?.Report($"📄 Reading {fileInfo.FullName}...");

                string category = _settings.FallbackCategory;
                string targetLabel = _settings.Categories.TryGetValue(_settings.FallbackCategory, out var fbLabel)
                                     ? fbLabel
                                     : $"0. {_settings.FallbackCategory}"; // Ensure fallback has a prefix if needed
                string finalFilenameBase = Path.GetFileNameWithoutExtension(fileInfo.Name);

                try
                {
                    string text = await ExtractTextAsync(fileInfo).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var (classifiedCategory, classificationTokens) = await ClassifyAsync(text, progress, cancellationToken).ConfigureAwait(false);
                        category = classifiedCategory;
                        totalTokensUsed += classificationTokens;
                        progress?.Report($"Tokens for classification: {classificationTokens}. Classified as: {category}");


                        if (_settings.EnableDescriptiveFilenames && confirmFilename != null)
                        {
                            progress?.Report($"📝 Generating descriptive filename for '{fileInfo.Name}' ({category})...");
                            var (suggestedFilename, filenameTokens) = await GenerateFilenameAsync(text, fileInfo.Name, category, progress, cancellationToken).ConfigureAwait(false);
                            totalTokensUsed += filenameTokens;
                            progress?.Report($"Tokens for filename suggestion: {filenameTokens}");

                            finalFilenameBase = await confirmFilename(
                                Path.GetFileNameWithoutExtension(fileInfo.Name),
                                suggestedFilename,
                                progress);

                            finalFilenameBase = SanitizeFilename(finalFilenameBase);
                            if (string.IsNullOrWhiteSpace(finalFilenameBase))
                            {
                                finalFilenameBase = Path.GetFileNameWithoutExtension(fileInfo.Name);
                                progress?.Report($"⚠️ Invalid name chosen. Reverting to original name for '{fileInfo.Name}'.");
                            }
                        }
                    }
                    else
                    {
                        _logger.LogWarning("No text extracted from {File}. Classifying to fallback.", fileInfo.FullName);
                        progress?.Report($"⚠️ No text extracted from '{fileInfo.Name}'. Moving to '{_settings.FallbackCategory}'.");
                    }

                    // Use the determined category (which might be fallback) to find the target label
                    targetLabel = _settings.Categories.TryGetValue(category, out var mapped)
                                  ? mapped
                                  : _settings.Categories.TryGetValue(_settings.FallbackCategory, out fbLabel) ? fbLabel : $"0. {_settings.FallbackCategory}";
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Classification/filename generation failed for {File}", fileInfo.FullName);
                    progress?.Report($"❌ Error processing {fileInfo.Name}: {ex.Message}. Moving to '{_settings.FallbackCategory}'.");
                    // Ensure category and targetLabel are set to fallback if an error occurs
                    category = _settings.FallbackCategory;
                    targetLabel = _settings.Categories.TryGetValue(_settings.FallbackCategory, out fbLabel) ? fbLabel : $"0. {_settings.FallbackCategory}";
                }

                string targetDir = Path.Combine(dstDir, targetLabel);
                Directory.CreateDirectory(targetDir);

                string dest = Path.Combine(targetDir, finalFilenameBase + fileInfo.Extension);
                int c = 1;
                while (File.Exists(dest))
                {
                    dest = Path.Combine(targetDir, $"{finalFilenameBase}_{c++}{fileInfo.Extension}");
                }

                try
                {
                    fileInfo.MoveTo(dest);
                    moved++;
                    progress?.Report($"✅ {fileInfo.Name} → {Path.GetFileName(dest)} (to {targetLabel})");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Move failed for {File}", fileInfo.FullName);
                    progress?.Report($"❌ Error moving {fileInfo.Name}: {ex.Message}");
                }
            }

            progress?.Report($"Organization complete – {processed} processed, {moved} moved. Total tokens: {totalTokensUsed}");
            return (processed, moved, totalTokensUsed);
        }

        private async Task<(string category, int tokens)> ClassifyAsync(string text, IProgress<string>? progress, CancellationToken ct)
        {
            // Prepare the list of category keys from appsettings.json
            // These are the EXACT strings the AI should try to return.
            var categoryKeys = _settings.Categories.Keys.Select(k => k.Trim()).ToList();
            string catListString = string.Join("\n- ", categoryKeys);
            int tokensUsed = 0;

            // --- Improved Prompt ---
            // Added more explicit instructions, structure, and a clear fallback instruction.
            // Consider adding 1-2 shot examples if simple prompting isn't enough.
            string prompt =
                "You are an expert document classifier. Your task is to classify the following document content into ONE of the predefined categories. " +
                "The available categories are:\n" +
                $"- {catListString}\n\n" +
                "Instructions:\n" +
                "1. Read the document content carefully.\n" +
                "2. Determine which of the listed categories best describes the main topic of the document.\n" +
                $"3. If the document clearly fits one of the categories, respond with the EXACT category name from the list. For example, if the best category is 'Financiën', your response must be 'Financiën'.\n" +
                $"4. If the document does not fit well into any of the listed categories, or if you are unsure, respond with the EXACT phrase: '{_settings.FallbackCategory}'.\n" +
                "5. DO NOT add any extra text, explanations, numbers, or punctuation around the category name. Your entire response should be ONLY the chosen category name or the fallback phrase.\n\n" +
                // Optional: Few-shot examples (uncomment and adapt if needed)
                // "Example 1:\n" +
                // "Document Content: [Short example text about a bank statement]\n" +
                // "Category: Financiën\n\n" +
                // "Example 2:\n" +
                // "Document Content: [Short example text about a doctor's appointment]\n" +
                // "Category: Gezondheid en Medisch\n\n" +
                "Document Content to Classify:\n" +
                "-------------------------------------\n" +
                text[..Math.Min(text.Length, _settings.MaxPromptChars)] +
                "\n-------------------------------------\n" +
                "Chosen Category:"; // Encourage the AI to fill in its choice here

            _logger.LogDebug("Classification Prompt for AI:\n{Prompt}", prompt);

            var model = _google.GenerativeModel(_settings.ModelName);
            string? rawAiResponse = null;
            GenerateContentResponse? apiResult = null;
            try
            {
                apiResult = await model.GenerateContent(prompt, cancellationToken: ct);
                rawAiResponse = apiResult.Text?.Trim();
                tokensUsed = apiResult.UsageMetadata?.TotalTokenCount ?? 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling Gemini API for classification.");
                progress?.Report($"⚠️ API error during classification: {ex.Message}");
                return (_settings.FallbackCategory, tokensUsed); // Return fallback on API error
            }

            _logger.LogInformation("Raw AI response (classification): '{RawAiResponse}'", rawAiResponse);
            _logger.LogDebug("Tokens used for classification: {Tokens}", tokensUsed);

            if (string.IsNullOrWhiteSpace(rawAiResponse))
            {
                _logger.LogWarning("AI returned an empty or whitespace response for classification. Falling back to '{Fallback}'.", _settings.FallbackCategory);
                return (_settings.FallbackCategory, tokensUsed);
            }

            // --- Improved Response Normalization and Matching ---
            // Remove potential prefixes/suffixes more robustly and perform a case-insensitive direct match.
            string normalizedResponse = rawAiResponse.Trim();

            // Strip common prefixes/suffixes that models might add despite instructions
            string[] prefixesToRemove = { "Category:", "Categorie:", "Chosen Category:", "Classification:" };
            foreach (var prefix in prefixesToRemove)
            {
                if (normalizedResponse.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    normalizedResponse = normalizedResponse.Substring(prefix.Length).Trim();
                    break;
                }
            }
            // Remove potential quotes or list markers if the AI still adds them
            normalizedResponse = normalizedResponse.TrimStart('"', '\'', '-', '*').TrimEnd('"', '\'', '.');
            normalizedResponse = normalizedResponse.Trim();

            _logger.LogDebug("Normalized AI response (classification): '{NormalizedResponse}'", normalizedResponse);

            // Case-insensitive direct match against the *keys* of your Categories dictionary
            string? matchedCategoryKey = categoryKeys
                .FirstOrDefault(key => string.Equals(key, normalizedResponse, StringComparison.OrdinalIgnoreCase));

            if (matchedCategoryKey != null)
            {
                _logger.LogInformation("AI classified as '{MatchedCategoryKey}' (matched from AI response '{RawAiResponse}').", matchedCategoryKey, rawAiResponse);
                return (matchedCategoryKey, tokensUsed); // Return the *exact key* from your settings
            }
            else
            {
                // If it's exactly the fallback category name (case-insensitive)
                if (string.Equals(_settings.FallbackCategory, normalizedResponse, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("AI explicitly chose fallback category: '{FallbackCategory}'.", _settings.FallbackCategory);
                    return (_settings.FallbackCategory, tokensUsed);
                }

                _logger.LogWarning("AI response '{RawAiResponse}' (normalized to '{NormalizedResponse}') could not be mapped to a known category key. Falling back to '{Fallback}'.", rawAiResponse, normalizedResponse, _settings.FallbackCategory);
                return (_settings.FallbackCategory, tokensUsed);
            }
        }

        private async Task<(string filename, int tokens)> GenerateFilenameAsync(string text, string originalFilename, string category, IProgress<string>? progress, CancellationToken ct)
        {
            string relevantText = text[..Math.Min(text.Length, _settings.MaxPromptChars)];
            int tokensUsed = 0;

            string prompt =
                "Suggest a highly descriptive, concise, and human-readable filename (without extension) for the following document content.\n" +
                $"The document has been classified into the category: '{category}'. Use this as context.\n" +
                "Focus on the core topic, relevant dates (e.g., YYYY-MM-DD or YYYYMMDD if present), and key entities (e.g., company names, project names).\n" +
                "Avoid generic terms like 'document', 'scan', 'report' unless they are part of a specific, meaningful title within the content.\n" +
                "The original filename was '{originalFilename}'.\n" +
                "Ensure the name is suitable for file paths (no invalid characters like \\ / : * ? \" < > |), keep it under 60 characters, and use underscores or hyphens for spaces.\n" +
                "IMPORTANT: Respond ONLY with the suggested filename, without any extra text or explanation.\n\n" + // Changed "BELANGRIJK" to "IMPORTANT" for consistency if the model prefers English for instructions
                "Document content:\n" +
                relevantText;

            _logger.LogDebug("Filename Prompt for AI:\n{Prompt}", prompt);

            var model = _google.GenerativeModel(_settings.ModelName);
            string? ans = null;
            GenerateContentResponse? result = null;
            try
            {
                result = await model.GenerateContent(prompt, cancellationToken: ct);
                ans = result.Text?.Trim();
                tokensUsed = result.UsageMetadata?.TotalTokenCount ?? 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling Gemini API for filename generation.");
                progress?.Report($"⚠️ API error during filename generation: {ex.Message}");
            }

            _logger.LogDebug("Raw AI response (filename): '{Ans}'", ans);
            _logger.LogDebug("Tokens used for filename: {Tokens}", tokensUsed);

            if (string.IsNullOrEmpty(ans))
            {
                _logger.LogWarning("AI failed to suggest a filename for {OriginalFile}. Falling back to original base name.", originalFilename);
                return (Path.GetFileNameWithoutExtension(originalFilename), tokensUsed);
            }

            string sanitizedAns = SanitizeFilename(ans);
            _logger.LogDebug("Sanitized filename: '{SanitizedAns}'", sanitizedAns);

            string finalFilename = string.IsNullOrEmpty(sanitizedAns) ? Path.GetFileNameWithoutExtension(originalFilename) : sanitizedAns;
            return (finalFilename, tokensUsed);
        }

        public static string SanitizeFilename(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename)) return string.Empty;
            char[] invalidChars = Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()).Distinct().ToArray();
            string sanitized = new string(filename.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
            sanitized = sanitized.Trim().Replace(" ", "_");

            // Replace multiple underscores with a single one
            sanitized = Regex.Replace(sanitized, @"_+", "_");

            if (sanitized.StartsWith("_")) sanitized = sanitized.Substring(1);
            if (sanitized.EndsWith("_")) sanitized = sanitized.Substring(0, sanitized.Length - 1);

            if (sanitized.Length > 100) // Keep filename length reasonable
            {
                sanitized = sanitized.Substring(0, 100);
                // Re-check for trailing underscore after truncation
                if (sanitized.EndsWith("_")) sanitized = sanitized.Substring(0, sanitized.Length - 1);
            }
            return sanitized;
        }

        private async Task<string> ExtractTextAsync(FileInfo fi)
        {
            string ext = fi.Extension.ToLowerInvariant();
            if (ext is ".txt" or ".md")
            {
                try { return await File.ReadAllTextAsync(fi.FullName).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "Error reading text file {File}", fi.Name); return string.Empty; }
            }
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
                catch (Exception ex) { _logger.LogError(ex, "Error extracting text from DOCX {File}", fi.Name); }
                return sb.ToString().Trim(); // Added Trim()
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
                catch (Exception ex) { _logger.LogError(ex, "Error extracting text from PDF {File}", fi.Name); }
                return sb.ToString().Trim(); // Added Trim()
            }
            return string.Empty;
        }
    }
}