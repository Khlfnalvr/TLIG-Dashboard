using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TLIGDashboard.Services;

namespace TLIGDashboard.Views;

/// <summary>
/// Shared task UI helpers used by both <see cref="LearningAnalyticPage"/> and
/// <see cref="TaskDetailPage"/>: the staff add/edit dialog and target formatting.
/// </summary>
internal static class TaskUi
{
    private static LocalizationManager Lang => App.Lang;

    /// <summary>Localized name of a structured target metric ("" for none).</summary>
    public static string MetricLabel(string metric) => metric switch
    {
        TaskMetrics.RiseTime         => Lang.Panel_RiseTime,
        TaskMetrics.Overshoot        => Lang.Panel_Overshoot,
        TaskMetrics.Settling         => Lang.Panel_Settling,
        TaskMetrics.SteadyStateError => Lang.Panel_SteadyErr,
        _                            => Lang.LearnDash_MetricNone,
    };

    /// <summary>
    /// Human-readable one-liner of a task's structured target, e.g.
    /// "Rise time &lt;= 2 ± 0.2 s". Empty string when the task has no structured target.
    /// </summary>
    public static string FormatTarget(LearningTask t)
    {
        if (!t.HasStructuredTarget) return "";
        string unit = t.Metric is TaskMetrics.RiseTime or TaskMetrics.Settling ? " s" : " %";
        string tol  = t.Tolerance > 0 ? $" ± {t.Tolerance:0.##}" : "";
        return $"{MetricLabel(t.Metric)} {t.Op} {t.Target:0.##}{tol}{unit}";
    }

    /// <summary>
    /// Shows the staff add/edit dialog. Returns a populated <see cref="LearningTask"/>
    /// (a copy carrying any existing id) on Save, or <c>null</c> if cancelled or the
    /// title was left blank.
    /// </summary>
    public static async Task<LearningTask?> ShowEditDialogAsync(XamlRoot root, LearningTask? existing)
    {
        var titleBox = new TextBox { Header = Lang.LearnDash_FieldTitle, Text = existing?.Title ?? "" };
        var objBox   = new TextBox
        {
            Header        = Lang.LearnDash_FieldObjective,
            Text          = existing?.Objective ?? "",
            AcceptsReturn = true,
            TextWrapping  = TextWrapping.Wrap,
            Height        = 90,
        };

        var metricCombo = new ComboBox { Header = Lang.LearnDash_FieldMetric, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var m in TaskMetrics.All)
            metricCombo.Items.Add(new ComboBoxItem { Content = MetricLabel(m), Tag = m });
        metricCombo.SelectedIndex = Math.Max(0, Array.IndexOf(TaskMetrics.All, existing?.Metric ?? TaskMetrics.None));

        var opCombo = new ComboBox { Header = Lang.LearnDash_FieldOperator, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var op in TaskOps.All)
            opCombo.Items.Add(new ComboBoxItem { Content = op, Tag = op });
        opCombo.SelectedIndex = Math.Max(0, Array.IndexOf(TaskOps.All, existing?.Op ?? TaskOps.Lte));

        var targetBox = new NumberBox
        {
            Header = Lang.LearnDash_FieldTarget, Value = existing?.Target ?? 0,
            SmallChange = 0.1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var tolBox = new NumberBox
        {
            Header = Lang.LearnDash_FieldTolerance, Value = existing?.Tolerance ?? 0, Minimum = 0,
            SmallChange = 0.1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(targetBox, 0);
        Grid.SetColumn(tolBox, 1);
        grid.Children.Add(targetBox);
        grid.Children.Add(tolBox);

        var metricOpGrid = new Grid { ColumnSpacing = 12 };
        metricOpGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        metricOpGrid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(metricCombo, 0);
        Grid.SetColumn(opCombo, 1);
        metricOpGrid.Children.Add(metricCombo);
        metricOpGrid.Children.Add(opCombo);

        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(titleBox);
        panel.Children.Add(objBox);
        panel.Children.Add(metricOpGrid);
        panel.Children.Add(grid);

        var dialog = new ContentDialog
        {
            Title             = existing is null ? Lang.LearnDash_DlgAddTaskTitle : Lang.LearnDash_DlgEditTaskTitle,
            Content           = new ScrollViewer { Content = panel },
            PrimaryButtonText = Lang.LearnDash_Save,
            CloseButtonText   = Lang.LearnDash_Cancel,
            DefaultButton     = ContentDialogButton.Primary,
            XamlRoot          = root,
        };

        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        catch { return null; }

        if (result != ContentDialogResult.Primary) return null;

        var title = titleBox.Text.Trim();
        if (string.IsNullOrEmpty(title)) return null; // title is required

        return new LearningTask
        {
            Id        = existing?.Id ?? "",
            Title     = title,
            Objective = objBox.Text.Trim(),
            Metric    = (metricCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? TaskMetrics.None,
            Op        = (opCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? TaskOps.Lte,
            Target    = double.IsNaN(targetBox.Value) ? 0 : targetBox.Value,
            Tolerance = double.IsNaN(tolBox.Value) ? 0 : Math.Max(0, tolBox.Value),
            CreatedBy = existing?.CreatedBy ?? "",
        };
    }
}
