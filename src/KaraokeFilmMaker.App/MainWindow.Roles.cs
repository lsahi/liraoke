using System.Windows;
using System.Windows.Controls;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Validation;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private Guid? _editingStyleRoleId, _rolesProjectId;
    private bool _syncingRoles;
    private sealed record StyleTarget(Guid? Id, string Name);
    private SubtitleStyle EditingStyle => _workspace?.Project.Roles.FirstOrDefault(r => r.Id == _editingStyleRoleId)?.Style
        ?? _workspace?.Project.SubtitleStyle ?? KaraokeProject.Create("Style", DateTimeOffset.UnixEpoch).SubtitleStyle;

    private void RefreshRoleControls()
    {
        if (ProjectRolesList is null) return;
        if (_rolesProjectId != _workspace?.Project.Id)
        {
            _rolesProjectId = _workspace?.Project.Id; _editingStyleRoleId = null;
            ClearRoleSelection();
        }
        var selected = (ProjectRolesList.SelectedItem as SingingRole)?.Id;
        var roles = _workspace?.Project.Roles ?? [];
        if (_editingStyleRoleId is { } id && !roles.Any(r => r.Id == id)) _editingStyleRoleId = null;
        _syncingRoles = true;
        try
        {
            ProjectRolesList.ItemsSource = roles;
            ProjectRolesList.SelectedItem = roles.FirstOrDefault(r => r.Id == selected);
            SubtitleStyleTargetCombo.ItemsSource = new[] { new StyleTarget(null, "标准（无角色）") }
                .Concat(roles.Select(r => new StyleTarget(r.Id, r.Name))).ToArray();
            SubtitleStyleTargetCombo.SelectedItem = SubtitleStyleTargetCombo.Items.Cast<StyleTarget>()
                .First(t => t.Id == _editingStyleRoleId);
        }
        finally { _syncingRoles = false; }
        SaveEditedRoleButton.IsEnabled = _editingStyleRoleId is not null;
        try { SavedRolesCombo.ItemsSource = _userPreferenceStore.Load().Roles; }
        catch (Exception e) when (e is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException)
        { StatusText.Text = $"常用角色读取失败：{e.Message}"; }
        RefreshRoleAssignmentChoices();
    }

    private void ProjectRole_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingRoles && ProjectRolesList.SelectedItem is SingingRole role) RoleNameText.Text = role.Name;
    }

    private async void AddRole_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null) return;
        await RunUiOperationAsync(() =>
        {
            string name = ValidRoleName(RoleNameText.Text);
            ApplySubtitleStyleFields();
            CommitRoleProject(_workspace.Project with { Roles = [.. _workspace.Project.Roles, new(Guid.NewGuid(), name, _workspace.Project.SubtitleStyle)] });
            StatusText.Text = $"已添加角色“{name}”。编辑样式后可保存到常用配置。";
            return Task.CompletedTask;
        });
    }

    private string ValidRoleName(string text, Guid? except = null)
    {
        string name = text.Trim();
        if (name.Length is 0 or > 80 || name.Any(char.IsControl)) throw new InvalidOperationException("角色名需要 1–80 个字符。");
        if (_workspace?.Project.Roles.Any(r => r.Id != except && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) == true)
            throw new InvalidOperationException("工程中已有同名角色。");
        return name;
    }

    private async void RenameRole_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || ProjectRolesList.SelectedItem is not SingingRole role) return;
        await RunUiOperationAsync(() =>
        {
            string name = ValidRoleName(RoleNameText.Text, role.Id);
            ApplySubtitleStyleFields();
            CommitRoleProject(_workspace.Project with { Roles = _workspace.Project.Roles.Select(r => r.Id == role.Id ? r with { Name = name } : r).ToArray() });
            return Task.CompletedTask;
        });
    }

    private async void RemoveRole_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || ProjectRolesList.SelectedItem is not SingingRole role) return;
        if (MessageBox.Show(this, $"移除工程角色“{role.Name}”及歌词中的对应分配？其他角色、歌词与全部打点不变，常用配置不删除。",
            "移除角色", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        await RunUiOperationAsync(() =>
        {
            ApplySubtitleStyleFields();
            CommitRoleProject(RoleAssignments.Remove(_workspace.Project, role.Id));
            RefreshSubtitleStyleFields();
            return Task.CompletedTask;
        });
    }

    private void CommitRoleProject(KaraokeProject project)
    {
        var errors = ProjectValidator.Validate(project);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        _workspace = _workspace! with { Project = project with { ModifiedAtUtc = DateTimeOffset.UtcNow } };
        RefreshLyricRows(); RefreshRoleControls();
        _lastTapHint = null; RefreshTapHint(_playback.Position); RefreshLyricPreview(_playback.Position);
    }

    private void EditRoleStyle_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectRolesList.SelectedItem is not SingingRole role) return;
        SubtitleStyleTargetCombo.SelectedItem = SubtitleStyleTargetCombo.Items.Cast<StyleTarget>().First(t => t.Id == role.Id);
        EditorTabs.SelectedItem = SubtitleStyleTab;
    }

    private void SubtitleStyleTarget_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingRoles || SubtitleStyleTargetCombo.SelectedItem is not StyleTarget target || _workspace is null) return;
        var before = _workspace;
        try
        {
            ApplySubtitleStyleFields();
            RecordHistory(before);
            _editingStyleRoleId = target.Id;
            SaveEditedRoleButton.IsEnabled = target.Id is not null;
            PopulateSubtitleStyleFields(EditingStyle, draft: target.Id is not null);
            RefreshLyricPreview(_playback.Position);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            StatusText.Text = error.Message;
            _syncingRoles = true;
            SubtitleStyleTargetCombo.SelectedItem = SubtitleStyleTargetCombo.Items.Cast<StyleTarget>().First(t => t.Id == _editingStyleRoleId);
            _syncingRoles = false;
        }
    }

    private void SaveRolePreset_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectRolesList.SelectedItem is not SingingRole selected || _workspace is null) return;
        try
        {
            var before = _workspace; ApplySubtitleStyleFields(); RecordHistory(before);
            var role = _workspace.Project.Roles.First(r => r.Id == selected.Id);
            bool exists = _userPreferenceStore.Load().Roles.Any(r => string.Equals(r.Name, role.Name, StringComparison.OrdinalIgnoreCase));
            if (exists && MessageBox.Show(this, $"覆盖常用角色“{role.Name}”的样式？不会改动其他工程。",
                "保存常用角色", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            _userPreferenceStore.SaveRole(role.Name, role.Style, exists);
            RefreshRoleControls(); StatusText.Text = $"角色“{role.Name}”与样式已保存到 user-preferences.json。";
        }
        catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { ShowPreferenceError(error); }
    }

    private void SaveEditedRole_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || _editingStyleRoleId is null) return;
        RefreshRoleControls();
        ProjectRolesList.SelectedItem = _workspace.Project.Roles.FirstOrDefault(r => r.Id == _editingStyleRoleId);
        SaveRolePreset_Click(sender, e);
    }

    private async void ImportRolePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || SavedRolesCombo.SelectedItem is not SingingRole preset) return;
        await RunUiOperationAsync(() =>
        {
            string name = ValidRoleName(preset.Name);
            ApplySubtitleStyleFields();
            CommitRoleProject(_workspace.Project with { Roles = [.. _workspace.Project.Roles, preset with { Id = Guid.NewGuid(), Name = name }] });
            return Task.CompletedTask;
        });
    }

    private async void ApplySongTitle()
    {
        if (_workspace is null) return;
        await RunUiOperationAsync(() =>
        {
            ApplySubtitleStyleFields();
            RefreshLyricPreview(_playback.Position);
            StatusText.Text = "曲名设置已应用；保存工程可保留。";
            return Task.CompletedTask;
        });
    }
}
