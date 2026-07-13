using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PublishTool.App.Models;

namespace PublishTool.App.Views;

public partial class GroupDialog : Window
{
    private readonly List<ProjectEntry> _allProjects;
    private List<ProjectGroup> _groups;
    private ObservableCollection<GroupMemberVm> _orderedMembers = new();
    private ObservableCollection<AvailableProjectVm> _availableProjects = new();
    private ProjectGroup? _currentGroup;

    public GroupDialog(List<ProjectGroup> groups, List<ProjectEntry> allProjects)
    {
        InitializeComponent();
        _allProjects = allProjects;
        _groups = groups;

        LstGroups.ItemsSource = _groups;
        LstGroups.DisplayMemberPath = "Name";
        LstMembers.ItemsSource = _orderedMembers;
        LstAvailable.ItemsSource = _availableProjects;

        if (_groups.Count > 0)
            LstGroups.SelectedIndex = 0;
    }

    private void LstGroups_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Save current group before switching
        if (_currentGroup != null)
        {
            if (!string.IsNullOrWhiteSpace(TxtGroupName.Text))
                _currentGroup.Name = TxtGroupName.Text.Trim();
            _currentGroup.BuildScript = string.IsNullOrWhiteSpace(TxtBuildScript.Text) ? null : TxtBuildScript.Text.Trim();
        }

        _currentGroup = LstGroups.SelectedItem as ProjectGroup;
        if (_currentGroup == null) return;

        TxtGroupName.Text = _currentGroup.Name;
        TxtBuildScript.Text = _currentGroup.BuildScript ?? string.Empty;

        // Build ordered members list
        _orderedMembers.Clear();
        var projectMap = _allProjects.ToDictionary(p => p.Id);
        for (int i = 0; i < _currentGroup.ProjectIds.Count; i++)
        {
            if (projectMap.TryGetValue(_currentGroup.ProjectIds[i], out var proj))
            {
                _orderedMembers.Add(new GroupMemberVm
                {
                    Order = i + 1,
                    Name = proj.Name,
                    Type = proj.Type.ToString(),
                    PublishDir = proj.PublishDir,
                    ProjectId = proj.Id
                });
            }
        }

        // Build available projects checkbox list
        _availableProjects.Clear();
        foreach (var proj in _allProjects)
        {
            _availableProjects.Add(new AvailableProjectVm
            {
                Name = proj.Name,
                Type = proj.Type.ToString(),
                PublishDir = proj.PublishDir,
                ProjectId = proj.Id,
                IsInGroup = _currentGroup.ProjectIds.Contains(proj.Id)
            });
        }

        TxtEmptyHint.Visibility = _orderedMembers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MemberCheck_Click(object sender, RoutedEventArgs e)
    {
        var cb = sender as CheckBox;
        if (cb?.DataContext is AvailableProjectVm vm)
        {
            if (cb.IsChecked == true)
                AddToGroup(vm.ProjectId);
            else
                RemoveFromGroup(vm.ProjectId);
        }
    }

