using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shell;
using Flip.Core;
using Flip.Routing;

namespace Flip.SettingsHub;

/// <summary>
/// Helper per costruire elementi UI stilizzati con tema scuro moderno per SettingsHub.
/// </summary>
public static class HubUi
{
    public static readonly SolidColorBrush BgDark = new(Color.FromRgb(0x12, 0x13, 0x18));
    public static readonly SolidColorBrush SidebarBg = new(Color.FromRgb(0x1A, 0x1C, 0x23));
    public static readonly SolidColorBrush CardBg = new(Color.FromRgb(0x22, 0x25, 0x30));
    public static readonly SolidColorBrush CardBorder = new(Color.FromRgb(0x2E, 0x32, 0x40));
    public static readonly SolidColorBrush Accent = new(Color.FromRgb(0xFF, 0x7A, 0x18));
    public static readonly SolidColorBrush AccentHover = new(Color.FromRgb(0xFF, 0x6B, 0x00));
    public static readonly SolidColorBrush TextPrimary = new(Color.FromRgb(0xF3, 0xF4, 0xF6));
    public static readonly SolidColorBrush TextSecondary = new(Color.FromRgb(0x9C, 0xA3, 0xAF));
    public static readonly SolidColorBrush TextMuted = new(Color.FromRgb(0xA0, 0xA7, 0xB5));
    public static readonly SolidColorBrush InputBg = new(Color.FromRgb(0x16, 0x18, 0x20));
    public static readonly SolidColorBrush Danger = new(Color.FromRgb(0xEF, 0x44, 0x44));
    public static readonly SolidColorBrush Success = new(Color.FromRgb(0x10, 0xB9, 0x81));

