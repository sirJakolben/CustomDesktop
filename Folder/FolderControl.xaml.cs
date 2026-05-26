using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace CustomDesktop.Folder;

internal sealed partial class FolderControl : UserControl
{
    private FolderModel? _model;

    /// Raised when the user single-clicks the folder (request to open popup).
    internal event Action<FolderControl>? OpenRequested;

    /// Raised when the user confirms folder deletion via the context menu.
    internal event Action<FolderControl>? DeleteRequested;

    /// Raised when a drag gesture is detected on this control.
    internal event Action<FolderControl>? DragStartRequested;

    // Drag-detection state
    private bool   _pointerDown;
    private global::Windows.Foundation.Point _pointerDownPos;
    private const double DragThresholdPx = 8.0;

    public FolderControl()
    {
        InitializeComponent();
        BuildColorSubMenu();
    }

    // ── Binding ─────────────────────────────────────────────────────────────────

    internal void Bind(FolderModel model)
    {
        _model = model;
        NameLabel.Text = model.Name;
        ApplyColor(model.BackgroundColor);
    }

    /// Update the 2×2 icon preview from up to 4 loaded BitmapImages.
    internal void SetPreviews(IReadOnlyList<BitmapImage?> icons)
    {
        Image[] slots = [Preview0, Preview1, Preview2, Preview3];
        for (int i = 0; i < slots.Length; i++)
            slots[i].Source = i < icons.Count ? icons[i] : null;
    }

    private void ApplyColor(Color c)
    {
        FolderBackground.Background = new SolidColorBrush(
            Color.FromArgb(0xCC, c.R, c.G, c.B));   // 80 % opacity
    }

    // ── Colour sub-menu ──────────────────────────────────────────────────────────

    private void BuildColorSubMenu()
    {
        foreach (var preset in FolderModel.Presets)
        {
            var item = new MenuFlyoutItem
            {
                Text = string.Empty,
                Icon = new FontIcon { Glyph = "" },   // filled circle
            };
            // Tint the icon to the preset colour
            item.Icon.Foreground = new SolidColorBrush(preset);
            var captured = preset;
            item.Click += (_, _) => SetColor(captured);
            ColorSubItem.Items.Add(item);
        }

        // Colour-wheel button
        var wheelItem = new MenuFlyoutItem
        {
            Text = "Weitere Farben …",
            Icon = new FontIcon { Glyph = "" },    // color picker icon
        };
        wheelItem.Click += ColorWheel_Click;
        ColorSubItem.Items.Add(new MenuFlyoutSeparator());
        ColorSubItem.Items.Add(wheelItem);
    }

    private void SetColor(Color c)
    {
        if (_model is null) return;
        _model.BackgroundColor = c;
        ApplyColor(c);
    }

    private void ColorWheel_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;

        // WinUI 3 ColorPicker inside a Flyout anchored to this control
        var picker = new ColorPicker
        {
            Color                   = _model.BackgroundColor,
            ColorSpectrumShape      = ColorSpectrumShape.Ring,
            IsAlphaEnabled          = false,
            IsHexInputVisible       = true,
            MinWidth                = 280,
        };

        var flyout = new Flyout { Content = picker };
        picker.ColorChanged += (_, args) => SetColor(args.NewColor);
        flyout.ShowAt(FolderBackground);
    }

    // ── Pointer events ───────────────────────────────────────────────────────────

    private void RootGrid_Tapped(object sender, TappedRoutedEventArgs e)
    {
        OpenRequested?.Invoke(this);
        e.Handled = true;
    }

    private void RootGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
        => e.Handled = true;    // prevent bubbling to LayerCanvas (Progman forward)

    // ── Rename ───────────────────────────────────────────────────────────────────

    private void NameLabel_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        NameLabel.Visibility  = Visibility.Collapsed;
        NameTextBox.Text      = _model?.Name ?? string.Empty;
        NameTextBox.Visibility = Visibility.Visible;
        NameTextBox.Focus(FocusState.Programmatic);
        NameTextBox.SelectAll();
        e.Handled = true;
    }

    private void CommitRename()
    {
        if (_model is null) return;
        var trimmed = NameTextBox.Text.Trim();
        if (trimmed.Length > 0)
        {
            _model.Name    = trimmed;
            NameLabel.Text = trimmed;
        }
        NameTextBox.Visibility = Visibility.Collapsed;
        NameLabel.Visibility   = Visibility.Visible;
    }

    private void NameTextBox_LostFocus(object sender, RoutedEventArgs e) => CommitRename();

    private void NameTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            CommitRename();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            NameTextBox.Visibility = Visibility.Collapsed;
            NameLabel.Visibility   = Visibility.Visible;
            e.Handled = true;
        }
    }

    // ── Delete ───────────────────────────────────────────────────────────────────

    private void DeleteFolder_Click(object sender, RoutedEventArgs e)
        => DeleteRequested?.Invoke(this);

    // ── Drag detection ───────────────────────────────────────────────────────────

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _pointerDown    = true;
        _pointerDownPos = e.GetCurrentPoint(RootGrid).Position;
        RootGrid.CapturePointer(e.Pointer);
    }

    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown) return;
        var pos = e.GetCurrentPoint(RootGrid).Position;
        double dx = pos.X - _pointerDownPos.X;
        double dy = pos.Y - _pointerDownPos.Y;
        if (Math.Sqrt(dx * dx + dy * dy) >= DragThresholdPx)
        {
            _pointerDown = false;
            RootGrid.ReleasePointerCapture(e.Pointer);
            DragStartRequested?.Invoke(this);
        }
    }

    private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _pointerDown = false;
        RootGrid.ReleasePointerCapture(e.Pointer);
    }

    private void RootGrid_PointerCanceled(object sender, PointerRoutedEventArgs e)
        => _pointerDown = false;

    // ── Model accessor ───────────────────────────────────────────────────────────

    internal FolderModel? Model => _model;
}
