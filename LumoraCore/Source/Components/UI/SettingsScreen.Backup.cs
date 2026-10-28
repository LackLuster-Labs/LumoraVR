// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Helio.UI.Listing;
using Lumora.Core.Localization;

namespace Lumora.Core.Components.UI;

// BACKUP
//
// Settings out to a file and back in, by groups. The file is the person's own: on a monitor the system's save
// and open dialogs pick it, in a headset (where that dialog would open on a screen nobody is looking at) the path
// row does. Nothing comes in until the list of what it would change has been read and Apply pressed, and that
// list follows the group toggles, so switching a group off takes its rows out of the import too. -xlinka
public sealed partial class SettingsScreen
{
    private const string BackupPath = "backup";
    private const string KindProse = "prose";
    private const long BackupGroupsOrder = 100, BackupFileOrder = 300, BackupPreviewOrder = 500;
    private const int BackupPreviewRows = 40;
    private const int BackupValueChars = 34;
    private const string BackupFilter = "*.json;Lumora settings";

    private readonly HashSet<string> _backupGroups = new(StringComparer.Ordinal);
    private readonly List<ListingItem> _backupPreview = new();
    private string _backupFile = string.Empty;
    private LocaleText _backupStatus;
    private SettingsTransfer.Snapshot? _backupSnapshot;
    private string _backupSource = string.Empty;
    private List<SettingsTransfer.Change>? _backupChanges;

    private void BuildBackup()
    {
        if (_backupGroups.Count == 0)
        {
            foreach (var group in SettingsTransfer.Groups)
                _backupGroups.Add(group.Id);
        }
        if (_backupFile.Length == 0)
            _backupFile = SettingsTransfer.DefaultPath;

        BackupRow(new ListingHeader(BackupPath + ".h.groups", "Settings.Backup.Section.Groups".AsLocale("What to Include")), BackupGroupsOrder);
        long order = BackupGroupsOrder;
        foreach (var group in SettingsTransfer.Groups)
        {
            string id = group.Id;
            BackupRow(new ListingToggle("backup.group." + id, group.Label)
            {
                Read = () => _backupGroups.Contains(id),
                Write = on => SetBackupGroup(id, on),
            }, ++order);
        }
        BackupRow(new ListingLabel("backup.excluded", "Settings.Backup.Excluded".AsLocale("Never Included"))
        {
            Kind = KindProse,
            DetailText = SettingsTransfer.ExcludedSummary,
        }, ++order);

        BackupRow(new ListingHeader(BackupPath + ".h.file", "Settings.Backup.Section.File".AsLocale("File")), BackupFileOrder);
        BackupRow(new ListingTextField("backup.path", "Settings.Backup.Path".AsLocale("File"))
        {
            DetailText = "Settings.Backup.Path.Hint".AsLocale("Where Export writes and Import reads. The file picker starts here."),
            Placeholder = SettingsTransfer.DefaultPath,
            Read = () => _backupFile,
            Write = value => _backupFile = string.IsNullOrWhiteSpace(value) ? SettingsTransfer.DefaultPath : value.Trim().Trim('"'),
        }, BackupFileOrder + 1);
        BackupRow(new ListingAction("backup.export", "Settings.Backup.Export".AsLocale("Export Settings"))
        {
            DetailText = "Settings.Backup.Export.Hint".AsLocale("Save the groups above to a file to keep or share"),
            ButtonLabel = "Settings.Backup.Export.Button".AsLocale("Export"),
            Invoke = BeginBackupExport,
        }, BackupFileOrder + 2);
        BackupRow(new ListingAction("backup.import", "Settings.Backup.Import".AsLocale("Import Settings"))
        {
            DetailText = "Settings.Backup.Import.Hint".AsLocale("Read a settings file and show what it would change"),
            ButtonLabel = "Settings.Backup.Import.Button".AsLocale("Import"),
            Invoke = BeginBackupImport,
        }, BackupFileOrder + 3);
        BackupRow(new ListingLabel("backup.status", "Settings.Backup.Status".AsLocale("Result"), () => _backupStatus.Resolve())
        {
            Kind = KindProse,
        }, BackupFileOrder + 4);
        _backupStatus = "Settings.Backup.Status.Idle".AsLocale("Nothing exported or imported yet");
    }

    private T BackupRow<T>(T item, long order) where T : ListingItem
    {
        item.Order = order;
        return _entries.Add(BackupPath, item);
    }

