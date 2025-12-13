using System;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.GodotUI.Wizards;
using Lumora.Core.Logging;
using Lumora.Core.Math;

namespace Lumora.Core.Templates;

/// <summary>
/// Base class for defining a world template.
/// </summary>
public abstract class WorldTemplateDefinition
{
    /// <summary>
    /// Template name used in <see cref="WorldTemplates"/>.
    /// </summary>
    public string TemplateName { get; }

    protected WorldTemplateDefinition(string templateName)
    {
        TemplateName = templateName ?? throw new ArgumentNullException(nameof(templateName));
    }

    /// <summary>
    /// Apply the template to the provided world instance.
    /// </summary>
    public void Apply(World world)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));

        Logger.Log($"WorldTemplates: Initializing {TemplateName} world");
        Build(world);
        PostBuild(world);
    }

    protected abstract void Build(World world);

    protected virtual void PostBuild(World world)
    {
        Logger.Log($"WorldTemplates: {TemplateName} initialized with {world.RootSlot.ChildCount} root slots");
    }

    /// <summary>
    /// Shared helper to add a Godot-based User Inspector panel.
    /// Uses native Godot UI loaded from a .tscn scene file.
    /// </summary>
    protected static void AttachGodotUserInspectorPanel(Slot parent, float3 offset)
    {
        var panelSlot = parent.AddSlot("GodotUserInspector");
        panelSlot.LocalPosition.Value = offset;
        panelSlot.LocalRotation.Value = floatQ.Euler(0f, 0f, 0f);
        panelSlot.LocalScale.Value = new float3(1f, 1f, 1f);

        var inspector = panelSlot.AttachComponent<GodotUserInspector>();
        inspector.Size.Value = new float2(500, 600);
        inspector.PixelsPerUnit.Value = 800f;

        Logger.Log("WorldTemplates: Created Godot User Inspector panel");
    }

    /// <summary>
    /// Shared helper to add a Godot-based Engine Debug panel.
    /// Displays world performance and memory statistics.
    /// </summary>
    protected static void AttachGodotEngineDebugPanel(Slot parent, float3 offset)
    {
        var panelSlot = parent.AddSlot("GodotEngineDebug");
        panelSlot.LocalPosition.Value = offset;
        panelSlot.LocalRotation.Value = floatQ.Euler(0f, 0f, 0f);
        panelSlot.LocalScale.Value = new float3(1f, 1f, 1f);

        var debugPanel = panelSlot.AttachComponent<GodotEngineDebug>();
        debugPanel.Size.Value = new float2(600, 700);
        debugPanel.PixelsPerUnit.Value = 800f;

        Logger.Log("WorldTemplates: Created Godot Engine Debug panel");
    }
}
