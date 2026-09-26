using System;
using TrenchHQ;
using TrenchHQ.Helpers;
using TrenchHQ.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace TrenchHQ.Views
{
    public sealed partial class DashboardTabView : UserControl
    {
        private bool _isApplyingSettings;
        private bool _isApplyingPanelSelections;
        private bool _isDesktopDisplayRunning;
        private bool _isDesktopDisplayChanging;
        private DesktopPanelManager? _panelManager;

        private sealed record PanelSelectionTag(string Id, bool IsDocked);

        public DashboardTabView()
        {
            InitializeComponent();
            Loaded += OnDashboardLoaded;
            Unloaded += OnDashboardUnloaded;
        }

        private async void OnDashboardLoaded(object sender, RoutedEventArgs e)
        {
            if (Application.Current is App app)
            {
                await app.EnsureInitializedAsync();
                _panelManager = app.PanelManager;
                _panelManager.DefinitionsChanged -= OnPanelDefinitionsChanged;
                _panelManager.DefinitionsChanged += OnPanelDefinitionsChanged;
                _panelManager.StateChanged -= OnPanelRuntimeStateChanged;
                _panelManager.StateChanged += OnPanelRuntimeStateChanged;
                _panelManager.DisplayEnvironmentChanged -= OnPanelDefinitionsChanged;
                _panelManager.DisplayEnvironmentChanged += OnPanelDefinitionsChanged;
                ApplySettings(SettingsService.ReadUiSettings());
                RefreshPanelSelections();
                ApplyStartupRegistrationStatus(await StartupRegistrationService.GetStatusAsync());
                SetDesktopDisplayState(app.IsDesktopDisplayRunning);
            }
            else
            {
                ApplySettings(SettingsService.ReadUiSettings());
            }

        }

        private void OnDashboardUnloaded(object sender, RoutedEventArgs e)
        {
            if (_panelManager != null)
            {
                _panelManager.DefinitionsChanged -= OnPanelDefinitionsChanged;
                _panelManager.StateChanged -= OnPanelRuntimeStateChanged;
                _panelManager.DisplayEnvironmentChanged -= OnPanelDefinitionsChanged;
            }
        }

        private void OnPanelDefinitionsChanged(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(RefreshPanelSelections);
        }

        private void OnPanelRuntimeStateChanged(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(RefreshPanelSelections);
        }

        private async void OnDashboardSettingChanged(object sender, RoutedEventArgs e)
        {
            if (_isApplyingSettings)
            {
                return;
            }

            LaunchOnStartupToggle.IsEnabled = false;
            var status = await StartupRegistrationService.SetEnabledAsync(LaunchOnStartupToggle.IsOn);
            SettingsService.SaveLaunchOnStartupSnapshot(status.IsEnabled);
            ApplyStartupRegistrationStatus(status);
        }

        private async void OnDesktopDisplayClick(object sender, RoutedEventArgs e)
        {
            if (_isDesktopDisplayChanging || Application.Current is not App app)
            {
                return;
            }

            _isDesktopDisplayChanging = true;
            DesktopDisplayButton.IsEnabled = false;
            try
            {
                if (app.IsDesktopDisplayRunning)
                {
                    app.StopDesktopDisplay();
                }
                else
                {
                    await app.StartDesktopDisplayAsync();
                }
            }
            finally
            {
                _isDesktopDisplayChanging = false;
                SetDesktopDisplayState(app.IsDesktopDisplayRunning);
            }
        }

        internal void ApplySettings(UiSettings settings)
        {
            _isApplyingSettings = true;
            try
            {
                if (LaunchOnStartupToggle != null)
                {
                    LaunchOnStartupToggle.IsOn = settings.LaunchOnStartup;
                }

            }
            finally
            {
                _isApplyingSettings = false;
                UpdateDesktopDisplayButtonAvailability();
            }
        }

        internal void SetDesktopDisplayState(bool isRunning)
        {
            _isDesktopDisplayRunning = isRunning;
            if (DesktopDisplayButton != null)
            {
                DesktopDisplayButton.Content = isRunning
                    ? "Stop Desktop Display"
                    : "Start Desktop Display";
                DesktopDisplayButton.Style = (Style)Application.Current.Resources[
                    isRunning ? "StagedSecondaryButtonStyle" : "StagedPrimaryButtonStyle"];
            }

            UpdateDesktopDisplayButtonAvailability();
        }

        internal void ApplyStartupRegistrationStatus(StartupRegistrationStatus status)
        {
            _isApplyingSettings = true;
            try
            {
                LaunchOnStartupToggle.IsOn = status.IsEnabled;
                LaunchOnStartupToggle.IsEnabled = status.Availability == StartupRegistrationAvailability.Available;
                StartupStatusText.Text = status.Availability switch
                {
                    StartupRegistrationAvailability.DisabledByUser =>
                        "Disabled in Windows Startup Apps. Re-enable TrenchHQ there.",
                    StartupRegistrationAvailability.DisabledByPolicy =>
                        status.IsEnabled ? "Enabled by Windows policy." : "Disabled by Windows policy.",
                    StartupRegistrationAvailability.Unavailable =>
                        "Unavailable for this installation.",
                    _ => status.IsEnabled
                        ? "TrenchHQ will launch automatically on startup."
                        : "Off by default."
                };
            }
            finally
            {
                _isApplyingSettings = false;
            }
        }

        private void UpdateDesktopDisplayButtonAvailability()
        {
            if (DesktopDisplayButton == null || _isDesktopDisplayChanging)
            {
                return;
            }

            DesktopDisplayButton.IsEnabled = _isDesktopDisplayRunning
                                             || _panelManager?.HasEnabledPanels == true;
        }

        private void RefreshPanelSelections()
        {
            if (DesktopPanelSelectionList == null || _panelManager == null)
            {
                return;
            }

            _isApplyingPanelSelections = true;
            try
            {
                DesktopPanelSelectionList.Children.Clear();
                var overlays = _panelManager.GetDefinitions();
                var dockedBars = _panelManager.GetDockedBarDefinitions();
                if (overlays.Length == 0 && dockedBars.Length == 0)
                {
                    DesktopPanelSelectionList.Children.Add(new TextBlock
                    {
                        Text = "Create an overlay or screen-edge panel to begin.",
                        Opacity = 0.7
                    });
                    return;
                }

                foreach (var definition in overlays)
                {
                    var hasContent = _panelManager.HasDisplayContent(definition);
                    var contentName = _panelManager.GetPanelContentName(definition.UseApplicationWindow, definition.WidgetIds);
                    DesktopPanelSelectionList.Children.Add(CreatePanelRow(
                        hasContent
                            ? $"{definition.Name} · {contentName} · {definition.WidthDip:0} px"
                            : $"{definition.Name} · {contentName}",
                        definition.Enabled,
                        hasContent,
                        new PanelSelectionTag(definition.Id, false)));
                }

                foreach (var definition in dockedBars)
                {
                    var hasContent = _panelManager.HasDisplayContent(definition);
                    var contentName = _panelManager.GetPanelContentName(definition.UseApplicationWindow, definition.WidgetIds);
                    var monitor = string.IsNullOrWhiteSpace(definition.MonitorDeviceName)
                        ? "Default monitor"
                        : definition.MonitorDeviceName.Replace(@"\\.\", string.Empty, StringComparison.OrdinalIgnoreCase);
                    DesktopPanelSelectionList.Children.Add(CreatePanelRow(
                        hasContent
                            ? $"{definition.Name} · {contentName} · {definition.Edge} · {monitor}"
                            : $"{definition.Name} · {contentName}",
                        definition.Enabled,
                        hasContent,
                        new PanelSelectionTag(definition.Id, true)));
                }
            }
            finally
            {
                _isApplyingPanelSelections = false;
                UpdateDesktopDisplayButtonAvailability();
            }
        }

        private FrameworkElement CreatePanelRow(
            string label,
            bool enabled,
            bool hasContent,
            PanelSelectionTag tag)
        {
            var row = new Grid
            {
                ColumnSpacing = 12
            };
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto
            });

            var content = new TextBlock
            {
                Text = label,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -3, 0, 0)
            };

            var checkBox = new CheckBox
            {
                Content = content,
                IsChecked = hasContent && enabled,
                IsEnabled = hasContent,
                Tag = tag,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            checkBox.Checked += OnPanelLaunchSelectionChanged;
            checkBox.Unchecked += OnPanelLaunchSelectionChanged;
            AutomationProperties.SetName(checkBox, label);
            Grid.SetColumn(checkBox, 0);
            row.Children.Add(checkBox);

            var actionLabel = GetPanelRuntimeActionLabel(tag);
            if (actionLabel != null)
            {
                var actionButton = new Button
                {
                    Content = actionLabel,
                    MinWidth = 72,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Style = (Style)Application.Current.Resources["CompactButtonStyle"],
                    Tag = tag
                };
                actionButton.Click += OnPanelRuntimeActionClick;
                AutomationProperties.SetName(actionButton, $"{actionLabel} {label}");
                Grid.SetColumn(actionButton, 1);
                row.Children.Add(actionButton);
            }

            return row;
        }

        private string? GetPanelRuntimeActionLabel(PanelSelectionTag tag)
        {
            if (_panelManager == null)
            {
                return null;
            }

            var isRunning = tag.IsDocked
                ? _panelManager.IsDockedBarRunning(tag.Id)
                : _panelManager.IsRunning(tag.Id);
            if (!isRunning)
            {
                return null;
            }

            var isMinimized = tag.IsDocked
                ? _panelManager.IsDockedBarMinimized(tag.Id)
                : _panelManager.IsOverlayMinimized(tag.Id);
            return isMinimized ? "Restore" : "Minimize";
        }

        private async void OnPanelRuntimeActionClick(object sender, RoutedEventArgs e)
        {
            if (_panelManager == null
                || sender is not Button { Tag: PanelSelectionTag tag } button)
            {
                return;
            }

            button.IsEnabled = false;
            try
            {
                var isRunning = tag.IsDocked
                    ? _panelManager.IsDockedBarRunning(tag.Id)
                    : _panelManager.IsRunning(tag.Id);
                var isMinimized = isRunning && (tag.IsDocked
                    ? _panelManager.IsDockedBarMinimized(tag.Id)
                    : _panelManager.IsOverlayMinimized(tag.Id));

                if (!isRunning)
                {
                    return;
                }

                if (!isMinimized)
                {
                    if (tag.IsDocked)
                    {
                        _panelManager.MinimizeDockedBar(tag.Id);
                    }
                    else
                    {
                        _panelManager.MinimizeOverlay(tag.Id);
                    }
                }
                else if (tag.IsDocked)
                {
                    await _panelManager.ShowDockedBarAsync(tag.Id, activate: false);
                }
                else
                {
                    _panelManager.Show(tag.Id, activate: false);
                }
            }
            finally
            {
                RefreshPanelSelections();
            }
        }

        private async void OnPanelLaunchSelectionChanged(object sender, RoutedEventArgs e)
        {
            if (_isApplyingPanelSelections
                || _panelManager == null
                || sender is not CheckBox { Tag: PanelSelectionTag tag } checkBox)
            {
                return;
            }

            checkBox.IsEnabled = false;
            try
            {
                var enabled = checkBox.IsChecked == true;
                if (tag.IsDocked)
                {
                    await _panelManager.SetDockedBarEnabledAsync(tag.Id, enabled);
                }
                else
                {
                    await _panelManager.SetOverlayEnabledAsync(tag.Id, enabled);
                }
            }
            catch
            {
                RefreshPanelSelections();
            }
            finally
            {
                checkBox.IsEnabled = true;
                UpdateDesktopDisplayButtonAvailability();
            }
        }

    }
}