    private void AddToGroup(string projectId)
    {
        if (_currentGroup == null) return;
        if (_currentGroup.ProjectIds.Contains(projectId)) return;

        var proj = _allProjects.FirstOrDefault(p => p.Id == projectId);
        if (proj == null) return;

        _currentGroup.ProjectIds.Add(projectId);
        _orderedMembers.Add(new GroupMemberVm
        {
            Order = _orderedMembers.Count + 1,
            Name = proj.Name,
            Type = proj.Type.ToString(),
            PublishDir = proj.PublishDir,
            ProjectId = proj.Id
        });
        UpdateAvailableCheck(projectId, true);
        TxtEmptyHint.Visibility = _orderedMembers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RemoveFromGroup(string projectId)
    {
        if (_currentGroup == null) return;

        _currentGroup.ProjectIds.Remove(projectId);
        var item = _orderedMembers.FirstOrDefault(m => m.ProjectId == projectId);
        if (item != null)
            _orderedMembers.Remove(item);

        RenumberMembers();
        UpdateAvailableCheck(projectId, false);
        TxtEmptyHint.Visibility = _orderedMembers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateAvailableCheck(string projectId, bool isInGroup)
    {
        var vm = _availableProjects.FirstOrDefault(a => a.ProjectId == projectId);
        if (vm != null)
            vm.IsInGroup = isInGroup;
        LstAvailable.Items.Refresh();
    }

    private void RenumberMembers()
    {
        for (int i = 0; i < _orderedMembers.Count; i++)
            _orderedMembers[i].Order = i + 1;
        LstMembers.Items.Refresh();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGroup == null) return;
        var selected = LstMembers.SelectedItem as GroupMemberVm;
        if (selected == null) return;

        var idx = _orderedMembers.IndexOf(selected);
        if (idx <= 0) return;

        _orderedMembers.Move(idx, idx - 1);
        _currentGroup.ProjectIds.RemoveAt(idx);
        _currentGroup.ProjectIds.Insert(idx - 1, selected.ProjectId);
        RenumberMembers();
        LstMembers.SelectedItem = selected;
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGroup == null) return;
        var selected = LstMembers.SelectedItem as GroupMemberVm;
        if (selected == null) return;

        var idx = _orderedMembers.IndexOf(selected);
        if (idx < 0 || idx >= _orderedMembers.Count - 1) return;

        _orderedMembers.Move(idx, idx + 1);
        _currentGroup.ProjectIds.RemoveAt(idx);
        _currentGroup.ProjectIds.Insert(idx + 1, selected.ProjectId);
        RenumberMembers();
        LstMembers.SelectedItem = selected;
    }

    private void AddGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = new ProjectGroup { Name = "新项目组" };
        _groups.Add(group);
        LstGroups.Items.Refresh();
        LstGroups.SelectedItem = group;
    }

    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = LstGroups.SelectedItem as ProjectGroup;
        if (group == null) return;

        if (MessageBox.Show("确定要删除项目组" + group.Name + "吗？", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _groups.Remove(group);
            LstGroups.Items.Refresh();
            if (_groups.Count > 0)
                LstGroups.SelectedIndex = 0;
            else
            {
                _orderedMembers.Clear();
                _availableProjects.Clear();
            }
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_currentGroup != null && !string.IsNullOrWhiteSpace(TxtGroupName.Text))
            _currentGroup.Name = TxtGroupName.Text.Trim();
        _currentGroup.BuildScript = string.IsNullOrWhiteSpace(TxtBuildScript.Text) ? null : TxtBuildScript.Text.Trim();
        base.OnClosing(e);
    }

    
    
    private string GetFirstProjectDir()
    {
        if (_currentGroup != null && _currentGroup.ProjectIds.Count > 0)
        {
            var firstProj = _allProjects.FirstOrDefault(p => p.Id == _currentGroup.ProjectIds[0]);
            if (firstProj != null)
            {
                return System.IO.File.Exists(firstProj.ProjectPath)
                    ? System.IO.Path.GetDirectoryName(firstProj.ProjectPath)!
                    : firstProj.ProjectPath;
            }
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private void BrowseBuildScript_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择编译脚本",
            Filter = "脚本文件|*.bat;*.cmd;*.ps1;*.sh|所有文件|*.*",
            InitialDirectory = GetFirstProjectDir()
        };
        if (dlg.ShowDialog() == true)
            TxtBuildScript.Text = dlg.FileName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGroup != null)
        {
            var name = TxtGroupName.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("请输入组名称", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _currentGroup.Name = name;
            _currentGroup.BuildScript = string.IsNullOrWhiteSpace(TxtBuildScript.Text) ? null : TxtBuildScript.Text.Trim();
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

public class GroupMemberVm
{
    public int Order { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string PublishDir { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
}

public class AvailableProjectVm
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string PublishDir { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public bool IsInGroup { get; set; }
}