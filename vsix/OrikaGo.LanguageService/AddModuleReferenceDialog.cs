using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;

namespace OrikaGo.LanguageService
{
    /// <summary>
    /// Input dialog for "加入 Go 模組參考...": module path (required) and
    /// version (optional; empty = latest). Built in code rather than XAML so the
    /// old-style csproj needs no XAML compilation wiring.
    /// </summary>
    internal sealed class AddModuleReferenceDialog : DialogWindow
    {
        private readonly TextBox _modulePathBox;
        private readonly TextBox _versionBox;

        public string ModulePath { get; private set; } = string.Empty;
        public string ModuleVersion { get; private set; } = string.Empty;

        public AddModuleReferenceDialog()
        {
            Title = GoStrings.AddReferenceDialogTitle;
            Width = 460;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            _modulePathBox = new TextBox { Margin = new Thickness(0, 2, 0, 8) };
            _versionBox = new TextBox { Margin = new Thickness(0, 2, 0, 8) };

            var okButton = new Button { Content = GoStrings.OkButton, Width = 80, Margin = new Thickness(0, 8, 8, 0), IsDefault = true };
            var cancelButton = new Button { Content = GoStrings.CancelButton, Width = 80, Margin = new Thickness(0, 8, 0, 0), IsCancel = true };
            okButton.Click += OnOk;

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(okButton);
            buttons.Children.Add(cancelButton);

            var layout = new StackPanel { Margin = new Thickness(12) };
            layout.Children.Add(new TextBlock { Text = GoStrings.ModulePathLabel });
            layout.Children.Add(_modulePathBox);
            layout.Children.Add(new TextBlock { Text = GoStrings.VersionLabel });
            layout.Children.Add(_versionBox);
            layout.Children.Add(buttons);
            Content = layout;

            Loaded += (s, e) => _modulePathBox.Focus();
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            string module = _modulePathBox.Text.Trim();
            string version = _versionBox.Text.Trim();

            // The values land on a "go get" command line via MSBuild; reject
            // anything a module path/version can never legally contain instead
            // of letting a typo produce a confusing build failure.
            if (module.Length == 0 || module.Any(char.IsWhiteSpace) || module.IndexOfAny(new[] { '"', '\'', '`' }) >= 0)
            {
                MessageBox.Show(this, GoStrings.InvalidModulePath, GoStrings.MessageBoxTitle,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (version.Any(char.IsWhiteSpace) || version.IndexOfAny(new[] { '"', '\'', '`' }) >= 0)
            {
                MessageBox.Show(this, GoStrings.InvalidVersion, GoStrings.MessageBoxTitle,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ModulePath = module;
            ModuleVersion = version;
            DialogResult = true;
        }
    }
}
