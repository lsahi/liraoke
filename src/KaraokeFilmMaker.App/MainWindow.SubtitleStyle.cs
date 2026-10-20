using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Validation;
using KaraokeFilmMaker.Infrastructure.Rendering;
using KaraokeFilmMaker.Infrastructure.Persistence;
using KaraokeFilmMaker.Application.Preferences;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private bool _syncingRomanization;
    private bool _subtitleStyleEditorInitialized;
    private bool _syncingSubtitleStyleFields;
    private bool? _draftStyleRomanization;
    private readonly UserPreferenceStore _userPreferenceStore;
    private SubtitlePresetChoice[] _subtitlePresetChoices = new[] { new SubtitlePresetChoice("自定义 · 保留当前参数", null) }
        .Concat(SubtitleStylePresets.All.Select(preset => new SubtitlePresetChoice(preset.Name, preset))).ToArray();
    private sealed record SubtitlePresetChoice(string Name, SubtitleStylePreset? Preset, SavedSubtitleStyle? Saved = null);

    private void SubtitleStyleEditor_Loaded(object sender, RoutedEventArgs e) => InitializeSubtitleStyleEditor();

    private void InitializeSubtitleStyleEditor()
    {
        if (_subtitleStyleEditorInitialized) { return; }
        _subtitleStyleEditorInitialized = true;
        ReloadUserStyles();
        _syncingSubtitleStyleFields = true;
        try
        {
            SubtitlePresetCombo.ItemsSource = _subtitlePresetChoices;
            SubtitleFontText.ItemsSource = SystemFontCatalog.Names;
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
        if (_syncingSubtitleStyleFields) return;
        DeleteUserStyleButton.IsEnabled = SubtitlePresetCombo.SelectedItem is SubtitlePresetChoice { Saved: not null };
        if (SubtitlePresetCombo.SelectedItem is SubtitlePresetChoice { Saved: { } saved })
        {
            PopulateSubtitleStyleFields(saved.Style, draft: true);
            UserStyleNameText.Text = saved.Name;
            StatusText.Text = "已载入自定义样式草稿，点击“应用字幕样式”后改变当前工程。";
            return;
        }
        if (SubtitlePresetCombo.SelectedItem is not SubtitlePresetChoice { Preset: { } preset }) return;
        InactiveColorText.Text = preset.InactiveColor;
        ActiveColorText.Text = preset.ActiveColor;
        OutlineColorText.Text = preset.OutlineColor;
        ActiveOutlineColorText.Text = preset.ActiveOutlineColor;
        OutlineWidthText.Text = preset.OutlineWidth.ToString(CultureInfo.CurrentCulture);
        ShadowOffsetText.Text = preset.ShadowOffset.ToString(CultureInfo.CurrentCulture);
    }
    private void SetRomanizationChecks(bool value)
    {
        _draftStyleRomanization = null;
        _syncingRomanization = true;
        try { ShowRomajiCheckBox.IsChecked = value; StyleShowRomajiCheckBox.IsChecked = value; }
        finally { _syncingRomanization = false; }
    }

    private void ShowRomanizationChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingRomanization || sender is not System.Windows.Controls.CheckBox check) { return; }
        bool show = check.IsChecked == true;
        if (_workspace is null) { _draftStyleRomanization = show; return; }
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
        PopulateSubtitleStyleFields(style);
    }

    private void PopulateSubtitleStyleFields(SubtitleStyle style, bool draft = false)
    {
        _syncingSubtitleStyleFields = true;
        try
        {
            if (draft)
            {
                _draftStyleRomanization = style.ShowRomanization;
                _syncingRomanization = true;
                try { StyleShowRomajiCheckBox.IsChecked = style.ShowRomanization; }
                finally { _syncingRomanization = false; }
            }
            else SetRomanizationChecks(style.ShowRomanization);
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
            if (!draft)
                SubtitlePresetCombo.SelectedItem = _subtitlePresetChoices.FirstOrDefault(choice => choice.Saved?.Style == style)
                    ?? _subtitlePresetChoices.FirstOrDefault(choice => choice.Preset?.ApplyTo(style) == style)
                    ?? _subtitlePresetChoices[0];
            DeleteUserStyleButton.IsEnabled = SubtitlePresetCombo.SelectedItem is SubtitlePresetChoice { Saved: not null };
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

        var style = ReadSubtitleStyleFields();
        KaraokeProject updated = _workspace.Project with { SubtitleStyle = style };
        IReadOnlyList<string> errors = ProjectValidator.Validate(updated);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }

        _workspace = _workspace with { Project = updated with { ModifiedAtUtc = DateTimeOffset.UtcNow } };
        SetRomanizationChecks(style.ShowRomanization);
    }

    private SubtitleStyle ReadSubtitleStyleFields()
    {
        var style = (_workspace?.Project.SubtitleStyle ?? KaraokeProject.Create("Style", DateTimeOffset.UnixEpoch).SubtitleStyle) with
        {
            ShowRomanization = _draftStyleRomanization ?? _workspace?.Project.SubtitleStyle.ShowRomanization ?? StyleShowRomajiCheckBox.IsChecked == true,
            FontFamily = SubtitleFontText.Text.Trim(),
            PrimaryFontSize = ParseSize(PrimaryFontSizeText.Text, "主歌词字号"),
            AnnotationFontSize = ParseSize(AnnotationFontSizeText.Text, "辅助行字号"),
            InactiveColor = InactiveColorText.ColorValue!,
            ActiveColor = ActiveColorText.ColorValue!,
            OutlineColor = OutlineColorText.ColorValue!,
            ActiveOutlineColor = ActiveOutlineColorText.ColorValue,
            OutlineWidth = ParseSize(OutlineWidthText.Text, "描边宽度"),
            ShadowOffset = ParseSize(ShadowOffsetText.Text, "阴影偏移"),
            LineGap = ParseSize(LineGapText.Text, "辅助行间距"),
            MarginRatio = ParseSize(RegionMarginText.Text, "区域边距") / 100,
        };
        KaraokeProject updated = KaraokeProject.Create("Style validation", DateTimeOffset.UnixEpoch) with { SubtitleStyle = style };
        IReadOnlyList<string> errors = ProjectValidator.Validate(updated);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }

        return style;
    }

    private void ReloadUserStyles(Guid? selected = null)
    {
        var preferences = new UserPreferences();
        try
        {
            preferences = _userPreferenceStore.Load();
            UserPreferenceStatusText.Text = "全局配置：user-preferences.json · 保存于程序目录，跨工程共用。";
            UserPreferenceStatusText.ToolTip = _userPreferenceStore.FilePath;
        }
        catch (Exception e) when (e is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException)
        {
            UserPreferenceStatusText.Text = $"无法读取用户偏好：{e.Message} 内置样式仍可使用；不会自动覆盖原配置。";
        }
        _subtitlePresetChoices = new[] { new SubtitlePresetChoice("自定义 · 保留当前参数", null) }
            .Concat(SubtitleStylePresets.All.Select(preset => new SubtitlePresetChoice(preset.Name, preset)))
            .Concat(preferences.SubtitleStyles.Select(saved => new SubtitlePresetChoice("我的 · " + saved.Name, null, saved))).ToArray();
        bool previous = _syncingSubtitleStyleFields;
        _syncingSubtitleStyleFields = true;
        try
        {
            SubtitlePresetCombo.ItemsSource = _subtitlePresetChoices;
            SubtitlePresetCombo.SelectedItem = _subtitlePresetChoices.FirstOrDefault(choice => selected is not null && choice.Saved?.Id == selected)
                ?? _subtitlePresetChoices[0];
            DeleteUserStyleButton.IsEnabled = SubtitlePresetCombo.SelectedItem is SubtitlePresetChoice { Saved: not null };
        }
        finally { _syncingSubtitleStyleFields = previous; }
    }

    private void SaveNamedUserStyle(string name, bool overwrite)
    {
        var preferences = _userPreferenceStore.SaveStyle(name, ReadSubtitleStyleFields(), overwrite);
        var saved = preferences.SubtitleStyles.Single(item => string.Equals(item.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        ReloadUserStyles(saved.Id);
        StatusText.Text = $"已保存全局样式“{saved.Name}”；其他工程也可调用，当前工程尚未应用的参数保持草稿。";
    }

    private void SaveUserStyle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string name = UserStyleNameText.Text.Trim();
            bool exists = _userPreferenceStore.Load().SubtitleStyles.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (exists && MessageBox.Show(this, $"覆盖已保存的样式“{name}”？已使用该样式的工程不会被修改。",
                "覆盖自定义样式", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            SaveNamedUserStyle(name, exists);
        }
        catch (Exception e1) when (e1 is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { ShowPreferenceError(e1); }
    }

    private void DeleteUserStyle_Click(object sender, RoutedEventArgs e)
    {
        if (SubtitlePresetCombo.SelectedItem is not SubtitlePresetChoice { Saved: { } saved }) return;
        if (MessageBox.Show(this, $"删除常用样式“{saved.Name}”？不会改变任何工程已应用的样式。",
            "删除自定义样式", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        try { _userPreferenceStore.DeleteStyle(saved.Id); ReloadUserStyles(); StatusText.Text = "已删除常用样式，当前工程样式保持不变。"; }
        catch (Exception e1) when (e1 is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException) { ShowPreferenceError(e1); }
    }

    private void ShowPreferenceError(Exception error)
    {
        UserPreferenceStatusText.Text = $"未能保存用户偏好：{error.Message} 请检查程序目录写入权限及配置文件；可将软件放到可写文件夹。原配置不会被清空。";
        StatusText.Text = UserPreferenceStatusText.Text;
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
