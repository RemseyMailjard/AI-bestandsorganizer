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
        public string? DetectedCategoryKey { get; set; }
        public string? TargetFolderRelativePath { get; set; }
        public string? AISuggestedFilename { get; set; }
        public string? FinalFilename { get; set; }
        public string? ExtractedTextPreview { get; set; }
    }
}
