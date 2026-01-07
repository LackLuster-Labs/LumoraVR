using System;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.HelioUI;
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
    /// Shared helper to add the User Inspector panel into the template.
    /// </summary>
    protected static void AttachUserInspectorPanel(Slot parent, float3 offset)
    {
        var panelSlot = parent.AddSlot("UserInspector");
        panelSlot.LocalPosition.Value = offset;
        panelSlot.LocalRotation.Value = floatQ.Euler(0f, 0f, 0f);
        panelSlot.LocalScale.Value = new float3(0.7f, 0.7f, 0.7f);

        panelSlot.AttachComponent<HelioUserInspector>();

        Logger.Log("WorldTemplates: Created User Inspector panel");
    }

    /// <summary>
    /// Shared helper to add the Engine Debug panel into the template.
    /// </summary>
    protected static void AttachEngineDebugPanel(Slot parent, float3 offset)
    {
        var panelSlot = parent.AddSlot("EngineDebug");
        panelSlot.LocalPosition.Value = offset;
        panelSlot.LocalRotation.Value = floatQ.Euler(0f, 0f, 0f);
        panelSlot.LocalScale.Value = new float3(0.35f, 0.35f, 0.35f);

        panelSlot.AttachComponent<EngineDebugWizard>();

        Logger.Log("WorldTemplates: Created Engine Debug panel");
    }
}
