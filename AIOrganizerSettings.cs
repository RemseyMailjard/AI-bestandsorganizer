using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AI_bestandsorganizer
{
    public class AIOrganizerSettings
    {
        public string ApiKey { get; set; } = "";
        public string ModelName { get; set; } = "gemini-2.5-pro-preview-05-06";
        public Dictionary<string, string> Categories { get; set; } = new();
        public List<string> SupportedExtensions { get; set; } = new() { ".pdf", ".docx", ".txt", ".md" };
        public string FallbackCategory { get; set; } = "Overig";
        public int MaxPromptChars { get; set; } = 4_000;
        public bool EnableDescriptiveFilenames { get; set; } = true;
    }
}