    private IReadOnlyCollection<string> PickedBackupGroups()
    {
        var picked = new List<string>();
        foreach (var group in SettingsTransfer.Groups)
        {
            if (_backupGroups.Contains(group.Id))
                picked.Add(group.Id);
        }
        return picked;
    }

    private void SetBackupGroup(string id, bool on)
    {
        if (on)
            _backupGroups.Add(id);
        else
            _backupGroups.Remove(id);
        if (_backupSnapshot != null)
            PlanBackupImport();
        _entryView?.RefreshValues();
    }

    // In a headset, on a phone, or with no dialog service the path row is the picker.
    private static Input.IFileDialogs? BackupDialogs
    {
        get
        {
            var input = Engine.Current?.InputInterface;
            return input == null || input.IsVRActive ? null : input.FileDialogs;
        }
    }

    private void SetBackupStatus(LocaleText status)
    {
        _backupStatus = status;
        _entryView?.RefreshValues();
        MarkDirty();
    }

    // The dialogs answer on the platform's own time, so the answer is brought back onto this world's update.
    private Action<string?> OnThisWorld(Action<string> picked) => path =>
    {
        var world = World;
        if (path == null || world == null)
            return;
        world.RunSynchronously(() =>
        {
            if (!IsDestroyed)
                picked(path);
        });
    };

    private void BeginBackupExport()
    {
        if (PickedBackupGroups().Count == 0)
        {
            SetBackupStatus("Settings.Backup.Error.NoGroups".AsLocale("Switch on at least one group first."));
            return;
        }
        var dialogs = BackupDialogs;
        if (dialogs != null && dialogs.PickSave(
                "Settings.Backup.Export.Dialog".AsLocale("Export Lumora settings").Resolve(),
                FolderOf(_backupFile), Path.GetFileName(_backupFile), BackupFilter, OnThisWorld(ExportBackupTo)))
            return;
        ExportBackupTo(_backupFile);
    }

    private void ExportBackupTo(string path)
    {
        if (!path.EndsWith(SettingsTransfer.FileExtension, StringComparison.OrdinalIgnoreCase))
            path += SettingsTransfer.FileExtension;
        _backupFile = path;
        if (SettingsTransfer.TryExportToFile(path, PickedBackupGroups(), out int count, out var error))
            SetBackupStatus("Settings.Backup.Exported".AsLocale("Saved to {1}. Settings in the file: {0}", count, path));
        else
            SetBackupStatus(error);
    }

    private void BeginBackupImport()
    {
        var dialogs = BackupDialogs;
        if (dialogs != null && dialogs.PickOpen(
                "Settings.Backup.Import.Dialog".AsLocale("Import Lumora settings").Resolve(),
                FolderOf(_backupFile), BackupFilter, OnThisWorld(LoadBackupFrom)))
            return;
        LoadBackupFrom(_backupFile);
    }

    private void LoadBackupFrom(string path)
    {
        _backupFile = path;
        ClearBackupPreview();
        if (!SettingsTransfer.TryReadFile(path, out var snapshot, out var error) || snapshot == null)
        {
            SetBackupStatus(error);
            return;
        }
        _backupSnapshot = snapshot;
        _backupSource = Path.GetFileName(path);
        PlanBackupImport();
        SetBackupStatus("Settings.Backup.Loaded".AsLocale("Read {0}. Check the changes below, then apply them.", _backupSource));
    }

    private static string FolderOf(string path)
    {
        try
        {
            var folder = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(folder) ? SettingsTransfer.DefaultFolder : folder;
        }
        catch (ArgumentException)
        {
            return SettingsTransfer.DefaultFolder;
        }
    }

    // PREVIEW

    private void PlanBackupImport()
    {
        RemoveBackupPreviewRows();
        if (_backupSnapshot == null)
            return;
        _backupChanges = SettingsTransfer.Plan(_backupSnapshot, PickedBackupGroups());
        var changes = _backupChanges;

        long order = BackupPreviewOrder;
        PreviewRow(new ListingHeader(BackupPath + ".h.preview",
            "Settings.Backup.Section.Preview".AsLocale("Changes in {0}", _backupSource)), order);

        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in changes)
            groups.Add(change.GroupId);
        PreviewRow(new ListingLabel("backup.summary", changes.Count == 0
                ? "Settings.Backup.Summary.None".AsLocale("Nothing to change")
                : "Settings.Backup.Summary".AsLocale("Settings that would change: {0}", changes.Count))
        {
            Kind = KindProse,
            DetailText = _backupSnapshot.Skipped > 0
                ? "Settings.Backup.Summary.Skipped".AsLocale("Groups touched: {0}. Left out of the file: {1} (secret, tied to the computer that wrote it, or unknown here).", groups.Count, _backupSnapshot.Skipped)
                : "Settings.Backup.Summary.Groups".AsLocale("Groups touched: {0}. A setting the file leaves out goes back to its default.", groups.Count),
        }, ++order);

