using System;
using System.Collections.Generic;
using Lumora.Core;

namespace Lumora.Core.Templates;

public static class WorldTemplates
{
    private const string EmptyTemplateName = "Empty";
    private const string LocalHomeTemplateName = "LocalHome";
    private const string GridTemplateName = "Grid";
    private const string SocialSpaceTemplateName = "SocialSpace";

    private static readonly IReadOnlyDictionary<string, WorldTemplateDefinition> s_templates =
        new Dictionary<string, WorldTemplateDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            { EmptyTemplateName, new EmptyWorldTemplate() },
            { LocalHomeTemplateName, new LocalHomeWorldTemplate() },
            { GridTemplateName, new GridSpaceWorldTemplate() },
            { SocialSpaceTemplateName, new SocialSpaceWorldTemplate() }
        };

    public static Action<World> GetTemplate(string name)
    {
        return GetDefinition(name).Apply;
    }

    public static void ApplyTemplate(World world, string templateName)
    {
        GetTemplate(templateName)?.Invoke(world);
    }

    private static WorldTemplateDefinition GetDefinition(string name)
    {
        name ??= string.Empty;

        if (s_templates.TryGetValue(name, out var template))
        {
            return template;
        }

        return s_templates[EmptyTemplateName];
    }
}
