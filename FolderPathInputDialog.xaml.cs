using System.Windows;

namespace AI_bestandsorganizer
{
    public partial class FolderPathInputDialog : Window
    {
        public string ResultFolderPath { get; private set; } = string.Empty;
        private readonly string _predefinedRelativePath;
        private readonly string _suggestedRelativePath;

        public FolderPathInputDialog(string predefinedRelativePath, string suggestedRelativePath)
        {
            InitializeComponent();
            _predefinedRelativePath = predefinedRelativePath;
            _suggestedRelativePath = suggestedRelativePath;

            PredefinedPathTextBlock.Text = predefinedRelativePath;
            SuggestedPathTextBox.Text = suggestedRelativePath;
            FinalPathTextBox.Text = suggestedRelativePath; // Default input to suggested path
            ResultFolderPath = suggestedRelativePath; // Default result
        }

        private void AcceptSuggested_Click(object sender, RoutedEventArgs e)
        {
            ProcessAndClose(_suggestedRelativePath);
        }

        private void UsePredefined_Click(object sender, RoutedEventArgs e)
        {
            ProcessAndClose(_predefinedRelativePath);
        }

        private void ApplyCustom_Click(object sender, RoutedEventArgs e)
        {
            ProcessAndClose(FinalPathTextBox.Text);
        }

        private void ProcessAndClose(string chosenPath)
        {
            string sanitizedPath = FileUtils.SanitizePathStructure(chosenPath);

            if (string.IsNullOrWhiteSpace(sanitizedPath) || sanitizedPath == "_")
            {
                WarningTextBlock.Text = "Invalid or empty path after sanitization. Please enter a valid relative folder path.";
                WarningTextBlock.Visibility = Visibility.Visible;
                FinalPathTextBox.Focus();
            }
            else
            {
                ResultFolderPath = sanitizedPath;
                DialogResult = true;
            }
        }
    }
}