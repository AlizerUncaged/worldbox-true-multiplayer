using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace WorldboxMP.Installer
{
    public partial class MainWindow : Window
    {
        private bool _busy;

        public MainWindow()
        {
            InitializeComponent();
            string found = GameLocator.Find();
            if (found != null)
            {
                PathBox.Text = found;
                PathHint.Text = "Found your WorldBox installation automatically.";
            }
            else PathHint.Text = "Couldn't find WorldBox automatically. Click Browse and pick worldbox.exe.";
            Log("Ready.");
        }

        private void PathBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            PathHint.Text = GameLocator.IsGameFolder(PathBox.Text.Trim())
                ? "Looks good: worldbox.exe found."
                : "This folder doesn't contain worldbox.exe.";
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Pick worldbox.exe",
                Filter = "WorldBox|worldbox.exe|Programs (*.exe)|*.exe",
                CheckFileExists = true,
            };
            if (GameLocator.IsGameFolder(PathBox.Text.Trim())) dlg.InitialDirectory = PathBox.Text.Trim();
            if (dlg.ShowDialog(this) == true) PathBox.Text = Path.GetDirectoryName(dlg.FileName);
        }

        private async void Install_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            string game = PathBox.Text.Trim();
            if (!GameLocator.IsGameFolder(game))
            {
                MessageBox.Show(this, "Pick the folder that contains worldbox.exe first.", "WorldBox Multiplayer", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (Process.GetProcessesByName("worldbox").Length > 0)
            {
                MessageBox.Show(this, "WorldBox is running. Close it, then click Install again.", "WorldBox Multiplayer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetBusy(true);
            LaunchButton.Visibility = Visibility.Collapsed;
            var steps = new ModInstaller(game, Log, SetProgress);
            try
            {
                await Task.Run(() => steps.Run());
                SetProgress(100, "Done");
                Log("All set. Turn on Experimental Mode in WorldBox's settings if you haven't, then start the game.");
                LaunchButton.Visibility = Visibility.Visible;
                InstallButton.Content = "Reinstall";
            }
            catch (Exception ex)
            {
                SetProgress(0, "Failed");
                Log("Error: " + ex.Message);
                MessageBox.Show(this, "Installation failed:\n\n" + ex.Message, "WorldBox Multiplayer", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { SetBusy(false); }
        }

        private void Launch_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("steam://rungameid/" + GameLocator.SteamAppId) { UseShellExecute = true }); }
            catch
            {
                try { Process.Start(Path.Combine(PathBox.Text.Trim(), "worldbox.exe")); }
                catch (Exception ex) { Log("Couldn't start WorldBox: " + ex.Message); }
            }
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            InstallButton.IsEnabled = !busy;
            BrowseButton.IsEnabled = !busy;
            PathBox.IsEnabled = !busy;
        }

        private void Log(string line) => Dispatcher.Invoke(() =>
        {
            LogBox.AppendText((LogBox.Text.Length > 0 ? Environment.NewLine : "") + line);
            LogBox.ScrollToEnd();
        });

        private void SetProgress(double pct, string step) => Dispatcher.Invoke(() =>
        {
            Progress.Value = pct;
            StepText.Text = step;
        });
    }
}
