using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace LumoraVR.Source.Scene.Editors;

public partial class HierarchyEditor : PanelContainer
{
    [Export] public PrefabEditor PrefabEditor;
    [ExportGroup("Internal")]
    [Export] public Tree Tree;
    [Export] public OptionButton AddOption;
    [Export] public Button AddButton;

    public RootObjectType RootType => PrefabEditor.Type;
    public Prefab Prefab => PrefabEditor.EditingPrefab;

    public override void _Ready()
    {
        base._Ready();

        PrefabEditor.HierarchyChanged += GenerateHierarchy;
        var allowedAdd = RootType.AllowedChildObjects().OrderBy(i => (int)i);
        foreach (var item in allowedAdd) AddOption.AddItem(item.ToString(), (int)item);

        AddButton.Pressed += AddButtonOnPressed;

        GenerateHierarchy();
    }
    private void AddButtonOnPressed()
    {
        var prefab = Prefab;
        var index = (ChildObjectType)AddOption.GetSelectedId();
        if (RootType.AllowedChildObjects().Contains(index))
        {
            var usedIndices = prefab.Children.Select(i => i.Key).OrderBy(i => i).ToArray();
            var ind = usedIndices.FirstOrDefault();
            while (usedIndices.Contains(ind)) ind++;
            var child = new PrefabChild
            {
                Type = index,
                Name = index.ToString(),
                Parent = -1,
            };
            Prefab.Children.Add(ind, child);
            PrefabEditor.HierarchyChanged();
        }
    }
    public void GenerateHierarchy()
    {
        var prefab = Prefab;

        Tree.Clear();
        var root = Tree.CreateItem();

        Tree.HideRoot = true;

        if (prefab is null) return;

        var items = new Dictionary<int, TreeItem>();

        var childrenToDo = prefab.Children.ToDictionary(i => i.Key, i => i.Value);

        for (var i = 0; i < 64; i++)
        {
            foreach (var (index, child) in childrenToDo.ToList())
            {
                if (items.TryGetValue(child.Parent, out var newParent) || child.Parent < 0)
                {
                    newParent ??= root;
                    var item = Tree.CreateItem(newParent);
                    item.SetText(0, child.Name);
                    items.Add(index, item);
                    childrenToDo.Remove(index);
                }
            }
            if (childrenToDo.Count == 0) break;
        }
    }
}