    internal static readonly Lazy<ResourceDictionary> DarkControls = new(() => (ResourceDictionary)XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Style x:Key="HubScrollBar" TargetType="ScrollBar">
                <Setter Property="OverridesDefaultStyle" Value="True"/>
                <Setter Property="Stylus.IsPressAndHoldEnabled" Value="False"/>
                <Setter Property="Stylus.IsFlicksEnabled" Value="False"/>
                <Style.Triggers>
                    <Trigger Property="Orientation" Value="Vertical">
                        <Setter Property="Width" Value="10"/>
                        <Setter Property="MinWidth" Value="10"/>
                        <Setter Property="Background" Value="#121318"/>
                        <Setter Property="Template">
                            <Setter.Value>
                                <ControlTemplate TargetType="ScrollBar">
                                    <Border Background="#121318" Padding="1,2">
                                        <Track x:Name="PART_Track" Orientation="Vertical" IsDirectionReversed="True"
                                               Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}"
                                               ViewportSize="{TemplateBinding ViewportSize}"
                                               Value="{Binding Value, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                                            <Track.Resources>
                                                <Style TargetType="RepeatButton">
                                                    <Setter Property="Focusable" Value="False"/>
                                                    <Setter Property="OverridesDefaultStyle" Value="True"/>
                                                    <Setter Property="Template">
                                                        <Setter.Value>
                                                            <ControlTemplate TargetType="RepeatButton">
                                                                <Border Background="Transparent"/>
                                                            </ControlTemplate>
                                                        </Setter.Value>
                                                    </Setter>
                                                </Style>
                                            </Track.Resources>
                                            <Track.DecreaseRepeatButton><RepeatButton Command="{x:Static ScrollBar.PageUpCommand}"/></Track.DecreaseRepeatButton>
                                            <Track.Thumb>
                                                <Thumb>
                                                    <Thumb.Template>
                                                        <ControlTemplate TargetType="Thumb">
                                                            <Border x:Name="Grip" Background="#2E323E" CornerRadius="4"/>
                                                            <ControlTemplate.Triggers>
                                                                <Trigger Property="IsMouseOver" Value="True">
                                                                    <Setter TargetName="Grip" Property="Background" Value="#43495B"/>
                                                                </Trigger>
                                                                <Trigger Property="IsDragging" Value="True">
                                                                    <Setter TargetName="Grip" Property="Background" Value="#FF7A18"/>
                                                                </Trigger>
                                                            </ControlTemplate.Triggers>
                                                        </ControlTemplate>
                                                    </Thumb.Template>
                                                </Thumb>
                                            </Track.Thumb>
                                            <Track.IncreaseRepeatButton><RepeatButton Command="{x:Static ScrollBar.PageDownCommand}"/></Track.IncreaseRepeatButton>
                                        </Track>
                                    </Border>
                                </ControlTemplate>
                            </Setter.Value>
                        </Setter>
                    </Trigger>
                </Style.Triggers>
            </Style>
            <Style TargetType="ScrollBar" BasedOn="{StaticResource HubScrollBar}"/>
            <Style x:Key="HubComboBoxItem" TargetType="ComboBoxItem">
                <Setter Property="OverridesDefaultStyle" Value="True" />
                <Setter Property="Foreground" Value="#F3F4F6" />
                <Setter Property="Background" Value="#161820" />
                <Setter Property="Padding" Value="10,6" />
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="ComboBoxItem">
                            <Border x:Name="ItemBackground" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}">
                                <ContentPresenter TextElement.Foreground="{TemplateBinding Foreground}" />
                            </Border>
                            <ControlTemplate.Triggers>
                                <Trigger Property="IsSelected" Value="True">
                                    <Setter TargetName="ItemBackground" Property="Background" Value="#3B2414" />
                                </Trigger>
                                <Trigger Property="IsHighlighted" Value="True">
                                    <Setter TargetName="ItemBackground" Property="Background" Value="#52311A" />
                                </Trigger>
                                <Trigger Property="IsEnabled" Value="False">
                                    <Setter Property="Foreground" Value="#A0A7B5" />
                                </Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>
            <Style x:Key="HubComboBox" TargetType="ComboBox">
                <Setter Property="OverridesDefaultStyle" Value="True" />
                <Setter Property="ItemContainerStyle" Value="{StaticResource HubComboBoxItem}" />
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="ComboBox">
                            <Grid>
                                <ToggleButton x:Name="DropDownToggle" Focusable="False" ClickMode="Press"
                                              Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                                              BorderThickness="{TemplateBinding BorderThickness}" Foreground="{TemplateBinding Foreground}"
                                              IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                                    <ToggleButton.Template>
                                        <ControlTemplate TargetType="ToggleButton">
                                            <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                                                    BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="4">
                                                <Path Data="M 0 0 L 4 4 L 8 0" Stroke="{TemplateBinding Foreground}" StrokeThickness="1.5"
                                                      HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,10,0" />
                                            </Border>
                                        </ControlTemplate>
                                    </ToggleButton.Template>
                                </ToggleButton>
                                <ContentPresenter Content="{TemplateBinding SelectionBoxItem}" IsHitTestVisible="False"
                                                  TextElement.Foreground="{TemplateBinding Foreground}"
                                                  Margin="{TemplateBinding Padding}" VerticalAlignment="Center" />
                                <Popup x:Name="PART_Popup" Placement="Bottom" Focusable="False"
                                       IsOpen="{TemplateBinding IsDropDownOpen}">
                                    <Border Background="#161820" BorderBrush="#666F83" BorderThickness="1"
                                            MinWidth="{TemplateBinding ActualWidth}" MaxHeight="{TemplateBinding MaxDropDownHeight}">
                                        <ScrollViewer HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto">
                                            <ScrollViewer.Resources>
                                                <Style TargetType="ScrollBar" BasedOn="{StaticResource HubScrollBar}"/>
                                            </ScrollViewer.Resources>
                                            <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained" />
                                        </ScrollViewer>
                                    </Border>
                                </Popup>
                            </Grid>
                            <ControlTemplate.Triggers>
                                <Trigger Property="IsMouseOver" Value="True">
                                    <Setter TargetName="DropDownToggle" Property="BorderBrush" Value="#8089A0" />
                                </Trigger>
                                <Trigger Property="IsKeyboardFocusWithin" Value="True">
                                    <Setter TargetName="DropDownToggle" Property="BorderBrush" Value="#FF7A18" />
                                </Trigger>
                                <Trigger Property="IsDropDownOpen" Value="True">
                                    <Setter TargetName="DropDownToggle" Property="BorderBrush" Value="#FF7A18" />
                                </Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>
            <Style TargetType="Button">
                <Setter Property="HorizontalContentAlignment" Value="Center"/>
                <Setter Property="VerticalContentAlignment" Value="Center"/>
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="Button">
                            <Border x:Name="Face" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                                    BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}">
                                <ContentPresenter RecognizesAccessKey="True" HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                                  VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
                            </Border>
                            <ControlTemplate.Triggers>
                                <Trigger Property="IsMouseOver" Value="True">
                                    <Setter TargetName="Face" Property="Background" Value="#2C303D"/>
                                </Trigger>
                                <Trigger Property="IsPressed" Value="True">
                                    <Setter TargetName="Face" Property="Background" Value="#252834"/>
                                </Trigger>
                                <MultiTrigger>
                                    <MultiTrigger.Conditions>
                                        <Condition Property="Tag" Value="Primary"/>
                                        <Condition Property="IsMouseOver" Value="True"/>
                                    </MultiTrigger.Conditions>
                                    <Setter TargetName="Face" Property="Background" Value="#FF6B00"/>
                                </MultiTrigger>
                                <MultiTrigger>
                                    <MultiTrigger.Conditions>
                                        <Condition Property="Tag" Value="Primary"/>
                                        <Condition Property="IsPressed" Value="True"/>
                                    </MultiTrigger.Conditions>
                                    <Setter TargetName="Face" Property="Background" Value="#E05A00"/>
                                </MultiTrigger>
                                <Trigger Property="IsKeyboardFocused" Value="True">
                                    <Setter TargetName="Face" Property="BorderBrush" Value="#FF7A18"/>
                                </Trigger>
                                <Trigger Property="IsEnabled" Value="False">
                                    <Setter TargetName="Face" Property="Background" Value="#222530"/>
                                    <Setter TargetName="Face" Property="BorderBrush" Value="#2E3240"/>
                                    <Setter Property="Opacity" Value="0.55"/>
                                </Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>
            <Style TargetType="TextBox">
                <Setter Property="SelectionBrush" Value="#FF7A18"/>
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="TextBox">
                            <Border x:Name="Bd" Background="{TemplateBinding Background}"
                                    BorderBrush="{TemplateBinding BorderBrush}"
                                    BorderThickness="{TemplateBinding BorderThickness}"
                                    CornerRadius="4" Padding="{TemplateBinding Padding}">
                                <ScrollViewer x:Name="PART_ContentHost" SnapsToDevicePixels="{TemplateBinding SnapsToDevicePixels}"/>
                            </Border>
                            <ControlTemplate.Triggers>
                                <Trigger Property="IsMouseOver" Value="True">
                                    <Setter TargetName="Bd" Property="BorderBrush" Value="#4B5266"/>
                                </Trigger>
                                <Trigger Property="IsKeyboardFocused" Value="True">
                                    <Setter TargetName="Bd" Property="BorderBrush" Value="#FF7A18"/>
                                </Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>
            <Style TargetType="ToolTip">
                <Setter Property="Background" Value="#222530"/>
                <Setter Property="Foreground" Value="#F3F4F6"/>
                <Setter Property="BorderBrush" Value="#2E3240"/>
            </Style>
            <Style TargetType="CheckBox">
                <Setter Property="Foreground" Value="#F3F4F6"/>
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="CheckBox">
                            <DockPanel Background="Transparent" LastChildFill="True">
                                <Border x:Name="Box" Width="14" Height="14" Margin="0,2,7,0" VerticalAlignment="Top"
                                        Background="#161820" BorderBrush="#7E879B" BorderThickness="1" CornerRadius="3">
                                    <Path x:Name="Mark" Visibility="Collapsed" Data="M 2,6 L 5,9 L 10,3"
                                          Stroke="#FFFFFF" StrokeThickness="1.5" StrokeLineJoin="Round"/>
                                </Border>
                                <ContentPresenter RecognizesAccessKey="True" VerticalAlignment="Center"/>
                            </DockPanel>
                            <ControlTemplate.Triggers>
                                <Trigger Property="IsChecked" Value="True">
                                    <Setter TargetName="Box" Property="Background" Value="#FF7A18"/>
                                    <Setter TargetName="Box" Property="BorderBrush" Value="#FF7A18"/>
                                    <Setter TargetName="Mark" Property="Visibility" Value="Visible"/>
                                </Trigger>
                                <Trigger Property="IsKeyboardFocused" Value="True">
                                    <Setter TargetName="Box" Property="BorderBrush" Value="#FF7A18"/>
                                </Trigger>
                                <Trigger Property="IsEnabled" Value="False">
                                    <Setter Property="Opacity" Value="0.5"/>
                                </Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>
        </ResourceDictionary>
        """));

    private static readonly Lazy<Style> ComboBoxStyle = new(() => (Style)DarkControls.Value["HubComboBox"]);

    /// <summary>Crea il badge iconico arancione FLIP (28x28, bordi arrotondati, 'F' bianca in grassetto).</summary>
    public static Border CreateBrandBadge() => new()
    {
        Width = 28,
        Height = 28,
        CornerRadius = new CornerRadius(7),
        Background = Accent,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = "F",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    public static Border CreateCard(string? title = null, string? subtitle = null)
    {
        var border = new Border
        {
            Background = CardBg,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18),
            Margin = new Thickness(0, 0, 0, 16)
        };

        var stack = new StackPanel();
        if (!string.IsNullOrEmpty(title))
        {
            var header = new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, string.IsNullOrEmpty(subtitle) ? 12 : 2)
            };
            stack.Children.Add(header);
        }

        if (!string.IsNullOrEmpty(subtitle))
        {
            var sub = new TextBlock
            {
                Text = subtitle,
                FontSize = 12,
                Foreground = TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            };
            stack.Children.Add(sub);
        }

        border.Child = stack;
        return border;
    }

    public static CheckBox CreateToggle(string label, string? description, bool isChecked, Action<bool> onChanged)
    {
        var cb = new CheckBox
        {
            IsChecked = isChecked,
            Margin = new Thickness(0, 6, 0, 6),
            Foreground = TextPrimary,
            VerticalContentAlignment = VerticalAlignment.Top
        };

        var contentStack = new StackPanel { Margin = new Thickness(6, -2, 0, 0) };
        contentStack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = TextPrimary,
            TextWrapping = TextWrapping.Wrap
        });

        if (!string.IsNullOrEmpty(description))
        {
            contentStack.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 11,
                Foreground = TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
        }

        cb.Content = contentStack;
        bool lastValue = isChecked;
        void OnToggle(object sender, RoutedEventArgs e)
        {
            if (cb.IsChecked is not bool value || value == lastValue) return;
            onChanged(value);
            lastValue = value;
        }
        cb.Checked += OnToggle;
        cb.Unchecked += OnToggle;
        return cb;
    }

    public static Button CreateButton(string label, bool isPrimary = false, Action? onClick = null)
    {
        var btn = new Button
        {
            Content = label,
            Background = isPrimary ? Accent : CardBg,
            Foreground = isPrimary ? Brushes.White : TextPrimary,
            BorderBrush = isPrimary ? AccentHover : CardBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 7, 14, 7),
            FontSize = 12,
            FontWeight = isPrimary ? FontWeights.SemiBold : FontWeights.Medium,
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = isPrimary ? "Primary" : null
        };

        if (onClick != null)
        {
            btn.Click += (_, _) => onClick();
        }

        return btn;
    }

    public static TextBox CreateTextBox(string text, Action<string> onChanged)
    {
        var tb = new TextBox
        {
            Text = text,
            Background = InputBg,
            Foreground = TextPrimary,
            CaretBrush = TextPrimary,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6, 10, 6),
            FontSize = 13
        };

        string lastText = tb.Text;
        tb.TextChanged += (_, _) =>
        {
            string value = tb.Text;
            if (value == lastText) return;
            onChanged(value);
            lastText = value;
        };
        return tb;
    }

    public static ComboBox CreateComboBox<T>(IEnumerable<T> items, T selectedItem, Action<T> onSelected) where T : notnull
    {
        var combo = new ComboBox
        {
            Style = ComboBoxStyle.Value,
            ItemsSource = items.ToList(),
            SelectedItem = selectedItem,
            Background = InputBg,
            Foreground = TextPrimary,
            BorderBrush = TextMuted,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6, 26, 6),
            MaxDropDownHeight = 260,
            FontSize = 13
        };

        T lastSelection = selectedItem;
        combo.SelectionChanged += (_, e) =>
        {
            if (e.Source == combo && combo.SelectedItem is T val && !EqualityComparer<T>.Default.Equals(val, lastSelection))
            {
                onSelected(val);
                lastSelection = val;
            }
        };

        return combo;
    }
}

/// <summary>
/// Hub unificato delle impostazioni di FLIP a 7 sezioni.
/// </summary>
public sealed class SettingsHubWindow : Window
{
    internal const int EnginesSection = 3;
    internal const int HistorySection = 5;
    internal const int GuideSectionIndex = 6;
    private static SettingsHubWindow? _instance;
    internal static bool IsSingletonOpen => _instance?.IsActive == true;
    private StackPanel _sidebarNav = new();
    private ScrollViewer _contentScroll = new();
    private readonly Dictionary<int, Button> _navButtons = [];
    private ComboBox? _languageCombo;
    private int _currentSectionIndex = 0;
    private bool _isRebuildingLanguage;
    private bool _rebuildPending;
    private bool _isClosed;
    private EventHandler? _chromeStateHandler;
    private Action<string[], string[], FlipSettings, Action<string>>? _startBatch;
    private Action? _cancelBatch;
    private string[] _batchFiles = [];
    private string[] _allBatchFiles = [];
    private string? _activeBatchGroup;
    private string _batchStatus = "";
    private TextBlock? _batchStatusBlock;
    private ContentControl? _historyView;

    internal int ActiveSectionIndex => _currentSectionIndex;

    private sealed record LanguageChoice(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    internal static void RefreshLanguage()
    {
        if (_instance is { IsLoaded: true, _isClosed: false } hub)
            hub.ScheduleRebuildForLanguage();
    }

    public static void ShowSingleton() => ShowSingleton(null, null, null, null);

    internal static void ShowSingleton(int? section, Action<string[], string[], FlipSettings, Action<string>>? startBatch,
        Action? cancelBatch, string[]? batchFiles)
    {
        if (_instance == null)
        {
            var window = new SettingsHubWindow();
            window.Closed += (_, _) => { if (ReferenceEquals(_instance, window)) _instance = null; };
            _instance = window;
        }

        var hub = _instance;
        if (startBatch != null) hub._startBatch = startBatch;
        if (cancelBatch != null) hub._cancelBatch = cancelBatch;
        if (batchFiles is { Length: > 0 }) hub.SetBatchFiles(batchFiles);
        if (section.HasValue) hub.NavigateTo(section.Value);
        if (!hub.IsVisible) hub.Show();
        if (hub.WindowState == WindowState.Minimized) hub.WindowState = WindowState.Normal;
        hub.Activate();
    }

    internal static void CloseSingleton() => _instance?.Close();

    internal static void RefreshHistory()
    {
        if (_instance?._historyView is { } view) view.Content = HistoryAndRecipesSection.Build();
    }

    public SettingsHubWindow()
    {
        Title = Program.ReleaseTitle;
        Width = 980;
        Height = 680;
        MinWidth = 860;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = HubUi.BgDark;
        Foreground = HubUi.TextPrimary;
        Resources.MergedDictionaries.Add(HubUi.DarkControls.Value);
        ApplyChrome(this, resizable: true);
        Closed += (_, _) => _isClosed = true;

        BuildLayout();
        NavigateTo(0);
    }

    internal bool SelectLanguageForTest(string preference)
    {
        if (_languageCombo == null) return false;
        string normalized = Loc.NormalizePreference(preference);
        var match = _languageCombo.Items.OfType<LanguageChoice>()
            .FirstOrDefault(c => string.Equals(c.Value, normalized, StringComparison.OrdinalIgnoreCase));
        if (match == null) return false;
        _languageCombo.SelectedItem = match;
        return true;
    }

    private void ScheduleRebuildForLanguage()
    {
        if (_isClosed || _rebuildPending) return;
        _rebuildPending = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            _rebuildPending = false;
            RebuildForLanguage();
        });
    }

    private void RebuildForLanguage()
    {
        if (_isClosed || _isRebuildingLanguage) return;
        _isRebuildingLanguage = true;
        try
        {
            int activeSection = _currentSectionIndex;
            BuildLayout();
            NavigateTo(activeSection);
        }
        finally
        {
            _isRebuildingLanguage = false;
        }
    }

    private void BuildLayout()
    {
        if (_chromeStateHandler != null)
        {
            StateChanged -= _chromeStateHandler;
            _chromeStateHandler = null;
        }

        // Disconnette esplicitamente il vecchio albero logico prima di costruire i nuovi controlli
        if (_languageCombo != null)
        {
            _languageCombo.IsDropDownOpen = false;
            _languageCombo = null;
        }
        _historyView = null;
        _batchStatusBlock = null;
        _navButtons.Clear();
        _sidebarNav.Children.Clear();
        _contentScroll.Content = null;
        Content = null;

        // Ricrea da zero i contenitori principali così nessun UIElement viene mai riattaccato a un altro parent
        _sidebarNav = new StackPanel();
        _contentScroll = new ScrollViewer();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ── Sidebar Sinistra ────────────────────────────────────────────────
        var sidebarBorder = new Border
        {
            Background = HubUi.SidebarBg,
            BorderBrush = HubUi.CardBorder,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(16, 20, 16, 16)
        };

        var sidebarGrid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        sidebarGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Nav
        sidebarGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Footer (Language + Reset)

        // Nav Buttons (unico logo FLIP già presente in alto nella barra del titolo)
        string[] sectionNames =
        [
            Loc.T("1. Ruota & Spicchi", "1. Wheel & Slices"),
            Loc.T("2. Attivazione & Input", "2. Activation & Input"),
            Loc.T("3. Destinazione File", "3. Output Rules"),
            Loc.T("4. Motori & GPU", "4. Engines & GPU"),
            Loc.T("5. Cartelle Automatiche", "5. Watch Folders"),
            Loc.T("6. Cronologia & Preset", "6. History & Presets"),
            Loc.T("7. Guida & Scorciatoie", "7. Guide & Shortcuts")
        ];

        for (int i = 0; i < sectionNames.Length; i++)
        {
            int index = i;
            var btn = new Button
            {
                Content = sectionNames[i],
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 2, 0, 2),
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Background = Brushes.Transparent,
                Foreground = HubUi.TextSecondary,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(3, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };

            btn.Click += (_, _) => NavigateTo(index);
            _navButtons[index] = btn;
            _sidebarNav.Children.Add(btn);
        }

        Grid.SetRow(_sidebarNav, 0);
        sidebarGrid.Children.Add(_sidebarNav);

        // Footer: Selettore Lingua + Ripristina predefiniti
        var footerStack = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        footerStack.Children.Add(new TextBlock
        {
            Text = Loc.T("Lingua", "Language"),
            FontSize = 11,
            Foreground = HubUi.TextSecondary,
            Margin = new Thickness(2, 0, 0, 4)
        });

        LanguageChoice[] langChoices =
        [
            new(Loc.Auto, "Auto (Windows)"),
            new(Loc.Italian, "Italiano"),
            new(Loc.English, "English")
        ];
        string currentLangPref = Loc.NormalizePreference(SettingsStore.Current.CreateSnapshot().Language);
        LanguageChoice selectedLang = langChoices.FirstOrDefault(c => c.Value == currentLangPref) ?? langChoices[0];
        ComboBox? comboLang = null;
        comboLang = HubUi.CreateComboBox(langChoices, selectedLang, choice =>
        {
            if (_isRebuildingLanguage || _isClosed) return;
            if (comboLang != null) comboLang.IsDropDownOpen = false;
            SettingsStore.Current.Update(s => s.Language = choice.Value);
            Loc.SetPreference(choice.Value);
            ScheduleRebuildForLanguage();
        });
        _languageCombo = comboLang;
        comboLang.FontSize = 12;
        comboLang.Padding = new Thickness(8, 5, 24, 5);
        comboLang.Margin = new Thickness(0, 0, 0, 10);
        footerStack.Children.Add(comboLang);

        var resetBtn = HubUi.CreateButton(Loc.T("Ripristina predefiniti", "Reset defaults"), isPrimary: false, () =>
        {
            if (Confirm(
                Loc.T("Ripristina impostazioni", "Reset settings"),
                Loc.T("Ripristinare le impostazioni principali ai valori predefiniti?", "Reset core settings back to their defaults?"),
                Loc.T("Ripristina", "Reset"),
                this))
            {
                SettingsStore.Current.Update(s =>
                {
                    var fresh = new FlipSettings();
                    // Sovrascrivi proprietà chiave con default puliti
                    s.MaxVisibleGrade = fresh.MaxVisibleGrade;
                    s.EnabledSpecialistFields = fresh.EnabledSpecialistFields;
                    s.TriggerKey = fresh.TriggerKey;
                    s.RequireDragButton = fresh.RequireDragButton;
                    s.EnableFlipWithTabOrScroll = fresh.EnableFlipWithTabOrScroll;
                    s.OutputDirectoryMode = fresh.OutputDirectoryMode;
                    s.CustomOutputDirectory = fresh.CustomOutputDirectory;
                    s.UserSelectedCustomOutputDirectory = false;
                    s.ConflictPolicy = fresh.ConflictPolicy;
                    s.ReplaceOriginalToRecycleBin = fresh.ReplaceOriginalToRecycleBin;
                    s.KeepOnlyIfSmaller = fresh.KeepOnlyIfSmaller;
                });
                NavigateTo(_currentSectionIndex); // Ricarica la vista
            }
        });
        resetBtn.Margin = new Thickness(0);
        footerStack.Children.Add(resetBtn);

        Grid.SetRow(footerStack, 1);
        sidebarGrid.Children.Add(footerStack);

        sidebarBorder.Child = sidebarGrid;
        Grid.SetColumn(sidebarBorder, 0);
        grid.Children.Add(sidebarBorder);

        // ── Contenuto Destra ────────────────────────────────────────────────
        _contentScroll.Background = HubUi.BgDark;
        _contentScroll.Padding = new Thickness(28, 24, 28, 24);
        _contentScroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _contentScroll.Focusable = false;
        _contentScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _contentScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        if (_contentScroll.Resources.MergedDictionaries.Count == 0)
            _contentScroll.Resources.MergedDictionaries.Add(HubUi.DarkControls.Value);
        _contentScroll.Resources[typeof(TextBlock)] = new Style(typeof(TextBlock))
        {
            Setters = { new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap) }
        };

        Grid.SetColumn(_contentScroll, 1);
        grid.Children.Add(_contentScroll);

        Content = Frame(this, grid, resizable: true, handler => _chromeStateHandler = handler);
    }

    public void NavigateTo(int sectionIndex)
    {
        _currentSectionIndex = sectionIndex;
        _historyView = null;
        _batchStatusBlock = null;

        // Aggiorna stile bottoni sidebar con indicatore attivo arancione FLIP
        foreach (var (idx, btn) in _navButtons)
        {
            bool active = (idx == sectionIndex);
            btn.Background = active ? HubUi.CardBg : Brushes.Transparent;
            btn.BorderBrush = active ? HubUi.Accent : Brushes.Transparent;
            btn.Foreground = active ? HubUi.TextPrimary : HubUi.TextSecondary;
            btn.FontWeight = active ? FontWeights.SemiBold : FontWeights.Medium;
        }

        // Carica la vista corrispondente
        UIElement view = sectionIndex switch
        {
            0 => WheelStudioSection.Build(),
            1 => TriggerAndOsSection.Build(),
            2 => OutputRulesSection.Build(),
            3 => EnginesHubSection.Build(),
            4 => WatchFoldersSection.Build(),
            HistorySection => BuildHistory(),
            GuideSectionIndex => GuideSection.Build(),
            _ => new TextBlock { Text = Loc.T("Sezione non valida", "Invalid section"), Foreground = HubUi.TextMuted }
        };

        if (_contentScroll.IsKeyboardFocusWithin && _navButtons.TryGetValue(sectionIndex, out var sectionButton))
            sectionButton.Focus();
        _contentScroll.Content = view;
        _contentScroll.ScrollToTop();
    }

    private UIElement BuildHistory()
    {
        _historyView = new ContentControl { Content = HistoryAndRecipesSection.Build(), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        if (_startBatch == null) return _historyView;

        var root = new StackPanel();
        var card = HubUi.CreateCard(
            Loc.T("Conversione batch", "Batch conversion"),
            Loc.T("Seleziona i file e uno o più formati comuni.", "Select files and one or more shared output formats."));
        var panel = (StackPanel)card.Child;
        var files = new TextBlock { Foreground = HubUi.TextSecondary, TextWrapping = TextWrapping.Wrap };
        var groupBar = new WrapPanel { Margin = new Thickness(0, 8, 0, 2), Visibility = Visibility.Collapsed };
        var formats = new WrapPanel { Margin = new Thickness(0, 10, 0, 8) };
        var actions = new WrapPanel();
        FlipSettings snapshot = SettingsStore.Current.CreateSnapshot();
        var run = HubUi.CreateButton(Loc.T("Converti", "Convert"), true, () =>
        {
            var selected = formats.Children.OfType<CheckBox>().Where(box => box.IsChecked == true)
                .Select(box => (string)box.Content).ToArray();
            _startBatch(_batchFiles, selected, snapshot, SetBatchStatus);
        });
        actions.Children.Add(HubUi.CreateButton(Loc.T("Scegli file…", "Choose files…"), false, () =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("FLIP · Scegli i file", "FLIP · Choose files"), Multiselect = true };
            if (picker.ShowDialog(this) == true) { SetBatchFiles(picker.FileNames); PopulateFormats(); }
        }));
        actions.Children.Add(run);
        actions.Children.Add(HubUi.CreateButton(Loc.T("Annulla conversione", "Cancel conversion"), false, () => _cancelBatch?.Invoke()));
        _batchStatusBlock = new TextBlock { Text = _batchStatus, Foreground = HubUi.TextSecondary, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(files);
        panel.Children.Add(groupBar);
        panel.Children.Add(formats);
        panel.Children.Add(actions);
        panel.Children.Add(_batchStatusBlock);
        root.Children.Add(card);
        root.Children.Add(_historyView);
        PopulateFormats();
        return root;

        Button MakeGroupPill(string caption, bool active, Action onClick)
        {
            var btn = HubUi.CreateButton(caption, active, onClick);
            btn.Padding = new Thickness(10, 5, 10, 5);
            btn.FontSize = 12;
            btn.Margin = new Thickness(0, 0, 6, 6);
            return btn;
        }

        void PopulateFormats()
        {
            snapshot = SettingsStore.Current.CreateSnapshot();
            groupBar.Children.Clear();
            formats.Children.Clear();

            var groups = RoutePlanner.GroupFilesByExtension(_allBatchFiles);
            if (groups.Count >= 2)
            {
                groupBar.Visibility = Visibility.Visible;
                groupBar.Children.Add(MakeGroupPill(Loc.F("Tutti ({0})", "All ({0})", _allBatchFiles.Length), _activeBatchGroup == null, () =>
                {
                    _activeBatchGroup = null;
                    _batchFiles = _allBatchFiles;
                    PopulateFormats();
                }));
                foreach (var group in groups)
                {
                    string extKey = group.Extension;
                    bool isActive = string.Equals(_activeBatchGroup, extKey, StringComparison.OrdinalIgnoreCase);
                    groupBar.Children.Add(MakeGroupPill(group.Label, isActive, () =>
                    {
                        _activeBatchGroup = extKey;
                        _batchFiles = group.Files;
                        PopulateFormats();
                    }));
                }
            }
            else
            {
                groupBar.Visibility = Visibility.Collapsed;
            }

            var targets = RoutePlanner.GetReachableTargetFormats(_batchFiles.Select(path => Path.GetExtension(path)).ToArray(), snapshot);
            files.Text = _allBatchFiles.Length == 0
                ? Loc.T("Nessun file selezionato.", "No files selected.")
                : _activeBatchGroup != null
                    ? Loc.F("Gruppo {0} · {1} file su {2} · {3} formati disponibili", "Group {0} · {1} of {2} files · {3} formats available",
                        _activeBatchGroup.ToUpperInvariant(), _batchFiles.Length, _allBatchFiles.Length, targets.Count)
                    : targets.Count == 0 && groups.Count >= 2
                        ? Loc.F("Batch misto ({0} file): seleziona una pillola qui sotto per convertire per tipo.", "Mixed batch ({0} files): pick a pill below to convert by type.", _allBatchFiles.Length)
                        : targets.Count == 0
                            ? Loc.F("{0} file · nessun formato comune.", "{0} files · no shared formats.", _batchFiles.Length)
                            : Loc.F("{0} file selezionati", "{0} files selected", _batchFiles.Length);
            files.ToolTip = string.Join(Environment.NewLine, _batchFiles);
            foreach (var target in targets)
            {
                var choice = new CheckBox
                {
                    Content = target.Extension.ToUpperInvariant(), Foreground = HubUi.TextPrimary,
                    Margin = new Thickness(0, 3, 16, 3)
                };
                choice.Checked += (_, _) => run.IsEnabled = true;
                choice.Unchecked += (_, _) => run.IsEnabled = formats.Children.OfType<CheckBox>().Any(box => box.IsChecked == true);
                formats.Children.Add(choice);
            }
            run.IsEnabled = false;
        }
    }

    private void SetBatchFiles(string[] files)
    {
        _allBatchFiles = Policy.ReadFiles(new DataObject(DataFormats.FileDrop, files));
        _activeBatchGroup = null;
        _batchFiles = _allBatchFiles;
        SetBatchStatus(_allBatchFiles.Length == 0 ? Loc.T("Seleziona file locali disponibili.", "Select available local files.") : "");
    }

    private void SetBatchStatus(string status)
    {
        _batchStatus = status;
        if (_batchStatusBlock != null) _batchStatusBlock.Text = status;
    }

    internal static bool Confirm(string title, string message, string accept, Window? owner = null)
    {
        owner ??= _instance;
        var dialog = new Window
        {
            Title = title, Width = 460, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner?.IsVisible == true ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Background = HubUi.BgDark, Foreground = HubUi.TextPrimary, ShowInTaskbar = false
        };
        if (owner?.IsVisible == true) dialog.Owner = owner;
        dialog.Resources.MergedDictionaries.Add(HubUi.DarkControls.Value);
        ApplyChrome(dialog, resizable: false);
        var content = new StackPanel { Margin = new Thickness(24, 18, 24, 22) };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = HubUi.CreateButton(Loc.T("Annulla", "Cancel"), false, () => dialog.DialogResult = false);
        cancel.IsCancel = true;
        actions.Children.Add(cancel);
        var confirm = HubUi.CreateButton(accept, true, () => dialog.DialogResult = true);
        confirm.IsDefault = true;
        actions.Children.Add(confirm);
        content.Children.Add(actions);
        dialog.Content = Frame(dialog, content, resizable: false);
        return dialog.ShowDialog() == true;
    }

    private static void ApplyChrome(Window window, bool resizable)
    {
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.NoResize;
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 42, GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(resizable ? 6 : 0)
        });
    }

    private static UIElement Frame(Window window, UIElement body, bool resizable, Action<EventHandler>? registerStateHandler = null)
    {
        var root = new DockPanel { Background = HubUi.BgDark };
        var titleBar = new DockPanel { Background = HubUi.SidebarBg, Height = 42, LastChildFill = true };
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(controls, Dock.Right);
        WindowChrome.SetIsHitTestVisibleInChrome(controls, true);
        titleBar.Children.Add(controls);
        if (resizable)
        {
            AddControl("−", Loc.T("Riduci a icona", "Minimize"), () => SystemCommands.MinimizeWindow(window));
            var maximize = AddControl("□", Loc.T("Massimizza", "Maximize"), () =>
            {
                if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window);
                else SystemCommands.MaximizeWindow(window);
            });
            EventHandler stateHandler = (_, _) =>
            {
                bool maximized = window.WindowState == WindowState.Maximized;
                maximize.Content = maximized ? "❐" : "□";
                maximize.ToolTip = maximized ? Loc.T("Ripristina finestra", "Restore") : Loc.T("Massimizza", "Maximize");
                System.Windows.Automation.AutomationProperties.SetName(maximize, (string)maximize.ToolTip);
            };
            window.StateChanged += stateHandler;
            registerStateHandler?.Invoke(stateHandler);
        }
        AddControl("×", Loc.T("Chiudi", "Close"), window.Close);
        var titleBrand = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        titleBrand.Children.Add(HubUi.CreateBrandBadge());
        titleBrand.Children.Add(new TextBlock
        {
            Text = window.Title,
            Foreground = HubUi.TextPrimary,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        });
        titleBar.Children.Add(titleBrand);
        DockPanel.SetDock(titleBar, Dock.Top);
        root.Children.Add(titleBar);
        root.Children.Add(body);
        return root;

        Button AddControl(string caption, string name, Action action)
        {
            var button = HubUi.CreateButton(caption, false, action);
            button.Width = 45; button.Margin = new Thickness(0); button.ToolTip = name;
            System.Windows.Automation.AutomationProperties.SetName(button, name);
            controls.Children.Add(button);
            return button;
        }
    }
}

