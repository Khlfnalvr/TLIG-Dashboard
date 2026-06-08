using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TLIGDashboard.Services;

namespace TLIGDashboard.Views;

/// <summary>
/// Shared staff dialog for configuring AI providers (DeepSeek / OpenAI / Anthropic):
/// per provider an enable toggle, API key, and which models to offer, plus the shared
/// system prompt. Edits go through <see cref="AiConfigService"/> — on the Server flavor
/// they hit local settings; on the Client flavor (staff) they POST to the server.
/// </summary>
internal static class AiConfigUi
{
    private static LocalizationManager Lang => App.Lang;

    private sealed class Row
    {
        public AiProviderView View = null!;
        public ToggleSwitch   Enable = null!;
        public PasswordBox    Key = null!;
        public List<(string model, CheckBox cb)> Models = new();
    }

    /// <summary>
    /// Shows the provider-configuration dialog. Returns true when the user saved and
    /// the save succeeded. No-op (returns false) when the current user cannot edit.
    /// </summary>
    public static async Task<bool> ShowProviderConfigAsync(XamlRoot root)
    {
        var cfg = await AiConfigService.LoadAsync();
        if (!cfg.CanEdit) return false;

        var panel = new StackPanel { Spacing = 12, Width = 380 };
        var rows  = new List<Row>();

        foreach (var p in cfg.Providers)
        {
            var info = AiProviders.Resolve(p.Id);
            var card = new StackPanel { Spacing = 6 };

            // Header: provider name + enable toggle.
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock
            {
                Text = p.Name,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var enable = new ToggleSwitch { IsOn = p.Enabled, OnContent = "", OffContent = "", MinWidth = 0 };
            Grid.SetColumn(name, 0);
            Grid.SetColumn(enable, 1);
            header.Children.Add(name);
            header.Children.Add(enable);
            card.Children.Add(header);

            // API key.
            card.Children.Add(MutedLabel(Lang.Ai_ApiKey));
            var key = new PasswordBox
            {
                FontSize = 13,
                PlaceholderText = p.HasKey ? Lang.Ai_KeySaved : info.KeyHint,
            };
            card.Children.Add(key);

            // Models offered.
            card.Children.Add(MutedLabel(Lang.Ai_Models));
            var models = new List<(string, CheckBox)>();
            foreach (var m in p.AllModels)
            {
                var cb = new CheckBox { Content = m, IsChecked = p.Models.Contains(m), FontSize = 13, MinHeight = 0 };
                models.Add((m, cb));
                card.Children.Add(cb);
            }

            panel.Children.Add(new Border
            {
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                Child = card,
            });

            rows.Add(new Row { View = p, Enable = enable, Key = key, Models = models });
        }

        // Shared system prompt.
        panel.Children.Add(MutedLabel(Lang.Ai_SystemPrompt));
        var sysBox = new TextBox
        {
            Text = cfg.SystemPrompt,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 60,
            FontSize = 13,
        };
        panel.Children.Add(sysBox);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Lang.Ai_ConfigTitle,
            PrimaryButtonText = Lang.Ai_Save,
            CloseButtonText = Lang.Ai_Cancel,
            DefaultButton = ContentDialogButton.Primary,
            Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 480,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;

        // Collect edits back into the config snapshot.
        foreach (var r in rows)
        {
            r.View.Enabled = r.Enable.IsOn;
            r.View.Models  = r.Models.Where(t => t.cb.IsChecked == true).Select(t => t.model).ToList();
            var typed = (r.Key.Password ?? "").Trim();
            if (typed.Length > 0) r.View.NewApiKey = typed;
        }
        cfg.SystemPrompt = sysBox.Text.Trim();

        return await AiConfigService.SaveAsync(cfg);
    }

    private static TextBlock MutedLabel(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Opacity = 0.65,
    };
}
