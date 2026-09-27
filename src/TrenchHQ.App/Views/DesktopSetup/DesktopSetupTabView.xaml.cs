using TrenchHQ.ViewModels;
using TrenchHQ.Core.Panels;
using TrenchHQ.Core.Widgets;
using TrenchHQ.Core.Windows;
using TrenchHQ.Infrastructure.Panels;
using TrenchHQ.Infrastructure.Widgets;
using TrenchHQ.Infrastructure.Windows;
using TrenchHQ.Panels;
using TrenchHQ.Presentation;
using TrenchHQ.Interop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace TrenchHQ.Views
{
    public sealed partial class DesktopSetupTabView : UserControl
    {
        private DesktopPanelManager? _manager;
        private SavedWidgetCatalogService? _widgetCatalog;
        private DesktopPanelListItem[] _panels = [];
        private OverlayDefinition? _selectedOverlay;
        private DockedBarDefinition? _selectedDockedBar;
        private WindowsAppBarNative.DisplayMonitorInfo[] _monitors = [];
        private readonly List<ToggleButton> _positionButtons = [];
        private string _backgroundColor = "#000000";
        private string _textColor = "#F5F5F5";
        private bool _isApplying;
        private bool _isDirty;
        private bool _shortcutDialogOpen;
        private bool _shortcutCaptureFocused;
        private bool _savingShortcut;
        private PanelShortcutCaptureHook? _shortcutHook;
        private readonly PanelShortcutCapture _shortcutCapture = new();
        private string _shortcut = string.Empty;
        private string _shortcutBeforeCapture = string.Empty;

        public DesktopSetupTabView()
        {
            InitializeComponent();
            SaveStatusText.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
                SaveStatusText.Visibility = string.IsNullOrWhiteSpace(SaveStatusText.Text)
                    ? Visibility.Collapsed : Visibility.Visible);
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        internal void RefreshRuntimeState()
        {
            if (_manager == null || (_selectedOverlay == null && _selectedDockedBar == null))
            {
                ShowHideButton.IsEnabled = false;
                return;
            }

            var isRunning = _selectedDockedBar != null
                ? _manager.IsDockedBarRunning(_selectedDockedBar.Id)
                  && !_manager.IsDockedBarMinimized(_selectedDockedBar.Id)
                : _manager.IsRunning(_selectedOverlay!.Id)
                  && !_manager.IsOverlayMinimized(_selectedOverlay.Id);
            var hasContent = _selectedDockedBar != null
                ? _manager.HasDisplayContent(_selectedDockedBar)
                : _manager.HasDisplayContent(_selectedOverlay!);
            ShowHideButton.IsEnabled = isRunning || hasContent;
            ShowHideButton.Content = isRunning ? "Close" : "Show";
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (Application.Current is not App app)
            {
                return;
            }

            await app.EnsureInitializedAsync();
            _manager = app.PanelManager;
            _widgetCatalog = app.WidgetCatalog;
            _manager.DefinitionsChanged -= OnDefinitionsChanged;
            _manager.StateChanged -= OnRuntimeStateChanged;
            _manager.DisplayEnvironmentChanged -= OnDisplayEnvironmentChanged;
            _manager.DefinitionsChanged += OnDefinitionsChanged;
            _manager.StateChanged += OnRuntimeStateChanged;
            _manager.DisplayEnvironmentChanged += OnDisplayEnvironmentChanged;
            await RefreshPanelsAsync();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            CloseShortcutDialog();
            if (_manager != null)
            {
                _manager.DefinitionsChanged -= OnDefinitionsChanged;
                _manager.StateChanged -= OnRuntimeStateChanged;
                _manager.DisplayEnvironmentChanged -= OnDisplayEnvironmentChanged;
            }
        }

        private async void OnDefinitionsChanged(object? sender, EventArgs e)
        {
            if (_shortcutDialogOpen) return;
            await RefreshPanelsAsync(GetSelectedKey());
        }

        private void OnRuntimeStateChanged(object? sender, EventArgs e)
        {
            RefreshRuntimeState();
        }

        private void OnDisplayEnvironmentChanged(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                if (_manager == null)
                {
                    return;
                }

                if (!_isDirty && !_shortcutDialogOpen)
                {
                    await RefreshPanelsAsync(GetSelectedKey());
                    return;
                }

                _monitors = WindowsAppBarNative.GetMonitors().OrderBy(item => item.Number).ToArray();
                if (_selectedDockedBar != null)
                {
                    BuildMonitorDiagrams();
                }
            });
        }

        private async Task RefreshPanelsAsync(string? preferredKey = null)
        {
            if (_manager == null)
            {
                return;
            }

            preferredKey ??= GetSelectedKey();
            _monitors = WindowsAppBarNative.GetMonitors().OrderBy(item => item.Number).ToArray();
            var overlays = _manager.GetDefinitions();
            var dockedBars = _manager.GetDockedBarDefinitions();
            _panels =
            [
                .. overlays.Select(definition => new DesktopPanelListItem(
                    definition.Id,
                    false,
                    definition.Name)),
                .. dockedBars.Select(definition => new DesktopPanelListItem(
                    definition.Id,
                    true,
                    definition.Name))
            ];

            _isApplying = true;
            try
            {
                UpdateSidebarVisibility(_panels.Length > 0);
                PanelHeadingText.Text = $"Your panels ({_panels.Length})";
                PanelList.ItemsSource = _panels;
                PanelList.SelectedItem = _panels.FirstOrDefault(item => GetKey(item) == preferredKey)
                                         ?? _panels.FirstOrDefault();
            }
            finally
            {
                _isApplying = false;
            }

            if (PanelList.SelectedItem is DesktopPanelListItem selected)
            {
                await ApplySelectedPanelAsync(selected);
            }
            else
            {
                ApplyEmptyState();
            }
        }

        private async void OnPanelSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isApplying && PanelList.SelectedItem is DesktopPanelListItem selected)
            {
                await ApplySelectedPanelAsync(selected);
            }
        }


        private Task ApplySelectedPanelAsync(DesktopPanelListItem panel)
        {
            CloseShortcutDialog();
            if (_manager == null)
            {
                return Task.CompletedTask;
            }

            _selectedOverlay = null;
            _selectedDockedBar = null;
            _isApplying = true;
            try
            {
                EmptyState.Visibility = Visibility.Collapsed;
                PanelEditorHeader.Visibility = Visibility.Visible;
                EditorScrollViewer.Visibility = Visibility.Visible;
                SaveStatusText.Text = string.Empty;

                if (panel.IsDocked)
                {
                    var selected = _manager.GetDockedBarDefinitions().First(item =>
                        string.Equals(item.Id, panel.Id, StringComparison.OrdinalIgnoreCase));
                    _selectedDockedBar = DesktopSetupService.Clone(selected);
                    ApplyDockedBar(_selectedDockedBar);
                    ApplyWidgetSelection(_selectedDockedBar.WidgetIds);
                }
                else
                {
                    var selected = _manager.GetDefinitions().First(item =>
                        string.Equals(item.Id, panel.Id, StringComparison.OrdinalIgnoreCase));
                    _selectedOverlay = DesktopSetupService.Clone(selected);
                    ApplyOverlay(_selectedOverlay);
                    ApplyWidgetSelection(_selectedOverlay.WidgetIds);
                }
            }
            finally
            {
                _isApplying = false;
            }

            UpdateColorDisplays();
            UpdateOpacityText();
            RefreshRuntimeState();
            SetDirty(false);
            return Task.CompletedTask;
        }

        private void ApplyOverlay(OverlayDefinition definition)
        {
            PanelNameText.Text = definition.Name;
            SelectContentSize(definition.ContentSize);
            _backgroundColor = definition.BackgroundColor;
            _textColor = definition.TextColor;
            PriceFlashToggle.IsOn = definition.PriceFlashOnChange;
            LuminositySlider.Value = definition.LuminosityOpacity;
            TintSlider.Value = definition.TintOpacity;
            WidthSlider.Minimum = OverlayLayoutRules.MinimumWidth;
            WidthSlider.Value = definition.WidthDip;
            HeightSlider.Value = OverlayLayoutRules.ClampHeight(definition.HeightDip);
            WidthValueText.Text = $"{WidthSlider.Value:0} DIP";
            HeightValueText.Text = $"{HeightSlider.Value:0} DIP";
            _shortcut = definition.Shortcut;
            UpdateShortcutEditorState();
            OverlayOpacityPanel.Visibility = Visibility.Visible;
            OverlayPlacementPanel.Visibility = Visibility.Visible;
            DockedBarPlacementPanel.Visibility = Visibility.Collapsed;
        }

        private void ApplyDockedBar(DockedBarDefinition definition)
        {
            PanelNameText.Text = definition.Name;
            SelectContentSize(definition.ContentSize);
            _backgroundColor = definition.BackgroundColor;
            _textColor = definition.TextColor;
            PriceFlashToggle.IsOn = definition.PriceFlashOnChange;
            definition.MonitorDeviceName = WindowsAppBarNative.ResolveMonitorDeviceName(definition.MonitorDeviceName);
            definition.Edge = DockedBarLayoutRules.NormalizeEdge(definition.Edge);
            BuildMonitorDiagrams();
            _shortcut = definition.Shortcut;
            UpdateShortcutEditorState();
            OverlayOpacityPanel.Visibility = Visibility.Collapsed;
            OverlayPlacementPanel.Visibility = Visibility.Collapsed;
            DockedBarPlacementPanel.Visibility = Visibility.Visible;
        }

        private void ApplyEmptyState()
        {
            CloseShortcutDialog();
            _selectedOverlay = null;
            _selectedDockedBar = null;
            EmptyState.Visibility = Visibility.Visible;
            PanelEditorHeader.Visibility = Visibility.Collapsed;
            EditorScrollViewer.Visibility = Visibility.Collapsed;
            PanelHeadingText.Text = "Your panels (0)";
            SetDirty(false);
        }

        private void UpdateSidebarVisibility(bool hasPanels)
        {
            PanelSidebarColumn.Width = hasPanels ? new GridLength(240) : new GridLength(0);
            PanelSidebar.Visibility = hasPanels ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void OnAddOverlayClick(object sender, RoutedEventArgs e)
        {
            if (_manager == null)
            {
                return;
            }

            var created = await _manager.AddAsync();
            await RefreshPanelsAsync(GetKey(created.Id, false));
        }

        private async void OnAddDockedBarClick(object sender, RoutedEventArgs e)
        {
            if (_manager == null)
            {
                return;
            }

            var created = await _manager.AddDockedBarAsync();
            if (created == null)
            {
                await ShowMessageAsync("No free monitor edge", "Each detected monitor edge already has a configured TrenchHQ panel.");
                return;
            }

            await RefreshPanelsAsync(GetKey(created.Id, true));
        }

        private async void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            if (_manager == null || (_selectedOverlay == null && _selectedDockedBar == null))
            {
                return;
            }

            var name = _selectedDockedBar?.Name ?? _selectedOverlay!.Name;
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Delete panel?",
                Content = $"{name} and its saved configuration will be removed.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            if (_selectedDockedBar != null)
            {
                await _manager.DeleteDockedBarAsync(_selectedDockedBar.Id);
            }
            else
            {
                await _manager.DeleteAsync(_selectedOverlay!.Id);
            }

            await RefreshPanelsAsync();
        }

        private async void OnShowHideClick(object sender, RoutedEventArgs e)
        {
            if (_manager == null)
            {
                return;
            }

            if (_selectedDockedBar != null)
            {
                if (_manager.IsDockedBarRunning(_selectedDockedBar.Id)
                    && !_manager.IsDockedBarMinimized(_selectedDockedBar.Id))
                {
                    _manager.CloseDockedBar(_selectedDockedBar.Id);
                }
                else if (!await _manager.ShowDockedBarAsync(_selectedDockedBar.Id))
                {
                    await ShowMessageAsync("Edge already in use", "Close the TrenchHQ panel already running on this monitor edge first.");
                }
            }
            else if (_selectedOverlay != null)
            {
                if (_manager.IsRunning(_selectedOverlay.Id)
                    && !_manager.IsOverlayMinimized(_selectedOverlay.Id))
                {
                    _manager.Close(_selectedOverlay.Id);
                }
                else
                {
                    _manager.Show(_selectedOverlay.Id);
                }
            }
        }

        private async void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (_manager == null)
            {
                return;
            }

            var content = PanelContentSelector.SelectedItem as PanelContentOption;
            var automaticTickerThickness = UsesAutomaticTickerThickness(content);
            if (_selectedDockedBar != null)
            {
                _monitors = WindowsAppBarNative.GetMonitors().OrderBy(item => item.Number).ToArray();
                if (!_monitors.Any(item => string.Equals(item.DeviceName, _selectedDockedBar.MonitorDeviceName, StringComparison.OrdinalIgnoreCase)))
                {
                    BuildMonitorDiagrams();
                    await ShowMessageAsync("Monitor unavailable", "Choose an available monitor edge before saving. Your changes have not been applied.");
                    return;
                }
                if (!automaticTickerThickness)
                {
                    UpdateThicknessBounds(_selectedDockedBar.Edge, (int)Math.Round(ThicknessSlider.Value));
                    if (ThicknessUnavailableText.Visibility == Visibility.Visible) return;
                }
            }

            var shortcut = _shortcut;
            var panelId = _selectedDockedBar?.Id ?? _selectedOverlay?.Id;
            var isDocked = _selectedDockedBar != null;
            if (panelId == null || Application.Current is not App app || app.PanelShortcuts == null)
            {
                return;
            }

            var shortcutResult = app.PanelShortcuts.CanAssign(shortcut, panelId, isDocked);
            if (shortcutResult != PanelShortcutAssignmentResult.Available)
            {
                var (title, message) = shortcutResult switch
                {
                    PanelShortcutAssignmentResult.Invalid =>
                        ("Invalid shortcut", "Record Ctrl+Alt or Ctrl+Shift plus A-Z, 0-9, or F1-F11."),
                    PanelShortcutAssignmentResult.Unsafe =>
                        ("Shortcut could interfere with typing", "Use Ctrl with Alt or Shift so the shortcut cannot be triggered by ordinary typing or a single common modifier."),
                    PanelShortcutAssignmentResult.Reserved =>
                        ("Shortcut reserved by Windows", "Windows-key shortcuts and F12 are unavailable for TrenchHQ panel shortcuts."),
                    PanelShortcutAssignmentResult.Duplicate =>
                        ("Shortcut already assigned", "Choose a shortcut that is not used by another panel."),
                    _ =>
                        ("Shortcut unavailable", "Another application or Windows is already using this shortcut.")
                };
                await ShowMessageAsync(title, message);
                return;
            }

            shortcut = PanelShortcutRules.Normalize(shortcut);
            var widgetId = content?.Widget?.Id;
            if (_selectedDockedBar != null)
            {
                if (!automaticTickerThickness)
                {
                    _selectedDockedBar.ThicknessPx = (int)Math.Round(ThicknessSlider.Value);
                }
                _selectedDockedBar.BackgroundColor = _backgroundColor;
                _selectedDockedBar.TextColor = _textColor;
                _selectedDockedBar.ContentSize = GetSelectedContentSize();
                _selectedDockedBar.PriceFlashOnChange = PriceFlashToggle.IsOn;
                _selectedDockedBar.Shortcut = shortcut;
                _selectedDockedBar.WidgetIds = CreateWidgetReferences(widgetId);
                _selectedDockedBar.UseApplicationWindow = content?.IsApplicationWindow == true;
                if (_selectedDockedBar.UseApplicationWindow) _selectedDockedBar.WidgetIds = [];
                if (!_selectedDockedBar.UseApplicationWindow && _selectedDockedBar.WidgetIds.Length == 0)
                    _selectedDockedBar.Enabled = false;
                if (!await _manager.SaveDockedBarDefinitionAsync(_selectedDockedBar))
                {
                    await ShowMessageAsync("Edge already configured", "Choose an available edge in the monitor diagram.");
                    return;
                }
            }
            else if (_selectedOverlay != null)
            {
                _selectedOverlay.WidthDip = WidthSlider.Value;
                _selectedOverlay.BackgroundColor = _backgroundColor;
                _selectedOverlay.TextColor = _textColor;
                _selectedOverlay.ContentSize = GetSelectedContentSize();
                _selectedOverlay.LuminosityOpacity = LuminositySlider.Value;
                _selectedOverlay.TintOpacity = TintSlider.Value;
                _selectedOverlay.PriceFlashOnChange = PriceFlashToggle.IsOn;
                _selectedOverlay.Shortcut = shortcut;
                _selectedOverlay.WidgetIds = CreateWidgetReferences(widgetId);
                _selectedOverlay.UseApplicationWindow = content?.IsApplicationWindow == true;
                _selectedOverlay.HeightDip = HeightSlider.Value;
                if (_selectedOverlay.UseApplicationWindow) _selectedOverlay.WidgetIds = [];
                if (!_selectedOverlay.UseApplicationWindow && _selectedOverlay.WidgetIds.Length == 0)
                    _selectedOverlay.Enabled = false;
                await _manager.SaveDefinitionAsync(_selectedOverlay);
            }

            if (shortcut.Length > 0 && !app.PanelShortcuts.IsRegistered(panelId, isDocked, shortcut))
            {
                await _manager.ClearShortcutAsync(panelId, isDocked);
                await ShowMessageAsync(
                    "Shortcut could not be activated",
                    "Windows or another application claimed this shortcut while the panel was being saved. TrenchHQ cleared the shortcut; record a different one.");
                await RefreshPanelsAsync(GetKey(panelId, isDocked));
                return;
            }

            SaveStatusText.Text = "Saved";
            await RefreshPanelsAsync(GetSelectedKey());
        }

        private async void OnCancelClick(object sender, RoutedEventArgs e)
        {
            if (PanelList.SelectedItem is DesktopPanelListItem selected)
            {
                await ApplySelectedPanelAsync(selected);
            }
        }

        private async void OnEditShortcutClick(object sender, RoutedEventArgs e)
        {
            if (_shortcutDialogOpen || Application.Current is not App app || app.PanelShortcuts == null) return;
            _shortcutBeforeCapture = _shortcut;
            _shortcutCapture.Set(_shortcut);
            _shortcutDialogOpen = true;
            try
            {
                ShortcutDialog.XamlRoot = XamlRoot;
                app.PanelShortcuts.BeginCapture();
                var window = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Current);
                _shortcutHook = new PanelShortcutCaptureHook(window,
                    () => _shortcutDialogOpen && _shortcutCaptureFocused && !_savingShortcut,
                    (key, down) => DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_shortcutDialogOpen) OnCapturedKey(key, down);
                    }));
                UpdateShortcutValidation();
                await ShortcutDialog.ShowAsync();
            }
            catch (Exception)
            {
                SaveStatusText.Text = "Shortcut editor could not open. Close other dialogs and try again.";
            }
            finally
            {
                _shortcutHook?.Dispose();
                _shortcutHook = null;
                _shortcutDialogOpen = false;
                _shortcutCaptureFocused = false;
                app.PanelShortcuts.EndCapture();
                UpdateShortcutEditorState();
            }
        }

        private void OnShortcutDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            ShortcutCaptureBox.Focus(FocusState.Programmatic);
        }

        private void OnShortcutCaptureFocused(object sender, RoutedEventArgs e)
        {
            _shortcutCaptureFocused = true;
            // Forget held-key state across focus changes; keep the candidate visible.
            _shortcutCapture.ResetHeldKeys();
        }

        private void OnShortcutCaptureBlurred(object sender, RoutedEventArgs e) => _shortcutCaptureFocused = false;

        private void OnCapturedKey(uint key, bool down)
        {
            if (!down)
            {
                _shortcutCapture.Release(key);
                return;
            }
            if (key == (uint)VirtualKey.Escape)
            {
                ShortcutDialog.Hide();
                return;
            }
            _shortcutCapture.Press(key);
            UpdateShortcutValidation();
        }

        private void OnResetShortcutClick(object sender, RoutedEventArgs e)
        {
            if (_savingShortcut) return;
            _shortcutCapture.Set(_shortcutBeforeCapture);
            UpdateShortcutValidation();
            ShortcutCaptureBox.Focus(FocusState.Programmatic);
        }

        private void OnClearShortcutClick(object sender, RoutedEventArgs e)
        {
            if (_savingShortcut) return;
            _shortcutCapture.Set(string.Empty);
            UpdateShortcutValidation();
            ShortcutCaptureBox.Focus(FocusState.Programmatic);
        }

        private bool UpdateShortcutValidation()
        {
            ShortcutCaptureBox.Text = _shortcutCapture.Preview.Replace("+", " + ");
            var id = _selectedDockedBar?.Id ?? _selectedOverlay?.Id;
            var candidate = _shortcutCapture.Shortcut;
            var result = id != null && Application.Current is App app && app.PanelShortcuts != null
                ? app.PanelShortcuts.CanAssign(candidate ?? _shortcutCapture.Preview, id, _selectedDockedBar != null)
                : PanelShortcutAssignmentResult.Unavailable;
            var valid = candidate != null && result == PanelShortcutAssignmentResult.Available;
            ShortcutDialog.IsPrimaryButtonEnabled = valid && !_savingShortcut;
            ShortcutValidationText.Text = result switch
            {
                PanelShortcutAssignmentResult.Available when valid && candidate!.Length == 0 => "No shortcut will be assigned.",
                PanelShortcutAssignmentResult.Available when valid => "Available.",
                PanelShortcutAssignmentResult.Duplicate => "Another TrenchHQ panel already uses this shortcut.",
                PanelShortcutAssignmentResult.Unavailable => "Windows or another application already uses this shortcut.",
                PanelShortcutAssignmentResult.Reserved => "Windows-key shortcuts and F12 are reserved.",
                PanelShortcutAssignmentResult.Unsafe => "Hold Ctrl with Alt or Shift to avoid interfering with ordinary typing.",
                _ => "Use Ctrl with Alt or Shift and one letter, number or F1-F11 key."
            };
            if (valid && candidate!.Contains("Ctrl+Alt"))
                ShortcutValidationText.Text += " Ctrl+Alt can overlap AltGr typing; prefer Ctrl+Shift if you use AltGr.";
            ShortcutValidationText.Foreground = new SolidColorBrush(valid
                ? UiPalette.Success : UiPalette.Warning);
            return valid;
        }

        private async void OnShortcutDialogSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (!UpdateShortcutValidation() || _manager == null || Application.Current is not App app || app.PanelShortcuts == null) { args.Cancel = true; return; }
            var id = _selectedDockedBar?.Id ?? _selectedOverlay!.Id;
            var isDocked = _selectedDockedBar != null;
            var candidate = _shortcutCapture.Shortcut!;
            var deferral = args.GetDeferral();
            _savingShortcut = true;
            sender.IsPrimaryButtonEnabled = false;
            try
            {
                await _manager.SetShortcutAsync(id, isDocked, candidate);
                app.PanelShortcuts.EndCapture();
                if (!app.PanelShortcuts.IsRegistered(id, isDocked, candidate))
                {
                    await _manager.SetShortcutAsync(id, isDocked, _shortcutBeforeCapture);
                    app.PanelShortcuts.BeginCapture();
                    ShortcutValidationText.Text = "This shortcut was just claimed by another application. Choose another.";
                    args.Cancel = true;
                    return;
                }
                _shortcut = candidate;
                if (_selectedDockedBar != null) _selectedDockedBar.Shortcut = candidate;
                if (_selectedOverlay != null) _selectedOverlay.Shortcut = candidate;
                SaveStatusText.Text = "Shortcut saved";
            }
            catch (Exception)
            {
                // Restore the in-memory assignment as well as the saved value where possible.
                try { await _manager.SetShortcutAsync(id, isDocked, _shortcutBeforeCapture); } catch { }
                args.Cancel = true;
                ShortcutValidationText.Text = "Could not save the shortcut. Try again.";
            }
            finally
            {
                _savingShortcut = false;
                sender.IsPrimaryButtonEnabled = _shortcutCapture.Shortcut != null;
                deferral.Complete();
            }
        }

        private void OnShortcutDialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            if (_savingShortcut) args.Cancel = true;
        }

        private void CloseShortcutDialog()
        {
            if (!_shortcutDialogOpen) return;
            ShortcutDialog.Hide();
            _shortcutHook?.Dispose();
            _shortcutHook = null;
            _shortcutCaptureFocused = false;
            if (Application.Current is App app) app.PanelShortcuts?.EndCapture();
        }

        private void UpdateShortcutEditorState()
        {
            ShortcutButtonText.Text = _shortcut.Length == 0 ? "Set shortcut" : _shortcut.Replace("+", " + ");
            AutomationProperties.SetName(ShortcutEditButton, "Edit panel shortcut: " + (_shortcut.Length == 0 ? "None" : ShortcutButtonText.Text));
        }

        private void OnPanelContentChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isApplying) return;
            UpdatePanelContentVisibility();
            SetDirty(true);
        }

        private void UpdatePanelContentVisibility()
        {
            var content = PanelContentSelector.SelectedItem as PanelContentOption;
            var isApplication = content?.IsApplicationWindow == true;
            var showContentSize = !isApplication
                                  && content?.Widget != null
                                  && content.Widget.Type != PanelWidgetTypes.Website;
            ContentSizeLabel.Visibility = showContentSize ? Visibility.Visible : Visibility.Collapsed;
            ContentSizeSelector.Visibility = showContentSize ? Visibility.Visible : Visibility.Collapsed;
            HeightPanel.Visibility = _selectedOverlay != null && OverlayLayoutRules.HasAdjustableHeight(isApplication, content?.Widget?.Type)
                ? Visibility.Visible : Visibility.Collapsed;
            WidthSlider.Minimum = OverlayLayoutRules.MinimumWidthFor(content?.Widget?.Type);
            var showPriceFlash = content?.Widget?.Type == PanelWidgetTypes.PriceTicker;
            PriceFlashLabel.Visibility = showPriceFlash ? Visibility.Visible : Visibility.Collapsed;
            PriceFlashToggle.Visibility = showPriceFlash ? Visibility.Visible : Visibility.Collapsed;
            var automaticTickerThickness = UsesAutomaticTickerThickness(content);
            ThicknessPanel.Visibility = _selectedDockedBar != null && !automaticTickerThickness
                ? Visibility.Visible : Visibility.Collapsed;
            if (_selectedDockedBar != null && !automaticTickerThickness)
                UpdateThicknessBounds(_selectedDockedBar.Edge, _isApplying ? _selectedDockedBar.ThicknessPx : (int)Math.Round(ThicknessSlider.Value));
            else if (_selectedDockedBar != null)
            {
                ThicknessUnavailableText.Visibility = Visibility.Collapsed;
                UpdateSaveActionState();
            }
            else UpdateSaveActionState();
        }

        private static bool UsesAutomaticTickerThickness(PanelContentOption? content) =>
            content?.IsApplicationWindow != true
            && content?.Widget?.Type == PanelWidgetTypes.PriceTicker;

        private void SelectContentSize(string? value)
        {
            var normalized = PanelContentSizeRules.Normalize(value);
            ContentSizeSelector.SelectedItem = ContentSizeSelector.Items
                .OfType<ComboBoxItem>()
                .First(item => string.Equals(item.Tag as string, normalized, StringComparison.Ordinal));
        }

        private string GetSelectedContentSize() => PanelContentSizeRules.Normalize(
            (ContentSizeSelector.SelectedItem as ComboBoxItem)?.Tag as string);

        private void ApplyWidgetSelection(string[]? widgetIds)
        {
            var options = (_widgetCatalog?.GetWidgets() ?? [])
                .Where(widget => PanelWidgetHostRules.SupportsWidget(
                    widget.Type,
                    _selectedDockedBar == null
                        ? PanelWidgetHostRules.ForOverlay()
                        : PanelWidgetHostRules.ForDockedBar(_selectedDockedBar.Edge)))
                .Select(widget => new PanelContentOption(widget.Name, widget))
                .ToList();
            options.Add(new PanelContentOption("Application window", null));
            PanelContentSelector.ItemsSource = options;
            var selectedId = (widgetIds ?? []).FirstOrDefault();
            PanelContentSelector.SelectedItem = (_selectedDockedBar?.UseApplicationWindow ?? _selectedOverlay?.UseApplicationWindow) == true
                ? options.First(option => option.IsApplicationWindow)
                : options.FirstOrDefault(option => option.Widget != null &&
                    string.Equals(option.Widget.Id, selectedId, StringComparison.OrdinalIgnoreCase));
            UpdatePanelContentVisibility();
        }

        private async void OnBackgroundColorClick(object sender, RoutedEventArgs e)
        {
            BackgroundColorDialog.XamlRoot ??= XamlRoot;
            BackgroundColorPicker.Color = ParseColor(_backgroundColor, Color.FromArgb(255, 0, 0, 0));
            if (await BackgroundColorDialog.ShowAsync() == ContentDialogResult.Primary)
            {
                _backgroundColor = ToHex(BackgroundColorPicker.Color);
                UpdateColorDisplays();
                SetDirty(true);
            }
        }

        private async void OnTextColorClick(object sender, RoutedEventArgs e)
        {
            TextColorDialog.XamlRoot ??= XamlRoot;
            TextColorPicker.Color = ParseColor(_textColor, Color.FromArgb(255, 245, 245, 245));
            if (await TextColorDialog.ShowAsync() == ContentDialogResult.Primary)
            {
                _textColor = ToHex(TextColorPicker.Color);
                UpdateColorDisplays();
                SetDirty(true);
            }
        }

        private void UpdateColorDisplays()
        {
            BackgroundColorText.Text = _backgroundColor;
            TextColorText.Text = _textColor;
            BackgroundColorPreview.Background = new SolidColorBrush(ParseColor(_backgroundColor, Color.FromArgb(255, 0, 0, 0)));
            TextColorPreview.Background = new SolidColorBrush(ParseColor(_textColor, Color.FromArgb(255, 245, 245, 245)));
        }

        private void OnWidthChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (WidthValueText != null)
            {
                WidthValueText.Text = $"{e.NewValue:0} DIP";
            }
            if (!_isApplying) SetDirty(true);
        }

        private void OnHeightChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (HeightValueText != null) HeightValueText.Text = $"{e.NewValue:0} DIP";
            if (!_isApplying) SetDirty(true);
        }

        private void OnOpacityChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            UpdateOpacityText();
            if (!_isApplying) SetDirty(true);
        }

        private void OnEditorValueChanged(object sender, RoutedEventArgs e)
        {
            if (!_isApplying) SetDirty(true);
        }

        private void UpdateOpacityText()
        {
            if (LuminosityValueText != null && TintValueText != null)
            {
                LuminosityValueText.Text = $"{LuminositySlider.Value:0}%";
                TintValueText.Text = $"{TintSlider.Value:0}%";
            }
        }

        private void BuildMonitorDiagrams()
        {
            MonitorDiagramPanel.Children.Clear();
            _positionButtons.Clear();
            foreach (var monitor in _monitors)
            {
                var screen = new Grid { Width = 224, Height = 152, Padding = new Thickness(4) };
                foreach (var size in new[] { new GridLength(32), new GridLength(1, GridUnitType.Star), new GridLength(32) })
                {
                    screen.RowDefinitions.Add(new RowDefinition { Height = size });
                    screen.ColumnDefinitions.Add(new ColumnDefinition { Width = size });
                }
                var label = new TextBlock
                {
                    Text = $"Monitor {monitor.Number}" + (monitor.IsPrimary ? "\nPrimary" : string.Empty),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                };
                Grid.SetRow(label, 1);
                Grid.SetColumn(label, 1);
                screen.Children.Add(label);
                foreach (var edge in DockedBarLayoutRules.Edges)
                {
                    var position = new PanelPosition(monitor, edge);
                    var button = new ToggleButton
                    {
                        Tag = position, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
                        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
                        CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(2)
                    };
                    button.Click += OnPositionClick;
                    button.GotFocus += (_, _) => ShowPositionHint(button);
                    button.LostFocus += (_, _) => ShowSelectedPosition();
                    button.PointerEntered += (_, _) => ShowPositionHint(button);
                    button.PointerExited += (_, _) => ShowSelectedPosition();
                    // The wrapper also exposes occupied-edge hover text while its button is disabled.
                    var target = new Border { Child = button, Margin = new Thickness(2), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
                    target.PointerEntered += (_, _) => ShowPositionHint(button);
                    target.PointerExited += (_, _) => ShowSelectedPosition();
                    Grid.SetRow(target, edge == "Top" ? 0 : edge == "Bottom" ? 2 : 1);
                    Grid.SetColumn(target, edge == "Left" ? 0 : edge == "Right" ? 2 : 1);
                    screen.Children.Add(target);
                    _positionButtons.Add(button);
                }
                var frameBrush = new SolidColorBrush(Color.FromArgb(255, 115, 125, 140));
                var diagram = new StackPanel { Spacing = 0 };
                diagram.Children.Add(new Border { Child = screen, BorderThickness = new Thickness(2), BorderBrush = frameBrush, CornerRadius = new CornerRadius(8) });
                diagram.Children.Add(new Border { Width = 16, Height = 8, Background = frameBrush, HorizontalAlignment = HorizontalAlignment.Center });
                diagram.Children.Add(new Border { Width = 64, Height = 4, Background = frameBrush, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Center });
                MonitorDiagramPanel.Children.Add(diagram);
            }
            UpdatePositionButtons();
        }

        private void UpdatePositionButtons()
        {
            if (_selectedDockedBar == null || _manager == null) return;
            var occupied = _manager.GetDockedBarDefinitions().Select(item => new DockedBarOccupiedPlacement(
                item.Id, WindowsAppBarNative.ResolveMonitorDeviceName(item.MonitorDeviceName), item.Edge)).ToArray();
            foreach (var button in _positionButtons)
            {
                var position = (PanelPosition)button.Tag;
                var selected = string.Equals(position.Monitor.DeviceName, _selectedDockedBar.MonitorDeviceName, StringComparison.OrdinalIgnoreCase)
                    && position.Edge == _selectedDockedBar.Edge;
                var unavailable = DockedBarPlacementRules.IsOccupied(occupied, _selectedDockedBar.Id, position.Monitor.DeviceName, position.Edge);
                button.IsChecked = selected;
                button.IsEnabled = !unavailable;
                var accent = (SolidColorBrush)Application.Current.Resources["AccentBrush"];
                button.BorderBrush = (SolidColorBrush)Application.Current.Resources[unavailable ? "TextMutedBrush" : "AccentBrush"];
                button.Background = selected ? accent : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                button.Foreground = (SolidColorBrush)Application.Current.Resources[selected ? "AccentTextBrush" : "TextSecondaryBrush"];
                button.Content = unavailable ? new FontIcon { Glyph = "\uE72E", FontSize = 14 }
                    : selected ? new SymbolIcon(Symbol.Accept) { Width = 16, Height = 16 } : null;
                var description = PositionDescription(button);
                AutomationProperties.SetName(button, description);
                ToolTipService.SetToolTip(button, description);
            }
            ShowSelectedPosition();
        }

        private static string PositionDescription(ToggleButton button)
        {
            var position = (PanelPosition)button.Tag;
            return $"Monitor {position.Monitor.Number} - {position.Edge}"
                + (!button.IsEnabled ? " - Already in use" : button.IsChecked == true ? " - Selected" : " - Available");
        }

        private void ShowPositionHint(ToggleButton button) => PositionStatusText.Text = PositionDescription(button);

        private void ShowSelectedPosition()
        {
            var selected = _positionButtons.FirstOrDefault(button => button.IsChecked == true);
            PositionStatusText.Text = selected == null ? "Connect a monitor to choose a position." : PositionDescription(selected);
        }

        private void OnPositionClick(object sender, RoutedEventArgs e)
        {
            if (_selectedDockedBar == null || sender is not ToggleButton button) return;
            var position = (PanelPosition)button.Tag;
            _selectedDockedBar.MonitorDeviceName = position.Monitor.DeviceName;
            _selectedDockedBar.Edge = position.Edge;
            _selectedDockedBar.Name = WindowsAppBarNative.GetPanelName(position.Edge, position.Monitor.DeviceName, _monitors);
            PanelNameText.Text = _selectedDockedBar.Name;
            if (!UsesAutomaticTickerThickness(PanelContentSelector.SelectedItem as PanelContentOption))
            {
                UpdateThicknessBounds(position.Edge, (int)Math.Round(ThicknessSlider.Value));
            }
            UpdatePositionButtons();
            SetDirty(true);
        }

        private void UpdateThicknessBounds(string edge, int value)
        {
            var monitorName = _selectedDockedBar?.MonitorDeviceName;
            var monitor = WindowsAppBarNative.GetMonitorRect(monitorName, IntPtr.Zero);
            var span = DockedBarLayoutRules.IsHorizontal(edge)
                ? monitor.bottom - monitor.top : monitor.right - monitor.left;
            var maximum = DockedBarLayoutRules.GetMaximumThickness(span);
            var dpi = WindowsAppBarNative.GetMonitorScale(monitorName);
            var textScale = new global::Windows.UI.ViewManagement.UISettings().TextScaleFactor;
            var minimum = DockedBarLayoutRules.GetRuntimeThickness(edge, DockedBarLayoutRules.GetMinimumThickness(edge), textScale, span, dpi);
            var content = PanelContentSelector.SelectedItem as PanelContentOption;
            var application = content?.IsApplicationWindow == true;
            var needsContentMinimum = application || content?.Widget?.Type == PanelWidgetTypes.Website;
            var required = application ? DockedBarLayoutRules.MinimumApplicationWindowSize(dpi) : WebsiteWidgetRules.MinimumDockedSize(dpi);
            var contentMinimum = DockedBarLayoutRules.IsHorizontal(edge) ? required.Height : required.Width;
            var crossSpan = DockedBarLayoutRules.IsHorizontal(edge) ? monitor.right - monitor.left : monitor.bottom - monitor.top;
            var crossMinimum = DockedBarLayoutRules.IsHorizontal(edge) ? required.Width : required.Height;
            ThicknessUnavailableText.Text = application
                ? "Application window cannot fit on this edge within the size limit."
                : "Website cannot fit on this edge within the size limit.";
            ThicknessUnavailableText.Visibility = needsContentMinimum && (contentMinimum > maximum || crossMinimum > crossSpan)
                ? Visibility.Visible : Visibility.Collapsed;
            UpdateSaveActionState();
            var contentFits = contentMinimum <= maximum && crossMinimum <= crossSpan;
            if (needsContentMinimum && contentFits) minimum = Math.Max(minimum, contentMinimum);
            ThicknessSlider.IsEnabled = (!needsContentMinimum || contentFits) && minimum < maximum;
            // Lower Minimum first so a smaller monitor cannot leave an invalid slider range.
            ThicknessSlider.Minimum = Math.Min(ThicknessSlider.Minimum, minimum);
            ThicknessSlider.Maximum = maximum;
            ThicknessSlider.Minimum = minimum;
            ThicknessSlider.TickFrequency = Math.Max(1, maximum - minimum);
            ThicknessSlider.Value = Math.Clamp(value, minimum, maximum);
            ThicknessValueText.Text = $"{ThicknessSlider.Value:0} px";
            ThicknessMinimumText.Text = $"{minimum} px";
            ThicknessMaximumText.Text = $"{maximum} px";
        }

        private void OnThicknessChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (ThicknessValueText != null)
            {
                ThicknessValueText.Text = $"{e.NewValue:0} px";
            }
            if (!_isApplying) SetDirty(true);
        }

        private void OnPanelEditorToolbarSizeChanged(object sender, SizeChangedEventArgs e)
        {
            var compact = e.NewSize.Width < 500;
            Grid.SetRow(PanelEditorActions, compact ? 1 : 0);
            Grid.SetColumn(PanelEditorActions, compact ? 0 : 1);
            Grid.SetColumnSpan(PanelEditorActions, compact ? 2 : 1);
            Grid.SetColumnSpan(PanelNameText, compact ? 2 : 1);
            PanelEditorActions.Margin = new Thickness(0, compact ? 8 : 0, 0, 0);
            var labelWidth = new GridLength(e.NewSize.Width < 420 ? 120 : 176);
            ContentLabelColumn.Width = labelWidth;
            ShortcutLabelColumn.Width = labelWidth;
            AppearanceLabelColumn.Width = labelWidth;
        }

        private void SetDirty(bool isDirty)
        {
            _isDirty = isDirty;
            if (SaveStatusText != null)
            {
                SaveStatusText.Text = isDirty ? "Unsaved changes" : string.Empty;
            }
            UpdateSaveActionState();
        }

        private void UpdateSaveActionState()
        {
            if (SavePanelButton == null || CancelPanelButton == null)
            {
                return;
            }

            var canSave = _isDirty && ThicknessUnavailableText.Visibility != Visibility.Visible;
            SavePanelButton.IsEnabled = canSave;
            CancelPanelButton.IsEnabled = _isDirty;
        }

        private static string[] CreateWidgetReferences(string? widgetId)
        {
            return string.IsNullOrWhiteSpace(widgetId) ? [] : [widgetId];
        }

        private string? GetSelectedKey()
        {
            return _selectedDockedBar != null
                ? GetKey(_selectedDockedBar.Id, true)
                : _selectedOverlay != null
                    ? GetKey(_selectedOverlay.Id, false)
                    : null;
        }

        private static string GetKey(DesktopPanelListItem item) => GetKey(item.Id, item.IsDocked);

        private static string GetKey(string id, bool isDocked) => $"{(isDocked ? "bar" : "overlay")}:{id}";

        private static Color ParseColor(string? value, Color fallback)
        {
            var text = value?.Trim().TrimStart('#');
            if (text?.Length == 6
                && byte.TryParse(text[..2], System.Globalization.NumberStyles.HexNumber, null, out var red)
                && byte.TryParse(text.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var green)
                && byte.TryParse(text.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var blue))
            {
                return Color.FromArgb(255, red, green, blue);
            }

            return fallback;
        }

        private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        private async Task ShowMessageAsync(string title, string message)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = message,
                CloseButtonText = "OK"
            };
            await dialog.ShowAsync();
        }
    }
}
