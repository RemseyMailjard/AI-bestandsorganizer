// FilenameInputDialog.xaml.cs
using System.Windows;
using System.Windows.Controls; // Needed for TextBox focus

namespace AI_bestandsorganizer
{
    public partial class FilenameInputDialog : Window
    {
        public string ResultFilename { get; private set; } = string.Empty;
        private readonly string _originalFilenameBase;
        private readonly string _suggestedFilenameBase;

        public FilenameInputDialog(string originalFilenameBase, string suggestedFilenameBase)
        {
            InitializeComponent();
            _originalFilenameBase = originalFilenameBase;
            _suggestedFilenameBase = suggestedFilenameBase;

            OriginalNameTextBlock.Text = originalFilenameBase;
            SuggestedNameTextBox.Text = suggestedFilenameBase;
            FinalNameTextBox.Text = suggestedFilenameBase; // Default input to suggested name
            ResultFilename = suggestedFilenameBase; // Set default result if dialog is closed directly
        }

        private void AcceptSuggested_Click(object sender, RoutedEventArgs e)
        {
            ProcessAndClose(SuggestedNameTextBox.Text);
        }

        private void KeepOriginal_Click(object sender, RoutedEventArgs e)
        {
            ProcessAndClose(_originalFilenameBase);
        }

        private void ApplyCustom_Click(object sender, RoutedEventArgs e)
        {
            ProcessAndClose(FinalNameTextBox.Text);
        }

        private void ProcessAndClose(string chosenName)
        {
            // Sanitize the chosen name before returning
            string sanitizedName = FileUtils.SanitizeFilename(chosenName);

            if (string.IsNullOrWhiteSpace(sanitizedName))
            {
                WarningTextBlock.Visibility = Visibility.Visible;
                FinalNameTextBox.Focus(); // Keep focus on the input box
            }
            else
            {
                ResultFilename = sanitizedName;
                DialogResult = true; // Indicates successful selection
            }
        }
    }
}