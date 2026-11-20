using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using HydraPlaynite.Services;
using HydraPlaynite.Settings;

namespace HydraPlaynite.Views
{
    public class HydraLibrarySettingsView : UserControl
    {
        private const string TroubleshootingWikiUrl = "https://github.com/renercrisostomo/PlayniteHydraLibrary/wiki/Troubleshooting";
        private readonly HydraLocalizer localizer;

        public HydraLibrarySettingsView(HydraLibrary plugin, HydraLocalizer localizer)
        {
            this.localizer = localizer;
            if (this.localizer != null)
            {
                this.localizer.Reload();
            }

            this.Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(20),
                Content = BuildMainLayout()
            };
        }

        private StackPanel BuildMainLayout()
        {
            StackPanel root = new StackPanel { Orientation = Orientation.Vertical };

            root.Children.Add(CreateHeaderTitle("LOCPlayniteHydra_SettingsTitle"));
            root.Children.Add(CreateNoAuthBanner());
            root.Children.Add(CreateSectionHeader("LOCPlayniteHydra_SectionBehavior", topMargin: 4));

            root.Children.Add(CreateSettingCheckBox("LOCPlayniteHydra_ImportInstalled", "ImportInstalledGames"));
            root.Children.Add(CreateSettingCheckBox("LOCPlayniteHydra_ImportUninstalled", "ImportUninstalledGames"));

            root.Children.Add(CreateSettingCheckBox("LOCPlayniteHydra_LaunchViaHydraUri", "LaunchViaHydraUri"));

            root.Children.Add(CreateSettingCheckBox("LOCPlayniteHydra_AutoSyncPlaytime", "AutoSyncPlaytimeOnStartup"));
            root.Children.Add(CreateSettingCheckBox("LOCPlayniteHydra_KeepInstalledIfFilesExist", "KeepInstalledIfFilesExistOnDisk"));
            root.Children.Add(CreateSettingCheckBox("LOCPlayniteHydra_UpdateExistingOnRefresh", "UpdateExistingGamesOnRefresh"));
            root.Children.Add(CreateRestoreDefaultsButton());
            root.Children.Add(CreateTroubleshootingLink());

            return root;
        }

        private TextBlock CreateHeaderTitle(string localizationKey)
        {
            TextBlock title = new TextBlock
            {
                Text = T(localizationKey),
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 12)
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            return title;
        }

        private Border CreateNoAuthBanner()
        {
            StackPanel stack = new StackPanel { Orientation = Orientation.Vertical };

            TextBlock line1 = new TextBlock
            {
                Text = T("LOCPlayniteHydra_NoAuthNote"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13
            };
            line1.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            TextBlock line2 = new TextBlock
            {
                Text = T("LOCPlayniteHydra_TroubleshootingNote"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Margin = new Thickness(0, 8, 0, 0)
            };
            line2.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            stack.Children.Add(line1);
            stack.Children.Add(line2);

            return new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129)),
                Background = new SolidColorBrush(Color.FromArgb(30, 16, 185, 129)),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 16),
                Child = stack
            };
        }

        private TextBlock CreateSectionHeader(string localizationKey, double topMargin)
        {
            TextBlock header = new TextBlock
            {
                Text = T(localizationKey),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, topMargin, 0, 8)
            };
            header.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            return header;
        }

        private CheckBox CreateSettingCheckBox(string localizationKey, string propertyName)
        {
            TextBlock label = new TextBlock
            {
                Text = T(localizationKey),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(6, 0, 0, 0)
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            CheckBox checkBox = new CheckBox
            {
                Content = label,
                Margin = new Thickness(0, 5, 0, 7),
                VerticalContentAlignment = VerticalAlignment.Center
            };

            checkBox.SetBinding(CheckBox.IsCheckedProperty, new Binding(propertyName)
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });

            return checkBox;
        }


        private Button CreateRestoreDefaultsButton()
        {
            Button btnRestoreDefaults = new Button
            {
                Content = T("LOCPlayniteHydra_BtnRestoreDefaults"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 8, 14, 8),
                Margin = new Thickness(0, 16, 0, 14)
            };
            btnRestoreDefaults.Click += OnRestoreDefaultsClicked;
            return btnRestoreDefaults;
        }

        private TextBlock CreateTroubleshootingLink()
        {
            Hyperlink link = new Hyperlink(new Run(T("LOCPlayniteHydra_LinkTroubleshooting")))
            {
                NavigateUri = new Uri(TroubleshootingWikiUrl)
            };
            link.RequestNavigate += OnTroubleshootingNavigate;

            TextBlock container = new TextBlock
            {
                Margin = new Thickness(0, 4, 0, 6)
            };
            container.Inlines.Add(link);
            return container;
        }

        private void OnTroubleshootingNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                e.Handled = true;
            }
            catch
            {
            }
        }

        private void OnRestoreDefaultsClicked(object sender, RoutedEventArgs e)
        {
            HydraLibrarySettings currentSettings = this.DataContext as HydraLibrarySettings;
            if (currentSettings != null)
            {
                currentSettings.ResetToDefaults();
            }
        }

        private string T(string key)
        {
            return localizer != null ? localizer.Get(key, key) : key;
        }
    }
}
