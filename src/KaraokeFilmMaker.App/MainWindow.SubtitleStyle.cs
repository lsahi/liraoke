using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Validation;
using KaraokeFilmMaker.Infrastructure.Rendering;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private bool _syncingRomanization;
    private bool _subtitleStyleEditorInitialized;
    private bool _syncingSubtitleStyleFields;
    private readonly SubtitlePresetChoice[] _subtitlePresetChoices = new[] { new SubtitlePresetChoice("自定义 · 保留当前参数", null) }
        .Concat(SubtitleStylePresets.All.Select(preset => new SubtitlePresetChoice(preset.Name, preset))).ToArray();
    private sealed record SubtitlePresetChoice(string Name, SubtitleStylePreset? Preset);

    private void SubtitleStyleEditor_Loaded(object sender, RoutedEventArgs e) => InitializeSubtitleStyleEditor();

    private void InitializeSubtitleStyleEditor()
    {
        if (_subtitleStyleEditorInitialized) { return; }
        _subtitleStyleEditorInitialized = true;
        _syncingSubtitleStyleFields = true;
        try
        {
            SubtitlePresetCombo.ItemsSource = _subtitlePresetChoices;
            SubtitleFontText.ItemsSource = SystemFontCatalog.Names;
            string[] colors = ["#FFFFFFFF", "#FFFFD54F", "#FF24465F", "#FF000000", "#FF83CDE8", "#FFFFE59A", "#FF34424D"];
            foreach (ComboBox combo in new[] { InactiveColorText, ActiveColorText, OutlineColorText, ActiveOutlineColorText })
            {
                string text = combo.Text;
                combo.ItemsSource = colors;
                combo.Text = text;
            }
            SubtitleFontText.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(SubtitleFontText_TextChanged));
        }
        finally { _syncingSubtitleStyleFields = false; }
        RefreshSubtitleFontStatus(SubtitleFontText.Text);
    }

    private void SubtitleFontText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingSubtitleStyleFields || e.OriginalSource is not TextBox input) { return; }
        string text = input.Text;
        int caret = input.CaretIndex;
        _syncingSubtitleStyleFields = true;
        try
        {
            SubtitleFontText.ItemsSource = SystemFontCatalog.Search(text);
            SubtitleFontText.Text = text;
            input.CaretIndex = Math.Min(caret, input.Text.Length);
        }
        finally { _syncingSubtitleStyleFields = false; }
        RefreshSubtitleFontStatus(text);
    }

    private void SubtitleFontText_DropDownOpened(object? sender, EventArgs e)
    {
        // A chosen full family name must not trap the user in a one-item list.
        string text = SubtitleFontText.Text;
        _syncingSubtitleStyleFields = true;
        try
        {
            SubtitleFontText.ItemsSource = SystemFontCatalog.IsInstalled(text) ? SystemFontCatalog.Names : SystemFontCatalog.Search(text);
            SubtitleFontText.Text = text;
        }
        finally { _syncingSubtitleStyleFields = false; }
    }

    private void RefreshSubtitleFontStatus(string font)
    {
        SubtitleFontStatusText.Text = SystemFontCatalog.IsInstalled(font)
            ? $"使用本机字体：{SystemFontCatalog.Resolve(font)}。预览与导出一致；字体授权请以字体提供方条款为准。"
            : $"本机未安装“{font}”；预览和导出暂用 {SystemFontCatalog.Resolve(font)}。项目仍保留原字体名称，可安装字体后重启或在此重新选择。";
    }

    private void SubtitlePresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSubtitleStyleFields || SubtitlePresetCombo.SelectedItem is not SubtitlePresetChoice { Preset: { } preset }) { return; }
        InactiveColorText.Text = preset.InactiveColor;
        ActiveColorText.Text = preset.ActiveColor;
        OutlineColorText.Text = preset.OutlineColor;
        ActiveOutlineColorText.Text = preset.ActiveOutlineColor;
        OutlineWidthText.Text = preset.OutlineWidth.ToString(CultureInfo.CurrentCulture);
        ShadowOffsetText.Text = preset.ShadowOffset.ToString(CultureInfo.CurrentCulture);
    }
    private void SetRomanizationChecks(bool value)
    {
        _syncingRomanization = true;
        try { ShowRomajiCheckBox.IsChecked = value; StyleShowRomajiCheckBox.IsChecked = value; }
        finally { _syncingRomanization = false; }
    }

    private void ShowRomanizationChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingRomanization || _workspace is null || sender is not System.Windows.Controls.CheckBox check) { return; }
        bool show = check.IsChecked == true;
        var before = _workspace;
        _workspace = _workspace with { Project = _workspace.Project with
        { SubtitleStyle = _workspace.Project.SubtitleStyle with { ShowRomanization = show }, ModifiedAtUtc = DateTimeOffset.UtcNow } };
        SetRomanizationChecks(show);
        RecordHistory(before);
        _lastTapHint = null; RefreshTapHint(_playback.Position); RefreshLyricPreview(_playback.Position);
        ProjectTimeline.Focus();
    }

    private void RefreshSubtitleStyleFields()
    {
        if (_workspace is null)
        {
            return;
        }

        SubtitleStyle style = _workspace.Project.SubtitleStyle;
        InitializeSubtitleStyleEditor();
        _syncingSubtitleStyleFields = true;
        try
        {
            SetRomanizationChecks(style.ShowRomanization);
            SubtitleFontText.ItemsSource = SystemFontCatalog.Names;
            SubtitleFontText.Text = style.FontFamily;
            PrimaryFontSizeText.Text = style.PrimaryFontSize.ToString(CultureInfo.CurrentCulture);
            AnnotationFontSizeText.Text = style.AnnotationFontSize.ToString(CultureInfo.CurrentCulture);
            InactiveColorText.Text = style.InactiveColor;
            ActiveColorText.Text = style.ActiveColor;
            OutlineColorText.Text = style.OutlineColor;
            ActiveOutlineColorText.Text = style.ActiveOutlineColor ?? string.Empty;
            OutlineWidthText.Text = style.OutlineWidth.ToString(CultureInfo.CurrentCulture);
            ShadowOffsetText.Text = style.ShadowOffset.ToString(CultureInfo.CurrentCulture);
            LineGapText.Text = style.LineGap.ToString(CultureInfo.CurrentCulture);
            RegionMarginText.Text = (style.MarginRatio * 100).ToString(CultureInfo.CurrentCulture);
            SubtitlePresetCombo.SelectedItem = _subtitlePresetChoices.FirstOrDefault(choice => choice.Preset?.ApplyTo(style) == style)
                ?? _subtitlePresetChoices[0];
        }
        finally { _syncingSubtitleStyleFields = false; }
        RefreshSubtitleFontStatus(style.FontFamily);
    }

    private void ApplySubtitleStyleFields()
    {
        if (_workspace is null)
        {
            return;
        }

        var style = _workspace.Project.SubtitleStyle with
        {
            FontFamily = SubtitleFontText.Text.Trim(),
            PrimaryFontSize = ParseSize(PrimaryFontSizeText.Text, "主歌词字号"),
            AnnotationFontSize = ParseSize(AnnotationFontSizeText.Text, "辅助行字号"),
            InactiveColor = InactiveColorText.Text.Trim(),
            ActiveColor = ActiveColorText.Text.Trim(),
            OutlineColor = OutlineColorText.Text.Trim(),
            ActiveOutlineColor = string.IsNullOrWhiteSpace(ActiveOutlineColorText.Text) ? null : ActiveOutlineColorText.Text.Trim(),
            OutlineWidth = ParseSize(OutlineWidthText.Text, "描边宽度"),
            ShadowOffset = ParseSize(ShadowOffsetText.Text, "阴影偏移"),
            LineGap = ParseSize(LineGapText.Text, "辅助行间距"),
            MarginRatio = ParseSize(RegionMarginText.Text, "区域边距") / 100,
        };
        KaraokeProject updated = _workspace.Project with { SubtitleStyle = style };
        IReadOnlyList<string> errors = ProjectValidator.Validate(updated);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }

        _workspace = _workspace with { Project = updated with { ModifiedAtUtc = DateTimeOffset.UtcNow } };
    }

    private static double ParseSize(string text, string field)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value)
            || !double.IsFinite(value))
        {
            throw new InvalidOperationException($"{field}必须是有效数值。");
        }

        return value;
    }

    private async void ApplySubtitleStyleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null)
        {
            return;
        }

        await RunUiOperationAsync(() =>
        {
            ApplyLyricRowsToProject();
            ApplySubtitleStyleFields();
            StatusText.Text = "字幕样式已应用，可在预览页查看；保存项目可持久化这些设置。";
            return Task.CompletedTask;
        });
    }
}
