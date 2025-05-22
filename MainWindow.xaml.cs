using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls; // For ComboBoxItem, TextBox, etc.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SysForms = System.Windows.Forms;
using WpfMsg = System.Windows.MessageBox;
using WpfTextBox = System.Windows.Controls.TextBox;
using Microsoft.Win32;

// Assuming these types are defined in your project:
// public delegate Task<string> FilenameConfirmationHandler(string originalFilename, string suggestedFilename, IProgress<string>? progressReporter);
// public class AIFileOrganizer { /* ... */ public Task<(int processed, int moved)> OrganizeAsync(string src, string dst, FilenameConfirmationHandler? confirmer, IProgress<string> progress, CancellationToken token); }
// public class AIOrganizerSettings { public string ApiKey; public string ModelName; public bool EnableFileRenaming; public bool EnableDescriptiveFilenames; /* ... */ }
// public class FilenameInputDialog : Window { public FilenameInputDialog(string orig, string sugg); public string ResultFilename; /* ... */ }


namespace AI_bestandsorganizer
{
    public partial class MainWindow : Window // Ensure 'partial' keyword is present
    {
        private readonly AIFileOrganizer _organizer;
        private readonly ILogger<MainWindow> _logger;
        private readonly AIOrganizerSettings _settings;
        private CancellationTokenSource? _cts;

        public MainWindow(
            AIFileOrganizer organizer,
            ILogger<MainWindow> logger,
            IOptions<AIOrganizerSettings> options)
        {
            _organizer = organizer ?? throw new ArgumentNullException(nameof(organizer));
            _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));
            _settings  = options?.Value ?? throw new ArgumentNullException(nameof(options));

            // CRITICAL: This call requires MainWindow.xaml to be correctly processed.
            // If CS0103 occurs here, your XAML-to-C# link is broken.
            InitializeComponent();

            // UI-initialisatie: The following lines will cause CS0103 errors
            // if the corresponding x:Name attributes are missing in MainWindow.xaml
            // or if InitializeComponent() failed/is not found.

            // Ensure <PasswordBox x:Name="ApiKeyBox" ... /> exists in XAML
            ApiKeyBox.Password = _settings.ApiKey;

            // Ensure <ComboBox x:Name="ModelBox" ... /> exists in XAML
            // Ensure <ComboBox x:Name="ProviderBox" ... /> exists in XAML (as it affects ModelBox population)
            // It's assumed ProviderBox_SelectionChanged might run during InitializeComponent
            // if ProviderBox has a default selection in XAML.
            var modelItem = ModelBox.Items
                                     .OfType<ComboBoxItem>()
                                     .FirstOrDefault(i => (string?)i.Content == _settings.ModelName);

            if (modelItem != null)
            {
                ModelBox.SelectedItem = modelItem;
            }
            else if (ModelBox.Items.Count > 0)
            {
                ModelBox.SelectedItem = ModelBox.Items[0];
            }

            // Ensure <TextBox x:Name="SrcBox" ... /> exists in XAML
            SrcBox.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

            // Ensure <TextBox x:Name="DstBox" ... /> exists in XAML
            DstBox.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AI-mappen");

            // Ensure <CheckBox x:Name="RenameChk" ... /> exists in XAML
            RenameChk.IsChecked = _settings.EnableFileRenaming;

