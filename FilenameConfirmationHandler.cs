// Inside your Program.cs or a similar class where you call AIFileOrganizer
using AI_bestandsorganizer; // Assuming this namespace

public static class UserInteraction
{
    public static async Task<string> ConsoleFilenameConfirmation(
        string originalFilenameBase,
        string suggestedFilenameBase,
        IProgress<string>? progress)
    {
        // Use progress.Report if available, otherwise Console.WriteLine for output
        Action<string> output = progress != null ? (msg) => progress.Report(msg) : Console.WriteLine;

        string finalName = suggestedFilenameBase; // Start with the suggestion

        while (true)
        {
            output($"\n--- Filename Suggestion ---");
            output($"Original name: '{originalFilenameBase}'");
            output($"Suggested name: '{suggestedFilenameBase}'");
            output($"---------------------------");
            output($"Confirm (Y/y - accept suggested), Keep Original (N/n - use '{originalFilenameBase}'), or type a new name and press Enter:");
            Console.Write("Your choice: "); // Direct console write for input prompt

            string? input = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(input) || input.Equals("y", StringComparison.OrdinalIgnoreCase))
            {
                output($"Accepted suggested name: '{suggestedFilenameBase}'");
                return suggestedFilenameBase;
            }
            else if (input.Equals("n", StringComparison.OrdinalIgnoreCase))
            {
                output($"Keeping original name: '{originalFilenameBase}'");
                return originalFilenameBase;
            }
            else
            {
                // User provided a new name
                string sanitizedInput = FileUtils.SanitizeFilename(input); // Re-use the sanitizer
                if (string.IsNullOrWhiteSpace(sanitizedInput))
                {
                    output("Invalid name entered. Please try again.");
                }
                else
                {
                    output($"Using custom name: '{sanitizedInput}'");
                    return sanitizedInput;
                }
            }
        }
    }
}