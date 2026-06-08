using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TLIGDashboard.Services;
using TLIGDashboard.Views;

namespace TLIGDashboard.Controls;

/// <summary>
/// Compact AI provider + model picker shared by <see cref="AIPage"/> and the
/// Dashboard chat. Loads the enabled providers/models from the server (or local
/// store), lets the user pick a model, persists the choice to local settings, and
/// re-points the shared <see cref="App.Ai"/> service via
/// <see cref="AiConfigService.ApplyActive"/>. Staff get a gear button to open the
/// provider-configuration dialog.
/// </summary>
public sealed partial class AiModelPicker : UserControl
{
    private LocalizationManager Lang => App.Lang;

    private AiConfigResult _config = new();
    private bool _suppressPicker;

    /// <summary>Raised after the active provider/model changed (host may refresh status text).</summary>
    public event Action? SelectionChanged;

    public AiModelPicker()
    {
        InitializeComponent();
        ToolTipService.SetToolTip(ConfigBtn, Lang.Ai_ConfigTitle);
    }

    /// <summary>Fetches provider config from the server (or local store) and fills the pickers.</summary>
    public async System.Threading.Tasks.Task ReloadAsync()
    {
        try { _config = await AiConfigService.LoadAsync(); }
        catch { _config = new AiConfigResult(); }

        var settings = AppSettingsService.Load();
        var usable   = _config.Usable.ToList();

        ConfigBtn.Visibility = _config.CanEdit ? Visibility.Visible : Visibility.Collapsed;

        _suppressPicker = true;
        ProviderCombo.Items.Clear();
        foreach (var p in usable)
            ProviderCombo.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id });

        if (usable.Count == 0)
        {
            ModelCombo.Items.Clear();
            ProviderCombo.IsEnabled = false;
            ModelCombo.IsEnabled    = false;
            PickerStatus.Text = _config.CanEdit ? Lang.Ai_NoProvidersStaff : Lang.Ai_NoProvidersUser;
            _suppressPicker = false;
            AiConfigService.ApplyActive(App.Ai);
            SelectionChanged?.Invoke();
            return;
        }

        ProviderCombo.IsEnabled = true;
        ModelCombo.IsEnabled    = true;
        PickerStatus.Text       = "";

        // Prefer the user's last pick when still usable, else the server default, else the first.
        string wantProvider = settings.AiActiveProvider;
        if (usable.All(p => p.Id != wantProvider)) wantProvider = _config.ActiveProvider;
        if (usable.All(p => p.Id != wantProvider)) wantProvider = usable[0].Id;

        SelectProviderItem(wantProvider);
        PopulateModels(wantProvider, PreferredModel(wantProvider, settings));
        _suppressPicker = false;

        PersistAndApply();
    }

    private void SelectProviderItem(string id)
    {
        for (int i = 0; i < ProviderCombo.Items.Count; i++)
            if (ProviderCombo.Items[i] is ComboBoxItem it && (string?)it.Tag == id)
            { ProviderCombo.SelectedIndex = i; return; }
        if (ProviderCombo.Items.Count > 0) ProviderCombo.SelectedIndex = 0;
    }

    private string SelectedProviderId => (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
    private string SelectedModel      => ModelCombo.SelectedItem as string ?? "";

    private string PreferredModel(string providerId, AppSettings settings)
    {
        var models = _config.Provider(providerId)?.Models ?? new List<string>();
        if (providerId == settings.AiActiveProvider &&
            !string.IsNullOrWhiteSpace(settings.AiActiveModel) &&
            models.Contains(settings.AiActiveModel))
            return settings.AiActiveModel;
        if (providerId == _config.ActiveProvider &&
            !string.IsNullOrWhiteSpace(_config.ActiveModel) &&
            models.Contains(_config.ActiveModel))
            return _config.ActiveModel;
        return models.FirstOrDefault() ?? "";
    }

    private void PopulateModels(string providerId, string selectModel)
    {
        ModelCombo.Items.Clear();
        var view = _config.Provider(providerId);
        if (view is null) return;
        foreach (var m in view.Models) ModelCombo.Items.Add(m);
        int idx = view.Models.IndexOf(selectModel);
        ModelCombo.SelectedIndex = idx >= 0 ? idx : (view.Models.Count > 0 ? 0 : -1);
    }

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPicker) return;
        var pid = SelectedProviderId;
        _suppressPicker = true;
        PopulateModels(pid, PreferredModel(pid, AppSettingsService.Load()));
        _suppressPicker = false;
        PersistAndApply();
    }

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPicker) return;
        PersistAndApply();
    }

    /// <summary>Saves the current provider/model pick locally and re-points the AI service.</summary>
    private void PersistAndApply()
    {
        var pid   = SelectedProviderId;
        var model = SelectedModel;
        if (!string.IsNullOrEmpty(pid))
        {
            var s = AppSettingsService.Load();
            s.AiActiveProvider = pid;
            if (!string.IsNullOrEmpty(model)) s.AiActiveModel = model;
            AppSettingsService.Save(s);
        }
        AiConfigService.ApplyActive(App.Ai);
        SelectionChanged?.Invoke();
    }

    private async void ConfigBtn_Click(object sender, RoutedEventArgs e)
    {
        bool saved = await AiConfigUi.ShowProviderConfigAsync(XamlRoot);
        if (saved) await ReloadAsync();
    }
}