            // Initialize MetadataChk
            MetadataChk.IsChecked = _settings.GenerateMetadataFiles;
        }

        // ---------- Browse-knoppen ----------
        // SrcBox and DstBox are WpfTextBox (System.Windows.Controls.TextBox)
        // Ensure <Button Name="BrowseSrcButton" Click="BrowseSrc" ... /> uses SrcBox
        // Ensure <Button Name="BrowseDstButton" Click="BrowseDst" ... /> uses DstBox
        private void BrowseSrc(object? _, RoutedEventArgs e) => Browse(SrcBox);
        private void BrowseDst(object? _, RoutedEventArgs e) => Browse(DstBox);

        private static void Browse(WpfTextBox target) // WpfTextBox is System.Windows.Controls.TextBox
        {
            using var dlg = new SysForms.FolderBrowserDialog
            {
                InitialDirectory = Directory.Exists(target.Text)
                    ? target.Text
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dlg.ShowDialog() == SysForms.DialogResult.OK)
                target.Text = dlg.SelectedPath;
        }

        // ---------- Run-knop ----------
        // Ensure <Button Name="RunButton" Click="Run_Click" ... /> exists
        private async void Run_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button runBtn) runBtn.IsEnabled = false;

            // Ensure <TextBox x:Name="LogBox" ... /> exists in XAML
            LogBox.Clear();
            Log("🚀 Organiseren gestart …");

            if (string.IsNullOrWhiteSpace(ApiKeyBox.Password)) // ApiKeyBox already checked
            {
                Log("❌ Fout: API-key ontbreekt");
                WpfMsg.Show("API-key ontbreekt.", "AI File Organizer",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                if (sender is System.Windows.Controls.Button btn) btn.IsEnabled = true;
                return;
            }

            _settings.ApiKey             = ApiKeyBox.Password; 
            _settings.ModelName          = ((ComboBoxItem)ModelBox.SelectedItem)!.Content!.ToString()!; 
            _settings.EnableFileRenaming = RenameChk.IsChecked ?? false; // 

            // Update GenerateMetadataFiles setting
            _settings.GenerateMetadataFiles = MetadataChk.IsChecked ?? false;

            _cts = new CancellationTokenSource();

            try
            {
                var prog = new Progress<string>(Log); // Log uses LogBox

                FilenameConfirmationHandler? filenameConfirmer = null;
                // Assuming FilenameInputDialog, AIOrganizerSettings.EnableDescriptiveFilenames are okay
                if (_settings.EnableFileRenaming && _settings.EnableDescriptiveFilenames)
                {
                    filenameConfirmer = async (orig, sugg, reporter) =>
                    {
                        reporter?.Report($"Bevestig bestandsnaam voor '{orig}'…");

                        var result = await Dispatcher.InvokeAsync(() =>
                        {
                            var dlg = new FilenameInputDialog(orig, sugg) { Owner = this };
                            return dlg.ShowDialog() == true ? dlg.ResultFilename : orig;
                        });

                        reporter?.Report($"Naam gekozen: '{result}'");
                        return result;
                    };
                }

                // SrcBox and DstBox checked
                (int processed, int moved) = await _organizer
                    .OrganizeAsync(SrcBox.Text, DstBox.Text, filenameConfirmer, prog, _cts.Token);

                Log($"✅ Klaar! {moved}/{processed} bestanden verplaatst.");
            }
            catch (OperationCanceledException)
            {
                Log("⚠ Organisatie geannuleerd door gebruiker.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Onverwachte fout");
                Log($"❌ Onverwachte fout: {ex.Message}");
            }
            finally
            {
                if (sender is System.Windows.Controls.Button btn) btn.IsEnabled = true;
                _cts?.Dispose();
                _cts = null;
            }
        }

        // ---------- Log helper ----------
        private void Log(string line) =>
            Dispatcher.Invoke(() =>
            {
                // LogBox checked
                LogBox.AppendText(line + Environment.NewLine);
                LogBox.ScrollToEnd();
            });

        // ---------- LinkedIn-knop ----------
        // Ensure <Button Name="LinkedInButton" Click="OpenLinkedIn" ... /> exists
        private void OpenLinkedIn(object? _, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = "https://www.linkedin.com/in/remseymailjard/",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LinkedIn link openen mislukt.");
                WpfMsg.Show($"Kan link niet openen: {ex.Message}",
                            "Fout", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ---------- Provider-selectie ----------
        // Ensure <ComboBox x:Name="ProviderBox" SelectionChanged="ProviderBox_SelectionChanged" ... /> exists
        private void ProviderBox_SelectionChanged(object? s, SelectionChangedEventArgs e)
        {
            // Ensure these controls exist in XAML with x:Name:
            // ProviderBox, LblEndpoint, EndpointBox, LblDeploy, DeployBox, ModelBox
            if (ProviderBox == null || LblEndpoint == null || EndpointBox == null ||
                LblDeploy == null || DeployBox == null || ModelBox == null)
            {
                _logger?.LogWarning("ProviderBox_SelectionChanged called but some UI elements are null. Check XAML x:Name attributes and InitializeComponent call.");
                return;
            }

            bool azure = ProviderBox.SelectedIndex == 1;

            LblEndpoint.Visibility = EndpointBox.Visibility =
            LblDeploy.Visibility   = DeployBox.Visibility   = azure ? Visibility.Visible : Visibility.Collapsed;

            ModelBox.Items.Clear();

            if (azure || ProviderBox.SelectedIndex == 2)      // Azure of OpenAI
            {
                ModelBox.Items.Add(new ComboBoxItem { Content = "gpt-4o-mini" });
                ModelBox.Items.Add(new ComboBoxItem { Content = "gpt-35-turbo" });
            }
            else                                              // Gemini (default for Index 0 or any other index)
            {
                ModelBox.Items.Add(new ComboBoxItem { Content = "gemini-1.5-pro-latest" });
                ModelBox.Items.Add(new ComboBoxItem { Content = "gemini-1.5-flash-latest" });
            }

            if (ModelBox.Items.Count > 0)
            {
                ((ComboBoxItem)ModelBox.Items[0]).IsSelected = true;
            }
        }

        // Add this using statement if not already present for SaveFileDialog



        private void ExportLog_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(LogBox.Text))
            {
                WpfMsg.Show("Log is empty, nothing to export.", "Export Log", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Use the fully qualified name for the WPF SaveFileDialog to resolve ambiguity
            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Text Files (*.txt)|*.txt|CSV Files (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = $"AIOrganizer_Log_{DateTime.Now:yyyyMMdd_HHmmss}",
                Title = "Save Log File"
            };

            // ShowDialog() returns a bool? (nullable boolean) for WPF SaveFileDialog
            if (saveDialog.ShowDialog() == true)
            {
                try
                {
                    File.WriteAllText(saveDialog.FileName, LogBox.Text);
                    Log("📋 Log exported to: " + saveDialog.FileName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to export log.");
                    WpfMsg.Show($"Failed to export log: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }
}
