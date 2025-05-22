using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AI_bestandsorganizer
{
    public class FileMetadata
    {
        public string? OriginalFullPath { get; set; }
        public string? OriginalFilename { get; set; }
        public DateTime ProcessedTimestampUtc { get; set; }
        public string? DetectedCategoryRaw { get; set; } // Raw category from AI
        public string? TargetFolderLabel { get; set; }   // The folder name like "1. Bedrijfsadministratie"
        public string? AISuggestedFilename { get; set; } // Store this if available
        public string? FinalFilename { get; set; }
        public string? ExtractedTextPreview { get; set; }
        // Optional: public string? FileHashSha256 { get; set; }
        // Optional: public string? AISummary { get; set; }
        // Optional: public List<string>? AITags { get; set; }
    }
}