        for (int i = 0; i < changes.Count && i < BackupPreviewRows; i++)
        {
            var change = changes[i];
            var group = SettingsTransfer.FindGroup(change.GroupId);
            string from = DescribeBackupValue(change.Current), to = DescribeBackupValue(change.Incoming);
            PreviewRow(new ListingLabel("backup.change." + i,
                LocaleText.Plain($"{group?.Label.Resolve() ?? change.GroupId}: {SettingName(change.Key, group)}"),
                () => $"{from}  →  {to}")
            {
                }, ++order);
        }
        if (changes.Count > BackupPreviewRows)
        {
            PreviewRow(new ListingLabel("backup.more",
                "Settings.Backup.More".AsLocale("And {0} more", changes.Count - BackupPreviewRows))
            {
                }, ++order);
        }

        if (changes.Count > 0)
        {
            PreviewRow(new ListingAction("backup.apply", "Settings.Backup.Apply".AsLocale("Apply These Changes"))
            {
                DetailText = "Settings.Backup.Apply.Hint".AsLocale("Your settings change straight away"),
                ButtonLabel = "Settings.Backup.Apply.Button".AsLocale("Apply"),
                Invoke = ApplyBackupImport,
            }, ++order);
        }
        PreviewRow(new ListingAction("backup.discard", "Settings.Backup.Discard".AsLocale("Leave Everything As It Is"))
        {
            ButtonLabel = "Settings.Backup.Discard.Button".AsLocale("Discard"),
            Invoke = DiscardBackupImport,
        }, ++order);
        _entryView?.RefreshValues();
        MarkDirty();
    }

    private void PreviewRow(ListingItem item, long order)
    {
        item.Order = order;
        _backupPreview.Add(_entries.Add(BackupPath, item));
    }

    private void RemoveBackupPreviewRows()
    {
        for (int i = 0; i < _backupPreview.Count; i++)
            _entries.Remove(BackupPath, _backupPreview[i].Key);
        _backupPreview.Clear();
    }

    private void ClearBackupPreview()
    {
        RemoveBackupPreviewRows();
        _backupSnapshot = null;
        _backupChanges = null;
        _backupSource = string.Empty;
    }

    private void ApplyBackupImport()
    {
        var changes = _backupChanges;
        if (changes == null || changes.Count == 0)
            return;
        int applied = SettingsTransfer.Apply(changes);
        ClearBackupPreview();
        // The language can have changed under every label on the screen.
        OnLocaleChanged();
        SetBackupStatus("Settings.Backup.Applied".AsLocale("Applied. Settings changed: {0}", applied));
    }

    private void DiscardBackupImport()
    {
        ClearBackupPreview();
        SetBackupStatus("Settings.Backup.Discarded".AsLocale("Nothing was changed"));
    }

    private static string DescribeBackupValue(string? value)
    {
        if (value == null)
            return "Settings.Backup.Default".AsLocale("default").Resolve();
        if (value.Length == 0)
            return "Settings.Backup.Empty".AsLocale("empty").Resolve();
        string line = value.Replace('\n', ' ').Replace('\r', ' ');
        return line.Length <= BackupValueChars ? line : line.Substring(0, BackupValueChars - 1) + "…";
    }

    // The part of the key after its group's prefix, with a single-word tail spaced out ("MaxFps" -> "Max Fps").
    private static string SettingName(string key, SettingsTransfer.Group? group)
    {
        string tail = key;
        if (group != null)
        {
            foreach (var prefix in group.Prefixes)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    tail = key.Substring(prefix.Length);
                    break;
                }
            }
        }
        int dot = tail.LastIndexOf('.');
        if (key.StartsWith("Engine.", StringComparison.Ordinal) && dot >= 0)
            tail = tail.Substring(dot + 1);
        if (tail.IndexOf('.') >= 0 || tail.Length == 0)
            return tail;
        var spaced = new StringBuilder(tail.Length + 4);
        for (int i = 0; i < tail.Length; i++)
        {
            if (i > 0 && char.IsUpper(tail[i]) && !char.IsUpper(tail[i - 1]))
                spaced.Append(' ');
            spaced.Append(tail[i]);
        }
        return spaced.ToString();
    }
}
