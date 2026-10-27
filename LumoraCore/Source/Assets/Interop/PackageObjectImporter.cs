// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Lumora.Core.Components;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Logging;
using Lumora.Core.Math;
using Lumora.Core.Persistence;
using Lumora.Core.Phos;

namespace Lumora.Core.Assets.Interop;

// What actually happened during an import, as opposed to what the inspector predicted.
public sealed class PackageImportResult
{
    public Slot? Root { get; set; }
    public string? FailureReason { get; set; }

    public int SlotsCreated { get; set; }
    public int SlotsPruned { get; set; }

    // Slot trees dropped because they carry something the wearer's own user root already owns.
    public int SlotsConflicting { get; set; }

    // Solid colliders inside an avatar that were turned query-only on the way in.
    public int CollidersSoftened { get; set; }

    // Particle systems that got their sprite back by reading through the style chain.
    public int ParticlesTextured { get; set; }

    // Particle systems that got the mesh they actually draw per particle.
    public int ParticlesMeshed { get; set; }

    // Colour/size/lifetime/etc modules pointed back at the system that owns them.
    public int ParticleModulesWired { get; set; }

    public bool AvatarFinished { get; set; }
    public int ComponentsAttached { get; set; }
    public int ComponentsStripped { get; set; }
    public int ComponentsSkipped { get; set; }

    // How much of the wiring actually landed. A component can attach perfectly and still render
    // nothing if the reference to its mesh or material never resolved, and that failure is otherwise
    // completely silent - which is why it needs counting rather than hoping.
    public int ReferencesResolved { get; set; }
    public int ReferencesDangling { get; set; }

    public int SkeletonsBuilt { get; set; }

    public int MeshesConverted { get; set; }
    public int TexturesImported { get; set; }

    // Pictures whose own compressed chain came with the package and went straight into the GPU cache.
    public int TexturesPrecompressed { get; set; }
    public int AssetsFailed { get; set; }

    // The package's node graphs: scripts made from them, how many of those run, and the nodes carried or not,
    // by the package's class name.
    public int ScriptsMade { get; set; }
    public int ScriptsRunning { get; set; }
    public int ScriptNodesConverted { get; set; }
    public int ScriptNodesUnconverted { get; set; }
    public Dictionary<string, int> UnconvertedNodes { get; } = new(StringComparer.Ordinal);

    // Why parts of the graphs did not come across, one line per reason, with how often it came up.
    public Dictionary<string, int> ScriptProblems { get; } = new(StringComparer.Ordinal);

    public bool Succeeded => Root != null && FailureReason == null;
}

// Builds a Lumora hierarchy from a package's object graph.
//
// The shape of the object - its slots, names and transforms - transfers exactly, because that part of
// both formats is just a tree of named transforms. Assets transfer by being re-encoded into our own
// formats and stored in the local database, so from that point on they behave like anything else
// imported here: cached, peer-transferred, variant-generated.
//
// Components are the lossy part and the code is deliberately honest about it. A component is attached
// only when we have a counterpart AND know how to carry its data across; anything else is counted and
// reported rather than attached half-configured, because an empty component that looks right in the
// inspector but does nothing is worse than a visible gap.
//
// Scripted behaviour becomes scripts (PackageNodeGraphs), and the slots that existed only to hold its nodes
// are pruned. -xlinka
public static partial class PackageObjectImporter
{
    public static async Task<PackageImportResult> ImportAsync(string file, Slot parent)
    {
        var result = new PackageImportResult();
        if (parent == null || parent.IsDestroyed)
        {
            result.FailureReason = "No slot to import into.";
            return result;
        }

        var world = parent.World;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        // An import allocates like a world arriving (an avatar's came to gigabytes), and nothing gave that back
        // once its loads were done, so it gets the same one collection after it settles.
        GcPacer.WorldArriving();
        // Reading and decoding the archive is the import's longest single step, ten seconds on a package
        // of a few hundred thousand slots, and it never touches the world. -xlinka
        await WorldContext.ToBackground();
        using var archive = PackageArchive.Open(file);

        var record = archive.MainRecord;
        if (record == null || !record.IsObject)
        {
            result.FailureReason = record == null
                ? "Package has no main record."
                : $"Main record is a '{record.RecordType}' package; only object packages convert.";
            return result;
        }

        string? graphSignature = PackageArchive.SignatureOf(TryUri(record.AssetUri));
        if (graphSignature == null || !archive.HasAsset(graphSignature))
        {
            result.FailureReason = "Main record does not point at an object graph inside this package.";
            return result;
        }

        DataTreeDictionary graph;
        try
        {
            graph = PackageGraphReader.Read(archive.ReadAsset(graphSignature));
        }
        catch (Exception ex)
        {
            result.FailureReason = "Could not decode the object graph: " + ex.Message;
            return result;
        }

        var typeTable = ReadTypeTable(graph);
        // Read off the thread like the rest of the graph: it walks every component once.
        var plainColours = PackageMemberMapper.PlainColourComponents(graph, ReadTypeNames(graph));

        // Assets first: everything the tree points at must already exist in the local database before a
        // component can be pointed at it.
        var bindPoses = new Dictionary<string, Dictionary<string, float4x4>>(StringComparer.OrdinalIgnoreCase);
        var bindOrder = new Dictionary<string, float4x4[]>(StringComparer.OrdinalIgnoreCase);
        var meshBounds = new Dictionary<string, BoundingBox>(StringComparer.OrdinalIgnoreCase);
        var shapeNames = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var graphRead = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        var assetUrls = await ConvertAssetsAsync(archive, graph, result, bindPoses, bindOrder, meshBounds, shapeNames).ConfigureAwait(false);
        var assetsConverted = System.Diagnostics.Stopwatch.GetElapsedTime(started);

        await WorldContext.ToWorld();
        if (parent.IsDestroyed)
        {
            result.FailureReason = "The target slot went away during import.";
            return result;
        }

        if (graph.TryGetNode("Object") is not DataTreeDictionary objectNode)
        {
            result.FailureReason = "The object graph has no root object.";
            return result;
        }

        var context = new BuildContext(world, typeTable, assetUrls, result);
        context.PlainColours = plainColours;
        context.TypeNames.AddRange(ReadTypeNames(graph));
        if (graph.TryGetNode("TypeVersions") is DataTreeDictionary versions)
        {
            foreach (var (encoded, node) in versions.Children)
            {
                if (node is DataTreeValue { Value: IConvertible version } && version is not string)
                    context.TypeVersions[PackageTypeMap.SimpleName(encoded)] = Convert.ToInt32(version, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        context.Graphs.Wanted = context.TypeNames.Exists(PackageNodeGraphs.IsNodeType);
        context.Graphs.TypeNames = context.TypeNames;
        foreach (var pair in bindPoses)
            context.MeshBindPoses[pair.Key] = pair.Value;
        foreach (var pair in bindOrder)
            context.MeshBindOrder[pair.Key] = pair.Value;
        foreach (var pair in meshBounds)
            context.MeshBounds[pair.Key] = pair.Value;
        foreach (var pair in shapeNames)
            context.MeshShapeNames[pair.Key] = pair.Value;
        var root = parent.AddSlot(CleanName(record.Name));
        bool built = await BuildSlotAsync(objectNode, root, context);
        var treeBuilt = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        if (!built)
        {
            // Nothing in the whole package survived the strip - do not leave an empty slot behind.
            root.Destroy();
            result.FailureReason = "Everything in this package was stripped; nothing left to import.";
            return result;
        }

        await AttachLooseAssetsAsync(graph, root, context);
        WorldK7.LinkTemplateLists(context.TemplateLists, context.ById, context.Graphs);
        context.RefLinks.RemoveAll(link => WorldUi.TakePaletteLink(context.Graphs, link.Reference, link.TargetId));
        await ResolveLinksAsync(context);
        PackageMemberMapper.FinishMats(context.ById.Values);
        FinishShapeNames(context);
        FinishVisemes(context);
        FinishExpressions(context);
        FinishFontChains(context);
        FinishCanvases(context);
        FinishLodGroups(context);
        FinishLights(context);
        FinishMenus(context);
        FinishLocomotion(context);
        WorldUi.Finish(root, context.Locomotion);
        WorldK2.FinishBenches(context.Benches);
        await PaceAsync(context);
        BuildSkeletons(root, context);
        WireParticleLook(context);
        await PaceAsync(context);
        FinishAvatar(root, objectNode, context);
        FinishEyes(root, context);
        FinishWorldScope(root);
        await PaceAsync(context);
        if (context.Graphs.Wanted)
        {
            await PackageNodeGraphs.ConvertAsync(root, context.Graphs, context.ById, result);
            await WorldContext.ToWorld();
            await PaceAsync(context);
        }
        WorldK7.FinishTemplateLists(context.TemplateLists, context.ById);
        DropDeadCopies(context);

        // The root's saved Position is wherever the object happened to be standing in the world it was
        // packaged from, and it is meaningless here. The dova dog package carries (-4.27, 12.60,
        // -156.62), so the import landed 157 metres out and 12 up - visible as a speck on the horizon
        // rather than in front of whoever asked for it.
        //
        // Rotation and scale are NOT touched: those are how the author built the thing (this one is
        // scaled 1.25 and yawed about 101 degrees) and dropping them would arrive at the right place
        // wearing the wrong pose. Only the position is stale. -xlinka
        float3 packagedPosition = root.LocalPosition.Value;
        if (PackageWorldLanding.IsWorld(context.TypeNames))
            PackageWorldLanding.Land(root, parent);
        else if (packagedPosition != float3.Zero)
        {
            root.LocalPosition.Value = float3.Zero;
            Logger.Debug($"Package import: dropped the packaged root position {packagedPosition} so it lands on the import anchor (the dialog panel) instead.");
        }

        result.Root = root;
        Security.ItemSeals.ProtectImported(root);
        Logger.Log($"Package import: {result.SlotsCreated} slots, {result.ComponentsAttached} components, "
                 + $"{result.MeshesConverted} meshes, {result.TexturesImported} textures ({result.TexturesPrecompressed} came compressed) "
                 + $"({result.SlotsPruned} slots pruned, {result.SlotsConflicting} orphan/userspace trees dropped, "
                 + $"{result.ComponentsStripped} stripped, {result.ComponentsSkipped} skipped); "
                 + $"references {result.ReferencesResolved} wired, {result.ReferencesDangling} dangling; "
                 + $"{System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds:0.0} s: graph {graphRead.TotalSeconds:0.0}, assets "
                 + $"{(assetsConverted - graphRead).TotalSeconds:0.0}, tree {(treeBuilt - assetsConverted).TotalSeconds:0.0}, the rest "
                 + $"{(System.Diagnostics.Stopwatch.GetElapsedTime(started) - treeBuilt).TotalSeconds:0.0}");
        Logger.Debug($"Package import: {result.SkeletonsBuilt} skeleton(s) built for skinned meshes");
        if (result.ParticlesTextured > 0 || result.ParticlesMeshed > 0 || result.ParticleModulesWired > 0)
            Logger.Debug($"Package import: particles - {result.ParticleModulesWired} modules wired, "
                     + $"{result.ParticlesMeshed} got their mesh, {result.ParticlesTextured} got their texture");
        await PaceAsync(context);
        LogRendererWiring(result.Root);
        await PaceAsync(context);
        LogMaterialReferences(result.Root, context);
        LogDanglingReferences(context);
        return result;
    }

    // Copies that can never copy anything. One whose source and target both failed to land has nothing to read
    // and nowhere to write: its ends were plumbing read and dropped, or members that do nothing here (a
    // renderer's motion vector mode). One whose source sat on a part that did not come across reads a value that
    // can no longer change, and its target was stored holding what the copy last wrote, so the target keeps that
    // and the copy goes. Kept, either sits inert and reports losses that are really its ends'. -xlinka
    private static void DropDeadCopies(BuildContext context)
    {
        var failed = new Dictionary<Component, List<ISyncRef>>(ReferenceEqualityComparer.Instance);
        foreach (var reference in context.RefFailures.Keys)
        {
            if (reference.Target != null || CopyHolding(reference) is not { } copy)
                continue;
            if (!failed.TryGetValue(copy, out var held))
                failed[copy] = held = new List<ISyncRef>(2);
            held.Add(reference);
        }
        if (failed.Count == 0)
            return;

        var stored = new Dictionary<ISyncRef, string>(ReferenceEqualityComparer.Instance);
        foreach (var link in context.RefLinks)
            stored[link.Reference] = link.TargetId;

        int neither = 0, settled = 0;
        foreach (var (copy, held) in failed)
        {
            bool dead = held.Count >= 2;
            if (!dead && held.Count == 1
                && (held[0] as Lumora.Core.Networking.Sync.ISyncMember)?.Name == "Source"
                && stored.TryGetValue(held[0], out var sourceId) && context.Graphs.Unattached.ContainsKey(sourceId)
                && copy.GetType().GetField("Target")?.GetValue(copy) is ISyncRef { Target: not null })
            {
                dead = true;
                settled++;
            }
            else if (dead)
                neither++;
            if (!dead)
                continue;
            foreach (var reference in held)
                context.RefFailures.Remove(reference);
            context.Result.ReferencesDangling -= held.Count;
            copy.Destroy();
        }
        if (neither + settled > 0)
            Logger.Debug($"Package import: dropped {neither} copies with neither end here and {settled} copies of a part that did not come across");
    }

    private static Component? CopyHolding(ISyncRef reference)
    {
        var owner = (reference as Lumora.Core.Networking.Sync.SyncElement)?.Parent;
        while (owner is Lumora.Core.Networking.Sync.SyncElement { Parent: { } up } && owner is not Component)
            owner = up;
        if (owner is not Component { IsDestroyed: false } copy || !copy.GetType().IsGenericType)
            return null;
        var open = copy.GetType().GetGenericTypeDefinition();
        return open == typeof(Components.Utility.CopyValue<>) || open == typeof(Components.Utility.CopyReference<>) ? copy : null;
    }

    // Every reference that stayed unset, grouped by the member holding it and why, ids taken out, so a dangling
    // count can be read as a list of causes rather than a number. -xlinka
    private static void LogDanglingReferences(BuildContext context)
    {
        if (context.RefFailures.Count == 0)
            return;
        var groups = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (reference, why) in context.RefFailures)
        {
            var member = reference as Lumora.Core.Networking.Sync.ISyncMember;
            var owner = (reference as Lumora.Core.Networking.Sync.SyncElement)?.Parent;
            while (owner is Lumora.Core.Networking.Sync.SyncElement { Parent: { } up } && owner is not Component)
                owner = up;
            string reason = System.Text.RegularExpressions.Regex.Replace(why, "target [0-9a-fA-F-]+", "target");
            string key = $"{owner?.GetType().Name ?? "?"}.{member?.Name ?? "?"}: {reason}";
            groups[key] = groups.GetValueOrDefault(key) + 1;
        }
        var ordered = new List<KeyValuePair<string, int>>(groups);
        ordered.Sort((a, b) => b.Value.CompareTo(a.Value));
        foreach (var (key, count) in ordered)
            Logger.Debug($"Package import: dangling x{count} {key}");
    }

    // Meshes and materials do NOT live in the slot tree. The graph root carries a separate top-level
    // "Assets" list of bare components, and every renderer in the hierarchy references them by id. Build
    // only the tree and those references have nothing to point at: on the test avatar that was all 30
    // mesh references, all 5 skinned mesh references and 51 material references dangling, so the shapes
    // came through and nothing had geometry or a material to render with.
    //
    // They are attached to one slot under the import root, which also keeps them together the way the
    // package stored them. -xlinka
    private static async Task AttachLooseAssetsAsync(DataTreeDictionary graph, Slot root, BuildContext context)
    {
        var list = graph.TryGetNode("Assets") as DataTreeList
                   ?? graph.TryGetNode("Dependencies") as DataTreeList;
        if (list == null || list.Count == 0)
            return;

        var holder = root.AddSlot("Assets");

        foreach (var entry in list.Children)
        {
            // Every provider built here starts loading its asset, so this is the burst that actually
            // costs frames - a couple of hundred meshes and textures asking for decode at once. Yield
            // on the same budget as the tree build.
            if (await PaceAsync(context) && holder.IsDestroyed)
                return;

            if (entry is not DataTreeDictionary worker)
                continue;
            if (worker.TryGetNode("Type") is not DataTreeValue typeValue)
                continue;

            int index = SafeInt(typeValue);
            if (index < 0 || index >= context.Types.Count)
                continue;

            var resolution = context.Types[index];
            // THE PASS-THROUGH CAPTURE COMES BEFORE THE STRIP, and that ordering is the whole
            // feature. A style or a particle renderer is stripped precisely BECAUSE we read its data
            // here instead of attaching it - so dropping out above this line took the style out of
            // the table the moment it was marked stripped, and every particle system in the package
            // reported "no style component to read through" and came in on bare defaults. -xlinka
            var passData = worker.TryGetNode("Data") as DataTreeDictionary;
            if (passData != null && PassThroughTypes.Contains(resolution.SimpleName)
                && (passData.TryGetNode("ID") as DataTreeValue)?.Value?.ToString() is string capturedId
                && capturedId.Length > 0)
            {
                context.PassThrough[capturedId] = passData;
                context.PassThroughTypes[capturedId] = resolution.SimpleName;
            }

            if (resolution.Fate == PackageTypeFate.Stripped)
            {
                context.Result.ComponentsStripped++;
                continue;
            }

            var data = passData;

            if (resolution.Target == null || data == null)
            {
                context.Result.ComponentsSkipped++;
                continue;
            }

            if (!TryAttach(holder, resolution, data, context))
                context.Result.ComponentsSkipped++;
        }
    }

    // Give every skinned mesh a skeleton to hang off.
    //
    // Packages carry bones as ordinary slots and reference them directly - there is nothing in the file
    // that corresponds to our SkeletonBuilder, because building the platform skeleton is our own step.
    // Without one the skinned path bails before it builds anything (it needs a Skeleton3D to hang the
    // skin on), so the body and every other skinned surface rendered nothing.
    //
    // Two rules learned the hard way:
    //
    // ONE SKELETON PER RENDERER, never a shared one. Bind poses are per-mesh and bone NAMES collide
    // across meshes on the same character - this avatar has a "Head" and a "Jaw" in both the body and
    // the tail-maw rigs. Merging them put two bones called "Head" in one skeleton, and since lookup is
    // by name the second mesh bound to the first mesh's bones and deformed into nonsense.
    //
    // BONES COME FROM THE RENDERER'S OWN LIST, never from walking the slot hierarchy. Walking sweeps up
    // meshes, props and attachment points as joints - 493 "bones" from Hips here - and the per-frame
    // skeleton update then cost 40-160ms forever.
    //
    // The builder sits on the MODEL ROOT (the nearest slot containing both the mesh and its bones,
    // typically the armature) because SkeletonHook derives its orientation offset from the gap between
    // the builder's slot and the root bone's parent. Put it anywhere else and the skin renders offset
    // from the bones. -xlinka
    // Give an imported particle system its sprite back.
    //
    // In a package, a particle system's look is three components away from the system that owns it:
    //
    //     ParticleSystem.Style -> ParticleStyle.Renderer -> <renderer>.Material -> its texture
    //
    // The middle two have no counterpart here, so they never attach, and the reference pass that wires
    // everything else stops dead at the first missing link. The system arrives with no texture at all,
    // which is why an imported effect renders as a swarm of untextured beads. The materials themselves
    // DO import, so once the chain is walked in the source data the texture is a component we already
    // hold.
    //
    // Only the sprite is recovered. A renderer that draws an arbitrary MESH per particle has nowhere to
    // land - ours draws a quad or a sphere - so those systems get the material's texture on a billboard,
    // which is closer than nothing but is not what the author drew. -xlinka
    private static void WireParticleLook(BuildContext context)
    {
        foreach (var (system, data) in context.ParticleSystems)
        {
            if (system.IsDestroyed)
                continue;

            string name = system.Slot?.SlotName.Value ?? "?";

            // A MISSING STYLE IS NOT A REASON TO STOP EITHER, for the same reason a missing renderer
            // is not: a system whose modules are plain components on its own slot has no style at all,
            // and giving up here skipped the adoption below that is the ONLY thing that would have
            // wired them.
            DataTreeDictionary? style = null;
            if (ReadValue(data, "Style") is string styleId
                && context.PassThrough.TryGetValue(styleId, out var foundStyle))
            {
                style = foundStyle;
            }

            // A MISSING RENDERER IS NOT A REASON TO STOP.
            //
            // This used to give up here, and giving up here skipped the module wiring below - so a
            // system whose style carries no particle renderer at all got NONE of its modules pointed
            // at it and ran on pure defaults. Trail and ribbon systems are exactly that shape: the
            // ribbons are the visual, there is no per-particle renderer, and every one of them came
            // in as a swarm of default beads. The renderer is only needed for the mesh and the
            // texture further down; everything above it works without one. -xlinka
            DataTreeDictionary? renderer = null;
            string rendererType = string.Empty;
            if (style != null && ReadValue(style, "Renderer") is string rendererId)
            {
                context.PassThrough.TryGetValue(rendererId, out renderer);
                context.PassThroughTypes.TryGetValue(rendererId, out var foundType);
                rendererType = foundType ?? string.Empty;
            }

            // A NULL RENDERER IS A STATEMENT, not an absence: the author chose to draw no sprite and
            // let the ribbons or trails be the whole effect. Without honouring it the system falls back
            // to the default billboard, and a glowing streak from each eye arrives as a string of white
            // beads - which is exactly what it did. -xlinka
            if (rendererType == "NullParticleRenderer")
            {
                system.RenderParticles.Value = false;
            }

            // THE MODULES. Colour, size, lifetime, speed, rotation, gravity, trails, ribbons - all of it.
            //
            // Package modules do not know which system they belong to: the STYLE owns them as a plain list,
            // and not one of them carries a System reference. Ours is the other way round - every
            // ParticleModuleBase has a SyncRef<ParticleSystem> and registers itself with whatever it
            // points at. So there was nothing for the member mapper to map, System stayed null on all 29,
            // not a single module ever registered, and every imported system ran on pure component
            // defaults. That is white particles at a default size, no matter how many member names line
            // up. Walk the style's list and point each one at its system. -xlinka
            int modulesWired = 0;
            if (style != null
                && style.TryGetNode("Modules") is DataTreeDictionary modulesHolder
                && modulesHolder.TryGetNode("Data") is DataTreeList moduleList)
            {
                foreach (var entry in moduleList.Children)
                {
                    string? moduleId = entry switch
                    {
                        DataTreeDictionary holder => (holder.TryGetNode("Data") as DataTreeValue)?.Value?.ToString(),
                        DataTreeValue value => value.Value?.ToString(),
                        _ => null,
                    };

                    if (string.IsNullOrEmpty(moduleId) || !context.ById.TryGetValue(moduleId, out var moduleElement))
                        continue;
                    if (moduleElement is not ParticleModuleBase module || module.IsDestroyed)
                        continue;
                    if (module.System.Target != null)
                        continue;

                    module.System.Target = system;
                    modulesWired++;
                }
            }
            // AND THE MODULES THAT NEVER WENT THROUGH A STYLE.
            //
            // The list above is only one of the two ways these are authored. The other is modules
            // sitting as plain components on the SYSTEM'S OWN SLOT, with no style listing them at all,
            // and this avatar has both shapes in it: the right eye's trail keeps its modules in a
            // style, the left eye's are siblings of the system. Walking the style alone wired seven
            // modules on one eye and none on the other, which is exactly what "the eye particles are
            // wrong" looks like.
            //
            // Nothing is claimed twice: a module that already points at a system is left alone, so
            // this only picks up the ones the style walk did not reach. -xlinka
            int adopted = 0;
            var ownSlot = system.Slot;
            if (ownSlot != null && !ownSlot.IsDestroyed)
            {
                foreach (var module in ownSlot.GetComponents<ParticleModuleBase>())
                {
                    if (module == null || module.IsDestroyed || module.System.Target != null)
                        continue;
                    module.System.Target = system;
                    adopted++;
                }
            }

            modulesWired += adopted;
            context.Result.ParticleModulesWired += modulesWired;

            // The MESH the renderer draws per particle. This is the one that matters: most effects on a
            // real avatar are a mesh renderer, not a billboard, and without it every one of them is a ball.
            string meshResult = "none";
            if (renderer != null
                && system.ParticleMesh.Target == null && system.ParticleMeshAsset.Target == null
                && ReadValue(renderer, "Mesh") is string meshId
                && context.ById.TryGetValue(meshId, out var meshElement)
                && meshElement is Component meshComponent && !meshComponent.IsDestroyed)
            {
                // A provider needs the ASSET reference to decode at all; a procedural mesh needs only to
                // be pointed at.
                if (meshComponent is IAssetProvider<MeshDataAsset> meshProvider)
                {
                    system.ParticleMeshAsset.Target = meshProvider;
                }
                else
                {
                    system.ParticleMesh.Target = meshComponent;
                }

                context.Result.ParticlesMeshed++;
                meshResult = meshComponent.GetType().Name;
            }

            // The sprite, which lives another hop away on the renderer's material.
            string textureResult = "none";
            if (renderer != null
                && system.Texture.Target == null
                && ReadValue(renderer, "Material") is string materialId
                && context.ById.TryGetValue(materialId, out var materialElement)
                && materialElement is Component material && !material.IsDestroyed)
            {
                var texture = FirstTexture(material);
                if (texture != null)
                {
                    system.Texture.Target = texture;
                    context.Result.ParticlesTextured++;
                    textureResult = material.GetType().Name;
                }
                else
                {
                    textureResult = material.GetType().Name + " (no texture)";
                }

                // And the material itself, which is what package particles draw with in the first place:
                // there is no particle shader, only a renderer pointed at ordinary material.
                if (system.Material.Target == null && material is IAssetProvider<MaterialAsset> materialProvider)
                {
                    system.Material.Target = materialProvider;
                    textureResult += " (used as material)";
                }

                // The material also decides how these blend, and getting that wrong is the difference
                // between a splash and half a splash.
                if (BlendModeOf(material) is { } blend)
                {
                    system.BlendMode.Value = blend;
                    textureResult += $" blend={blend}";
                }
            }

            Logger.Debug($"Package import: particle '{name}' - {modulesWired} module(s) wired"
                     + (adopted > 0 ? $" ({adopted} off its own slot)" : string.Empty) + ", "
                     + $"mesh={meshResult} texture={textureResult}"
                     + (renderer == null ? " (no particle renderer; ribbons/trails carry the look)" : string.Empty)
                     + (style == null ? " (no style; modules taken off the slot)" : string.Empty)
                     + (rendererType == "NullParticleRenderer" ? " (null renderer; no sprite drawn)" : string.Empty));
        }
    }

    // Package particle materials carry a BlendMode member by that exact name; anything without one keeps
    // our additive default.
    private static Assets.BlendMode? BlendModeOf(Component material)
    {
        for (int i = 0; i < material.SyncMemberCount; i++)
        {
            if (!string.Equals(material.GetSyncMemberName(i), "BlendMode", StringComparison.Ordinal))
                continue;
            if (material.GetSyncMember(i) is Sync<Assets.BlendMode> blend)
                return blend.Value;
        }
        return null;
    }

    // The material's headline map. Package particle materials are usually unlit with a plain Texture, but
    // a PBS one names it AlbedoTexture, so the preferred names are tried in order before falling back to
    // whatever texture reference the material happens to carry first.
    private static readonly string[] TextureMemberOrder = { "Texture", "AlbedoTexture", "MainTexture" };

    private static IAssetProvider<TextureAsset>? FirstTexture(Component material)
    {
        foreach (var preferred in TextureMemberOrder)
        {
            for (int i = 0; i < material.SyncMemberCount; i++)
            {
                if (!string.Equals(material.GetSyncMemberName(i), preferred, StringComparison.Ordinal))
                    continue;
                if (material.GetSyncMember(i) is AssetRef<TextureAsset> { Target: { } named })
                    return named;
            }
        }

        for (int i = 0; i < material.SyncMemberCount; i++)
        {
            if (material.GetSyncMember(i) is AssetRef<TextureAsset> { Target: { } any })
                return any;
        }
        return null;
    }

    // A package world keeps its world-wide variables in a "World" scope every world has at its root; ours
    // have none, so "World/LightIntensity" and the rest bound nothing. The import root takes that scope,
    // which keeps the world's variables to the world. An avatar (a humanoid at or just under the root)
    // gets none: its "World/" reads are meant for whatever world it is worn in. -xlinka
    private static void FinishWorldScope(Slot root)
    {
        foreach (var rig in root.GetComponentsInChildren<HumanoidRig>())
        {
            if (rig.IsHumanoid && (rig.Slot == root || rig.Slot?.Parent == root))
                return;
        }
        foreach (var scope in root.GetComponents<Components.Variables.VariableScope>())
        {
            if (scope.ScopeName.Value == "World")
                return;
        }
        // Named lookups only: a variable with no scope in its path binds to the nearest unnamed scope, never here.
        var world = root.AttachComponent<Components.Variables.VariableScope>();
        world.ScopeName.Value = "World";
        world.DirectBindingOnly.Value = true;
    }

    // Leave an imported avatar in the state the creator would have left it in.
    //
    // Everything needed to BE an avatar arrives - rig, skeleton, bone map - but nothing that says it IS
    // one. The context menu offers "Equip Avatar" only where it finds an AvatarForm, and the package's
    // marker component has no counterpart here, so a perfectly good avatar imports and then simply cannot be
    // worn. The rest of this mirrors AvatarStudio.RunCreate: facing, references, a root grab. Equip
    // itself still does the IK, hand posers and face drivers, so none of that is repeated here.
    private static void FinishAvatar(Slot root, DataTreeDictionary objectNode, BuildContext context)
    {
        HumanoidRig? rig = null;
        foreach (var candidate in root.GetComponentsInChildren<HumanoidRig>())
        {
            if (candidate.IsHumanoid)
            {
                rig = candidate;
                break;
            }
        }

        var avatar = rig?.Slot;
        if (rig == null || avatar == null || avatar.IsDestroyed)
            return;
        var skeleton = avatar.GetComponentInChildren<SkeletonBuilder>();
        if (skeleton == null)
            return;

        // The stance is the creator's STORED default pose, never the pose the slots were saved in.
        //
        // A package saves the avatar as it was the moment it was packaged, and for a worn avatar that is
        // the last frame of somebody wearing it: on a real one the hips sat 9 cm lower than the creator
        // left them, the head was turned 10 degrees off the torso, one foot was yawed 10 degrees out and
        // the other 20 the opposite way, 35 cm apart on 23 cm hips. Building the facing, the gait
        // stance, the foot rest yaw and the view out of that pose produced exactly those faults on the
        // wearer - feet splayed, camera tilted - and none of them are in the avatar. The IK solver a
        // package carries never uses that pose either: it stores each bone's local pose at creation and
        // writes it back every frame before solving, so the saved slot pose is nothing but where the
        // solver last put things. That stored pose travels in the package, and RestoreStoredStance puts
        // the bones back to it. An avatar that carries no stored pose keeps the pose it was saved in.
        //
        // The BIND pose is still not a substitute. It is a rigging pose, not a stance: measured on a
        // real avatar its legs are 97% straight where the stance legs are 87%, and the entire
        // digitigrade bend lives in the difference - forcing bind stood the cat up like a person.
        // Calibrating in bind and restoring afterwards only moves the damage, because the references
        // would then be baked at bone positions the avatar never stands at. An avatar genuinely saved in
        // a cursed pose with no stored one is what AvatarIK.ForceTpose is for, and it stays opt-in.
        // -xlinka
        int stanceBones = 0;
        var storedStance = FindStoredStance(objectNode, index =>
            index >= 0 && index < context.Types.Count ? context.Types[index].SimpleName : null);
        if (storedStance != null)
        {
            stanceBones = RestoreStoredStance(storedStance, id =>
                context.ById.TryGetValue(id, out var element) && element is Slot slot && !slot.IsDestroyed ? slot : null);
        }

        SoftenAvatarColliders(avatar, context);

        if (avatar.GetComponent<AvatarForm>() == null)
            avatar.AttachComponent<AvatarForm>();

        // FOUR LEGS TAKE A DIFFERENT SOLVER, and that has to be decided HERE.
        //
        // The model-import path has branched on this for a while; this one never did, so an avatar that
        // arrived in a package was set up as a person no matter what it was. It got humanoid facing and
        // humanoid reference placement, and the quadruped rig was not built at all - not until the thing
        // was equipped, because that was the only other site that ever called TryAttachFor. So an
        // imported quadruped sat in the world mis-rigged, and "the legs do not import correctly" was
        // exactly and literally true.
        //
        // TryAttachFor returns null for anything that is not a quadruped, so the humanoid path below is
        // untouched for every avatar that really is one. -xlinka
        var quadrupedIk = Components.Avatar.QuadrupedIK.TryAttachFor(avatar, skeleton, rig);
        if (quadrupedIk == null)
        {
            // Sides from GEOMETRY before anything trusts the labels. A left-handed package arrives
            // reflected with its bone names unchanged: on a real avatar every Left_* bone sat on
            // the physical right and every Right_* on the physical left, arms, legs and eyes alike, so a
            // tool equipped on the right hand raised the arm on the left and crossed the chest to reach
            // it. This resolves physical right from the eyes, toes and knees, none of which carry a
            // label, and swaps whole pairs when the labels disagree. Correct rigs are a logged no-op.
            // -xlinka
            rig.ValidateLimbSides(skeleton);

            // The stored bone map keys this rig and the name pass never runs on a package, so a hand
            // that shipped without fingers stayed fingerless here: no finger posing, the tool hold rolled
            // off the holder, and the grip reference below had nothing to take its roll from. Same
            // geometric pass the model path runs; it only touches a hand with no finger bones. -xlinka
            rig.DetectHandRigs();

            // The pose proxies were saved where the wearer's headset and controllers last were, and
            // once the bones are back in the stored stance they no longer sit on their bones. Seated
            // again from the bones before anything reads them: the view reference is placed from the
            // head one.
            int proxies = ReseatPoseProxies(avatar, rig);
            if (proxies > 0)
                Logger.Debug($"Package import: {proxies} pose prox{(proxies == 1 ? "y" : "ies")} re-seated on the stance bones");

            // Facing first, then references: equip resets the root to identity, so a reference baked against
            // a stale frame wears the avatar yawed off the user's forward.
            AvatarCalibration.AlignAvatarFacing(avatar, rig);
            // Feet and pelvis off, matching the creator's own defaults: those are opt-in calibration for
            // full-body tracking, and placing them unasked gives a 3-point user targets they never set.
            AvatarCalibration.AutoPlaceReferences(avatar, rig, feet: false, pelvis: false);
        }

        // A packaged avatar usually hides the wearer's head already, as a view override that our mapper
        // keeps. One that arrives without it (or with the override disabled by the desktop pose relay it
        // hung off) gets ours on the head bone, so nobody wears their new avatar from inside its skull.
        var headOverride = AvatarCalibration.EnsureHeadViewOverride(avatar, rig);

        var grab = avatar.GetComponent<Grabbable>() ?? avatar.AttachComponent<Grabbable>();
        grab.BlockWhenWorn.Value = true;

        context.Result.AvatarFinished = true;
        Logger.Log($"Package import: avatar ready to equip - {(quadrupedIk != null ? "QUADRUPED, " : "")}{rig.Bones.Count} rig bones"
                 + (rig.TryGetBone(Lumora.Core.Input.BodyNode.LeftEye) != null || rig.TryGetBone(Lumora.Core.Input.BodyNode.RightEye) != null
                     ? ", eyes mapped" : ", no eye bones")
                 + (stanceBones > 0 ? $", stance restored from the stored default pose ({stanceBones} bones)" : ", no stored stance, saved pose kept")
                 + (headOverride != null ? $", head hidden from the wearer via '{headOverride.Slot?.SlotName.Value}'" : ", no head bone to hide")
                 + $", {context.Result.CollidersSoftened} collider(s) made query-only");
    }

    // The package IK solver's bone references, in the order its stored default-pose arrays index them:
    // array entry i belongs to reference i + 1, the root at index 0 has no stored pose.
    private static readonly string[] StoredStanceBones =
    {
        "root", "pelvis", "spine", "chest", "neck", "head",
        "leftShoulder", "leftUpperArm", "leftForearm", "leftHand",
        "rightShoulder", "rightUpperArm", "rightForearm", "rightHand",
        "leftThigh", "leftCalf", "leftFoot", "leftToes",
        "rightThigh", "rightCalf", "rightFoot", "rightToes",
    };

    // The solver data carrying the stored default pose, wherever the IK worker sits in the tree. The
    // worker itself is never attached (it is the licensed solver), so this reads the graph directly.
    public static DataTreeDictionary? FindStoredStance(DataTreeDictionary node, Func<int, string?> typeNameOf)
    {
        if (node.TryGetNode("Components") is DataTreeNode componentsNode
            && FindWorkerList(componentsNode) is { } workers)
        {
            foreach (var entry in workers)
            {
                if (entry is not DataTreeDictionary worker
                    || worker.TryGetNode("Type") is not DataTreeValue typeValue
                    || typeNameOf(SafeInt(typeValue)) != "VRIK")
                    continue;
                if ((worker.TryGetNode("Data") as DataTreeDictionary)?.TryGetNode("Solver") is DataTreeDictionary solver
                    && solver.TryGetNode("BoneReferences") is DataTreeDictionary)
                    return solver;
            }
        }

        if (node.TryGetNode("Children") is DataTreeList children)
        {
            foreach (var child in children.Children)
            {
                if (child is DataTreeDictionary childNode && FindStoredStance(childNode, typeNameOf) is { } found)
                    return found;
            }
        }
        return null;
    }

    // Write the stored default pose back onto the bone slots. Returns how many bones were written; zero
    // with one warning when the data does not have the shape the package IK solver writes, in which case
    // the pose is left exactly as saved.
    public static int RestoreStoredStance(DataTreeDictionary solver, Func<string, Slot?> resolveSlot)
    {
        if (solver.TryGetNode("BoneReferences") is not DataTreeDictionary references
            || (solver.TryGetNode("defaultLocalPositions") as DataTreeDictionary)?.TryGetNode("Data") is not DataTreeList positions
            || (solver.TryGetNode("defaultLocalRotations") as DataTreeDictionary)?.TryGetNode("Data") is not DataTreeList rotations)
        {
            Logger.Warn("Package import: the IK worker carries no stored default pose; saved pose kept");
            return 0;
        }

        int referenceCount = references.Children.Count - (references.ContainsKey("ID") ? 1 : 0);
        if (referenceCount != StoredStanceBones.Length
            || positions.Count != referenceCount - 1 || rotations.Count != referenceCount - 1)
        {
            Logger.Warn($"Package import: stored default pose does not match its bone references "
                      + $"({referenceCount} references, {positions.Count} positions, {rotations.Count} rotations); saved pose kept");
            return 0;
        }

        int written = 0;
        for (int i = 1; i < StoredStanceBones.Length; i++)
        {
            if ((references.TryGetNode(StoredStanceBones[i]) as DataTreeDictionary)?.TryGetNode("Data") is not DataTreeValue { Value: string id }
                || id.Length == 0 || PackageMemberMapper.IsNullId(id))
                continue;
            var bone = resolveSlot(id);
            if (bone == null)
                continue;

            var position = positions[i - 1] is DataTreeList p ? PackageMemberMapper.ReadNumbers(p) : null;
            var rotation = rotations[i - 1] is DataTreeList r ? PackageMemberMapper.ReadNumbers(r) : null;
            if (position is not { Length: >= 3 } || rotation is not { Length: >= 4 })
                continue;

            // The stored stance is in the same left-handed frame as everything else in the package.
            bone.LocalPosition.Value = PackageHandedness.Point(new float3(position[0], position[1], position[2]));
            bone.LocalRotation.Value = PackageHandedness.Rotation(new floatQ(rotation[0], rotation[1], rotation[2], rotation[3]));
            written++;
        }

        Logger.Debug($"Package import: stance restored from the stored default pose, {written} bones");
        return written;
    }

    // Package pose proxies come in pairs: the proxy slot carries the pose node and was seated at the
    // tracked reference (headset, controller, tracker), and its "Target" child was seated at the bone,
    // so the bone always equals proxy * Target.local. Re-seating solves that for the proxy:
    // proxy = bone * Target.local^-1. Proxies without a Target child are left alone, and feet seat on
    // the toes when the rig has them, which is the bone the package seated them on.
    public static int ReseatPoseProxies(Slot avatar, HumanoidRig rig)
    {
        int seated = 0;
        foreach (var driver in avatar.GetComponentsInChildren<AvatarPoseDriver>())
        {
            if (driver.IsDestroyed || driver.Slot == null || driver.Slot.IsDestroyed)
                continue;

            Slot? bone = driver.Node.Value switch
            {
                Lumora.Core.Input.BodyNode.Head => rig.TryGetBone(Lumora.Core.Input.BodyNode.Head),
                Lumora.Core.Input.BodyNode.Hips => rig.TryGetBone(Lumora.Core.Input.BodyNode.Hips),
                Lumora.Core.Input.BodyNode.LeftHand => rig.TryGetBone(Lumora.Core.Input.BodyNode.LeftHand),
                Lumora.Core.Input.BodyNode.RightHand => rig.TryGetBone(Lumora.Core.Input.BodyNode.RightHand),
                Lumora.Core.Input.BodyNode.LeftFoot => rig.TryGetBone(Lumora.Core.Input.BodyNode.LeftToes) ?? rig.TryGetBone(Lumora.Core.Input.BodyNode.LeftFoot),
                Lumora.Core.Input.BodyNode.RightFoot => rig.TryGetBone(Lumora.Core.Input.BodyNode.RightToes) ?? rig.TryGetBone(Lumora.Core.Input.BodyNode.RightFoot),
                _ => null,
            };
            if (bone == null || bone.IsDestroyed)
                continue;

            var proxy = driver.Slot;
            var target = proxy.FindChild("Target", recursive: false);
            if (target == null || target.IsDestroyed)
                continue;

            proxy.GlobalRotation = bone.GlobalRotation * target.LocalRotation.Value.Inverse;
            proxy.GlobalPosition += bone.GlobalPosition - target.GlobalPosition;
            seated++;
        }
        return seated;
    }

    // An avatar's own colliders are there to be POINTED AT, not walked into.
    //
    // A package's static colliders become StaticBody3D here, which is a wall: an imported avatar standing in
    // the world is a body-shaped lump of level geometry that shoves you around, and wearing it does not
    // help because the wearer collides with their own ribcage. Query-only is the same answer we already
    // reach for on our own generated bone capsules - still hit by the laser, by grab and by touch, never
    // by a character controller. Deliberately NOT applied to Active colliders, which are authored
    // physics props and are meant to have mass. -xlinka
    private static void SoftenAvatarColliders(Slot avatar, BuildContext context)
    {
        foreach (var collider in avatar.GetComponentsInChildren<Collider>())
        {
            if (collider.IsDestroyed || collider.Type.Value != Lumora.Core.Physics.ColliderType.Static)
                continue;

            collider.Type.Value = Lumora.Core.Physics.ColliderType.Trigger;
            context.Result.CollidersSoftened++;
        }
    }

    private static void BuildSkeletons(Slot root, BuildContext context)
    {
        // Snapshot the renderers BEFORE building anything. Each skeleton adds a slot and attaches a
        // component, which mutates the very hierarchy the search is walking - enumerating it live threw
        // "Collection was modified" and failed the whole import.
        var pending = new List<SkinnedMeshRenderer>();
        foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (renderer.Skeleton.Target == null && renderer.Bones.Count > 0)
                pending.Add(renderer);
        }

        foreach (var renderer in pending)
        {
            if (renderer.IsDestroyed || renderer.Slot == null)
                continue;

            var bones = new List<Slot>();
            foreach (var bone in renderer.Bones)
            {
                if (bone != null && !bone.IsDestroyed)
                    bones.Add(bone);
            }
            if (bones.Count == 0)
                continue;

            var rigRoot = Topmost(bones);
            if (rigRoot == null)
                continue;

            // The builder sits directly under the ROOT BONE'S PARENT, and this is load-bearing.
            // SkeletonHook offsets the Skeleton3D by `inverse(builderSlot.global) * firstBone.Parent.global`
            // - note it takes the FIRST BONE ADDED, not RootBone. Parenting the builder anywhere else
            // leaves a non-identity offset that shifts the whole skin away from its bones, which is what
            // put the tail in the wrong place. A plain child slot has an identity local transform, so
            // this makes that offset exactly identity - which is what the bind-derived rests below want,
            // since they already place the skin in the mesh's own bind space. -xlinka
            var skeletonParent = rigRoot.Parent ?? NearestShared(renderer.Slot, rigRoot) ?? rigRoot;
            var holder = skeletonParent.AddSlot("Skeleton");
            var builder = holder.AttachComponent<SkeletonBuilder>();

            builder.ClearBones();
            builder.RootBone.Target = rigRoot;

            // Parents before children: the skeleton is built in order, and a bone whose parent has not
            // been added yet has nothing to attach to.
            bones.Sort((a, b) => Depth(a).CompareTo(Depth(b)));

            // Rest poses come from the MESH'S BIND POSES, not from the bone slots' transforms.
            //
            // Skinning is only correct when a bone's accumulated global rest is the exact inverse of its
            // bind pose - that is literally what SkinnedMeshHook's canary checks. Slot transforms do not
            // satisfy that: the saved pose is not the bind pose, intermediate non-bone slots drop out of
            // the chain, and any scale on the way down compounds. Reading it off the bind pose makes the
            // identity hold by construction, for every bone, whatever the hierarchy did.
            //
            //     globalRest = bind^-1        so       localRest = bind_parent * bind^-1
            //
            // A bone the mesh does not bind falls back to its slot transform - it deforms nothing, it
            // just needs to keep the chain intact for its children. -xlinka
            var binds = BindPosesFor(renderer, context);
            var boneSet = new HashSet<Slot>(bones);

            // POSITION, NOT NAME. renderer.Bones[i] is the mesh's bone i, so pairing them by index is
            // exact and survives whatever the author called the slot. The name table stays as a
            // fallback for the case the two lists disagree in length, which would mean the renderer
            // and the mesh were never a matched pair in the first place.
            var bindByBone = new Dictionary<Slot, float4x4>();
            var order = BindOrderFor(renderer, context);
            if (order != null)
            {
                int limit = System.Math.Min(order.Length, renderer.Bones.Count);
                for (int i = 0; i < limit; i++)
                {
                    var bone = renderer.Bones[i];
                    if (bone != null && !bone.IsDestroyed)
                        bindByBone[bone] = order[i];
                }
                if (order.Length != renderer.Bones.Count)
                {
                    Logger.Warn($"Package import: '{renderer.Slot.SlotName.Value}' binds {renderer.Bones.Count} bones "
                              + $"but its mesh carries {order.Length}; the overlap is paired by position, "
                              + "the rest fall back to names.");
                }
            }

            bool TryBind(Slot? bone, out float4x4 bind)
            {
                bind = default;
                if (bone == null)
                    return false;
                if (bindByBone.TryGetValue(bone, out bind))
                    return true;
                return binds != null && binds.TryGetValue(bone.SlotName.Value, out bind);
            }

            var skeletonNames = SkeletonNamesAsMeshBinds(bones, renderer, order, binds);

            foreach (var bone in bones)
            {
                float4x4 rest;
                string boneName = skeletonNames.GetValueOrDefault(bone, bone.SlotName.Value);

                if (TryBind(bone, out var bind))
                {
                    var parentBone = NearestBoneAncestor(bone, boneSet);
                    if (TryBind(parentBone, out var parentBind))
                        rest = parentBind * bind.Inverse;
                    else
                        rest = bind.Inverse;
                }
                else
                {
                    rest = float4x4.Translate(bone.LocalPosition.Value)
                         * float4x4.Rotate(bone.LocalRotation.Value)
                         * float4x4.Scale(bone.LocalScale.Value);
                }

                builder.AddBone(boneName, bone, rest);
            }

            builder.IsBuilt.Value = true;
            builder.BoneHierarchyChanged = true;
            renderer.Skeleton.Target = builder;

            context.Result.SkeletonsBuilt++;
            Logger.Debug($"Package import: skeleton for '{renderer.Slot.SlotName.Value}' - {bones.Count} bones, "
                     + $"root '{rigRoot.SlotName.Value}' under '{skeletonParent.SlotName.Value}'"
                     + $", {bindByBone.Count}/{bones.Count} rests from bind poses by position"
                     + (binds != null ? string.Empty : ", no bind table"));
        }
    }

    // The bind table for whatever mesh this renderer is actually showing.
    private static Dictionary<string, float4x4>? BindPosesFor(SkinnedMeshRenderer renderer, BuildContext context)
        => MeshUrlOf(renderer) is string url ? context.MeshBindPoses.GetValueOrDefault(url) : null;

    // The same table in the mesh's own bone ORDER, which is what the renderer's Bones list is parallel to.
    private static float4x4[]? BindOrderFor(SkinnedMeshRenderer renderer, BuildContext context)
        => MeshUrlOf(renderer) is string url ? context.MeshBindOrder.GetValueOrDefault(url) : null;

    // The skin finds its bones by the names the mesh gives them, while the package pairs mesh bones with slots by
    // position. A slot renamed or reordered after export then pulled its vertices onto another bone: the attic flag
    // numbers its strip roots in a different order from its mesh, and its top edge tore into spikes. So a bone whose
    // own name would bind somewhere else, or nowhere, takes the mesh's name for it in the skeleton. A bone that
    // already binds right keeps its slot's name, and a rename that would leave two bones one name is not made.
    // -xlinka
    private static Dictionary<Slot, string> SkeletonNamesAsMeshBinds(List<Slot> bones, SkinnedMeshRenderer renderer,
        float4x4[]? order, Dictionary<string, float4x4>? binds)
    {
        var names = new Dictionary<Slot, string>();
        foreach (var bone in bones)
            names[bone] = bone.SlotName.Value;
        if (order == null || binds == null)
            return names;

        // The bind table keeps names by bone; the order keeps poses by position. A pose only one name carries says
        // which name sits at that position.
        var meshNames = new Dictionary<Slot, string>();
        int limit = System.Math.Min(order.Length, renderer.Bones.Count);
        for (int i = 0; i < limit; i++)
        {
            var bone = renderer.Bones[i];
            if (bone == null || bone.IsDestroyed)
                continue;
            string? found = null;
            bool alike = false;
            foreach (var (name, pose) in binds)
            {
                if (pose != order[i])
                    continue;
                alike = found != null;
                found = name;
                if (alike)
                    break;
            }
            if (found != null && !alike)
                meshNames[bone] = found;
        }

        var bySlotName = new Lumora.Core.Assets.BoneNameMatcher();
        for (int b = 0; b < bones.Count; b++)
            bySlotName.Add(bones[b].SlotName.Value, b);
        for (int b = 0; b < bones.Count; b++)
        {
            var bone = bones[b];
            if (!meshNames.TryGetValue(bone, out var meshName) || meshName == bone.SlotName.Value)
                continue;
            if (!bySlotName.TryResolve(meshName, out int lands, out _) || !ReferenceEquals(bones[lands], bone))
                names[bone] = meshName;
        }

        bool undone = true;
        while (undone)
        {
            undone = false;
            var uses = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var name in names.Values)
                uses[name] = uses.GetValueOrDefault(name) + 1;
            foreach (var bone in bones)
            {
                if (names[bone] != bone.SlotName.Value && uses[names[bone]] > 1)
                {
                    names[bone] = bone.SlotName.Value;
                    undone = true;
                }
            }
        }
        int renamed = 0;
        foreach (var (bone, name) in names)
            if (name != bone.SlotName.Value)
                renamed++;
        if (renamed > 0)
            Logger.Debug($"Package import: {renamed} bones of '{renderer.Slot.SlotName.Value}' named as its mesh names them");
        return names;
    }

    private static string? MeshUrlOf(SkinnedMeshRenderer renderer)
        => ((renderer.MeshAsset.Target as StaticAssetProvider<MeshDataAsset>)?.URL.Value)?.OriginalString;

    // The closest ancestor that is also a bone of this skeleton - which is what the skeleton will use
    // as the parent, regardless of how many non-bone slots sit between them.
    private static Slot? NearestBoneAncestor(Slot bone, HashSet<Slot> bones)
    {
        for (var walk = bone.Parent; walk != null; walk = walk.Parent)
        {
            if (bones.Contains(walk))
                return walk;
        }
        return null;
    }

    // The bone nearest the top of the hierarchy - the rig root.
    private static Slot? Topmost(List<Slot> bones)
    {
        Slot? best = null;
        int bestDepth = int.MaxValue;
        foreach (var bone in bones)
        {
            int depth = Depth(bone);
            if (depth < bestDepth)
            {
                bestDepth = depth;
                best = bone;
            }
        }
        return best;
    }

    private static Slot? NearestShared(Slot? a, Slot? b)
    {
        if (a == null || b == null)
            return null;

        var ancestors = new HashSet<Slot>();
        for (var walk = a; walk != null; walk = walk.Parent)
            ancestors.Add(walk);
        for (var walk = b; walk != null; walk = walk.Parent)
        {
            if (ancestors.Contains(walk))
                return walk;
        }
        return null;
    }

    private static int Depth(Slot slot)
    {
        int depth = 0;
        for (var walk = slot?.Parent; walk != null; walk = walk.Parent)
            depth++;
        return depth;
    }

    // The one thing that decides whether any of this is visible: did the renderers actually end up
    // holding a mesh and a material. Counted after the fact from the built tree rather than trusted.
    private static void LogRendererWiring(Slot root)
    {
        int renderers = 0, withMesh = 0, withMaterial = 0;
        int skinned = 0, skinnedWithMesh = 0, skinnedWithBones = 0;

        // One walk for the whole report, sorted by kind: it was six, each over everything imported.
        var everything = new List<Component>();
        root.GetComponentsInChildren(everything);
        var meshRenderers = new List<MeshRenderer>();
        var skinnedRenderers = new List<SkinnedMeshRenderer>();
        var materials = new List<MaterialProvider>();
        var lights = new List<Light>();
        foreach (var component in everything)
        {
            switch (component)
            {
                case MeshRenderer mesh: meshRenderers.Add(mesh); break;
                case SkinnedMeshRenderer skin: skinnedRenderers.Add(skin); break;
                case MaterialProvider material: materials.Add(material); break;
                case Light light: lights.Add(light); break;
            }
        }

        foreach (var renderer in meshRenderers)
        {
            renderers++;
            if (renderer.Mesh.Target != null) withMesh++;
            if (renderer.Materials.Count > 0) withMaterial++;
        }
        foreach (var renderer in skinnedRenderers)
        {
            skinned++;
            if (renderer.MeshAsset.Target != null) skinnedWithMesh++;
            if (renderer.Bones.Count > 0) skinnedWithBones++;
        }

        Logger.Debug($"Package import: {withMesh}/{renderers} mesh renderers have a mesh, {withMaterial} have a material; "
                 + $"{skinnedWithMesh}/{skinned} skinned renderers have a mesh, {skinnedWithBones} have bones");

        // Name every renderer that got geometry but no material.
        //
        // This is not a cosmetic gap. With no material we hand Godot a null surface override and it
        // substitutes its own default white-grey, so the thing draws as a big pale untextured blob that
        // rides along wherever the object goes. Counting them told us one existed and nothing about
        // WHICH, and hunting a nameless grey ball through a world is miserable. -xlinka
        // And every renderer that got NO GEOMETRY, which is the other way to be invisible and the one
        // the hook's own report cannot cover: with no mesh there is nothing to build, so the line that
        // would have named it never runs and the object leaves no trace anywhere. -xlinka
        foreach (var renderer in meshRenderers)
        {
            if (renderer.Mesh.Target != null)
                continue;
            Logger.Warn($"Package import: renderer '{PathOf(renderer.Slot)}' has NO MESH; "
                      + $"it has {renderer.Materials.Count} material(s) and will draw nothing");
        }

        // A canvas's own chunk renderers are not the package's: the canvas fills their mesh and materials when
        // it lays itself out, which for a canvas under an inactive slot is the moment the slot comes on. -xlinka
        foreach (var renderer in meshRenderers)
        {
            if (renderer.Mesh.Target == null || renderer.Materials.Count > 0 || renderer.Mesh.Target is Helio.UI.LocalMeshSource)
                continue;
            Logger.Warn($"Package import: renderer '{PathOf(renderer.Slot)}' has mesh "
                      + $"{renderer.Mesh.Target.GetType().Name} but NO material; it would draw as an "
                      + "untextured grey default, so it is left hidden");
        }

        // Every imported material's transparency and culling, by name.
        //
        // A fur or hair shell is alpha-cut geometry: hundreds of cards that are mostly transparent. If
        // the imported material lands on Opaque, every card renders solid and the mesh becomes a thick
        // block hugging the body, which reads as a fat black outline rather than as fur. Guessing at
        // which of the material properties failed to carry cost more time than printing them ever will.
        // -xlinka
        foreach (var material in materials)
        {
            if (material == null || material.IsDestroyed)
                continue;
            string alpha = "n/a";
            string cull = "n/a";
            var type = material.GetType();
            if (type.GetField("AlphaMode")?.GetValue(material) is Sync<AlphaMode> am)
                alpha = am.Value.ToString();
            else if (type.GetField("BlendMode")?.GetValue(material) is Sync<Assets.BlendMode> bm)
                alpha = bm.Value.ToString();
            if (type.GetField("Culling")?.GetValue(material) is Sync<Culling> cu)
                cull = cu.Value.ToString();
            Logger.Debug($"Package import: material '{material.Slot?.SlotName.Value}' {type.Name} "
                     + $"alpha={alpha} culling={cull}");
        }

        AuditRenderers(meshRenderers, materials);
        AuditLights(lights);
    }

    // Every light the package brought, grep "LIGHT:".
    //
    // A light has no geometry to look at, so a wrong one is only ever diagnosed by its effect on
    // everything else - and "the import is too bright" has at least four causes that look identical
    // from across the room. Print what was actually authored. -xlinka
    private static void AuditLights(List<Light> lights)
    {
        if (!Logger.EnableDebug)
            return;
        foreach (var light in lights)
        {
            if (light == null || light.IsDestroyed)
                continue;

            var c = light.LightColor.Value;
            // Scale, because a light's range is LOCAL: a lamp parented into a bone chain that carries a
            // scale has an effective range nothing in its own numbers reveals.
            var scale = light.Slot?.GlobalScale ?? Lumora.Core.Math.float3.One;

            Logger.Debug($"LIGHT: '{PathOf(light.Slot)}' type={light.Type.Value}"
                     + $" colour={c.r:0.##},{c.g:0.##},{c.b:0.##}"
                     + $" intensity={light.Intensity.Value:0.###}"
                     + $" range={light.Range.Value:0.##}"
                     + $" spotAngle={light.SpotAngle.Value:0.#}(full)"
                     + $" shadows={light.Shadows.Value}"
                     + $" enabled={light.Enabled}"
                     + $" scale={scale.x:0.###},{scale.y:0.###},{scale.z:0.###}"
                     + $" effectiveRange={light.Range.Value * System.Math.Max(scale.x, System.Math.Max(scale.y, scale.z)):0.###}");
        }
    }

    // Every renderer under the import keyed by the thing you can point at in the world, grep "RENDER:".
    //
    // The material dump above keys on SLOT NAME, and an imported package parks every material it ships
    // on one slot called 'Assets'. Forty-six lines reading 'Assets' PBS_Metallic answer nothing about
    // the one prop that is painting itself white, so this walks the other way: renderer first, then the
    // material actually bound to each of its surfaces.
    //
    // Scale sign is in here because a MIRRORED prop reads as a texture bug and is not one. A negative
    // determinant flips triangle winding, so back faces survive culling and the mesh is seen from the
    // inside: lettering reads backwards and the shading comes off normals that now point away. Any time
    // a texture looks right but reversed, check this line before touching the texture pipeline. -xlinka
    private static void AuditRenderers(List<MeshRenderer> renderers, List<MaterialProvider> materials)
    {
        if (!Logger.EnableDebug)
            return;
        var owners = new Dictionary<MaterialAsset, MaterialProvider>();
        foreach (var provider in materials)
        {
            if (provider == null || provider.IsDestroyed)
                continue;
            var asset = provider.Asset;
            if (asset != null)
                owners[asset] = provider;
        }

        // An avatar has dozens of renderers and every line is wanted; a scene of tens of thousands spent most
        // of a second, and the log, on lines nobody reads past the first few hundred. -xlinka
        int listed = 0, unlisted = 0;
        foreach (var renderer in renderers)
        {
            if (renderer == null || renderer.IsDestroyed || renderer.Slot == null)
                continue;
            if (listed >= AuditLineLimit)
            {
                unlisted++;
                continue;
            }
            listed++;

            var scale = renderer.Slot.GlobalScale;
            bool mirrored = scale.x * scale.y * scale.z < 0f;

            var line = new StringBuilder();
            line.Append("RENDER: '").Append(PathOf(renderer.Slot)).Append("'");
            line.Append(" mesh=").Append(renderer.Mesh.Target?.GetType().Name ?? "NULL");
            line.Append(" scale=").Append($"{scale.x:0.###},{scale.y:0.###},{scale.z:0.###}");
            if (mirrored)
                line.Append(" MIRRORED");
            line.Append(" mats=").Append(renderer.Materials.Count);

            for (int i = 0; i < renderer.Materials.Count; i++)
            {
                var asset = renderer.Materials.GetElement(i).Target?.Asset;
                line.Append(" [").Append(i).Append("] ");
                if (asset == null)
                {
                    line.Append("NULL");
                    continue;
                }
                if (!owners.TryGetValue(asset, out var provider) || provider == null)
                {
                    line.Append("orphan-asset");
                    continue;
                }
                line.Append(DescribeMaterial(provider));
            }

            Logger.Debug(line.ToString());
        }
        if (unlisted > 0)
            Logger.Debug($"RENDER: {unlisted} more renderers not listed");
    }

    private const int AuditLineLimit = 256;

    // Type, blend, and the two things that decide whether a surface comes out white: the albedo TINT and
    // whether an albedo texture is bound at all. A bound texture under a blown tint and no texture under
    // a white tint look identical on screen and want opposite fixes. -xlinka
    private static string DescribeMaterial(MaterialProvider provider)
    {
        var type = provider.GetType();
        var text = new StringBuilder(type.Name);

        if (type.GetField("BlendMode")?.GetValue(provider) is Sync<Assets.BlendMode> bm)
            text.Append(" blend=").Append(bm.Value);
        else if (type.GetField("AlphaMode")?.GetValue(provider) is Sync<AlphaMode> am)
            text.Append(" blend=").Append(am.Value);

        if (type.GetField("AlbedoColor")?.GetValue(provider) is Sync<colorHDR> albedo)
        {
            var c = albedo.Value;
            text.Append(" tint=").Append($"{c.r:0.##},{c.g:0.##},{c.b:0.##},{c.a:0.##}");
            if (c.r > 1f || c.g > 1f || c.b > 1f)
                text.Append("(HDR>1)");
        }

        if (type.GetField("AlbedoTexture")?.GetValue(provider) is IAssetRef tex)
            text.Append(" albedoTex=").Append(tex.Target != null ? "set" : "NONE");

        text.Append(provider.IsAssetAvailable ? " ready" : " NOTREADY");

        // The difference between "a texture is assigned" and "a texture is on screen". Everything above
        // reads the DATA MODEL, which says assigned; this reads whether the upload ever reached the
        // Godot material. A surface that is assigned, ready, and still listed here is rendering its flat
        // albedo tint, which on a white tint is indistinguishable from a missing texture. -xlinka
        var asset = provider.Asset;
        if (asset != null && asset.HasTexturesStillArriving)
            text.Append(" NOTONGPU=").Append(asset.TexturesStillArriving);

        return text.ToString();
    }

    // Every asset reference on every imported material, one line each, grep "MATREF:".
    //
    // A material reports READY the moment none of its references is still loading, and an unset
    // reference is not loading - so a material whose albedo link was lost in the wiring pass looks
    // exactly like one whose author left the albedo empty: both render the flat colour, both pass the
    // readiness check, and neither says a word. The link pass counts its failures in aggregate, which
    // told us 103 references dangled on one avatar and nothing about which slot on which material.
    // This is the per-slot answer, taken once at the end of the import, with the texture's load state
    // at that moment so a slow decode and a lost link read differently. -xlinka
    private static void LogMaterialReferences(Slot root, BuildContext context)
    {
        int materials = 0, wired = 0, dangling = 0, empty = 0, pending = 0;

        foreach (var material in root.GetComponentsInChildren<MaterialProvider>())
        {
            if (material == null || material.IsDestroyed)
                continue;
            materials++;
            string owner = $"'{material.Slot?.SlotName.Value}' {material.GetType().Name}";

            for (int i = 0; i < material.SyncMemberCount; i++)
            {
                if (material.GetSyncMember(i) is not IAssetRef reference)
                    continue;
                string member = material.GetSyncMemberName(i);

                if (reference is ISyncRef syncRef && context.RefFailures.TryGetValue(syncRef, out var why))
                {
                    dangling++;
                    Logger.Warn($"MATREF: {owner}.{member} -> DANGLING: {why}");
                    continue;
                }

                var target = reference.Target;
                if (target == null)
                {
                    empty++;
                    Logger.Debug($"MATREF: {owner}.{member} -> empty (authored null)");
                    continue;
                }

                wired++;
                string state;
                string url = "";
                if (target is IUrlAssetProvider urlProvider)
                {
                    if (urlProvider.IsLoadPending)
                        pending++;
                    url = " url=" + (urlProvider.SourceUrl?.ToString() ?? "<none>");
                    state = target is StaticAssetProvider<TextureAsset> texture ? texture.LoadStateDescription
                        : target is StaticAssetProvider<CubemapAsset> cubemap ? cubemap.LoadStateDescription
                        : urlProvider.IsLoadPending ? "pending" : "ready";
                }
                else
                {
                    state = target.IsAssetAvailable ? "available (procedural)" : "not built yet (procedural)";
                }

                var targetComponent = target as Component;
                string home = targetComponent?.Slot != null ? $" on '{PathOf(targetComponent.Slot)}'" : "";
                Logger.Debug($"MATREF: {owner}.{member} -> {target.GetType().Name} [{state}]{url}{home}");
            }
        }

        Logger.Debug($"MATREF: summary - {materials} materials, {wired} asset refs wired ({pending} still loading at import end), "
                 + $"{dangling} dangling, {empty} authored empty");
    }

    private static string ShortId(string id) => id.Length > 12 ? id[..12] : id;

    private static string PathOf(Slot? slot)
    {
        if (slot == null)
            return "(none)";
        var parts = new System.Collections.Generic.List<string>();
        for (var s = slot; s != null && parts.Count < 12; s = s.Parent)
            parts.Add(s.SlotName.Value ?? "?");
        parts.Reverse();
        return string.Join("/", parts);
    }

    private sealed class BuildContext
    {
        public readonly World World;
        public readonly List<PackageTypeMap.Resolution> Types;
        public readonly Dictionary<string, Uri> AssetUrls;
        public readonly PackageImportResult Result;

        // The package's element id -> the thing we built for it, so references can be reconnected once the
        // whole tree exists. SLOTS go in here as well as components: bone lists on a skinned renderer
        // are slot references, and with only components recorded they resolved to nothing and the mesh
        // stayed in its bind pose. -xlinka
        public readonly Dictionary<string, IWorldElement> ById = new(StringComparer.OrdinalIgnoreCase);

        // The package's own type names, and what its node graphs need kept: the nodes, the slots, the members.
        public readonly List<string> TypeNames = new();
        public readonly PackageNodeInput Graphs = new();
        public readonly List<PendingLink> Links = new();
        public readonly List<PendingRef> RefLinks = new();

        // Why a single reference stayed unset, keyed by the reference itself. A dangling count says
        // how many links failed and nothing about which; the material diagnostic at the end reads
        // this to say, per texture slot, whether the author left it empty or the wiring lost it.
        public readonly Dictionary<ISyncRef, string> RefFailures = new(ReferenceEqualityComparer.Instance);

        // Raw member data for components we do NOT attach but still have to read THROUGH. A particle's
        // look is not on the particle system: it hangs off a style, which hangs off a renderer, which
        // finally names the material. Two of those three links have no counterpart here, and a chain
        // that stops at the first unsupported link takes the sprite with it.
        public readonly Dictionary<string, DataTreeDictionary> PassThrough = new(StringComparer.OrdinalIgnoreCase);

        // What each pass-through entry actually IS. The data alone cannot tell a renderer that draws
        // nothing from one whose material simply failed to resolve, and those mean opposite things.
        public readonly Dictionary<string, string> PassThroughTypes = new(StringComparer.OrdinalIgnoreCase);

        // A sprite's id -> its texture's. Ours take the texture where a package takes a sprite of it.
        public readonly Dictionary<string, string> SpriteTextures = new(StringComparer.OrdinalIgnoreCase);

        // The package's component naming the ring a menu entry leads to, and the slot it sat on.
        public readonly List<(Slot Slot, DataTreeDictionary Data)> Submenus = new();

        // Walking locomotions and the character settings beside them, by slot.
        public readonly List<(Slot Slot, string Type, DataTreeDictionary Data)> Locomotion = new();

        // Bench touch surfaces and the planes they press seats onto, by slot.
        public readonly List<(Slot Slot, string Type, DataTreeDictionary Data)> Benches = new();

        // Template lists and the parts the package kept beside them (WorldK7).
        public readonly List<(Slot Slot, string Type, DataTreeDictionary Data)> TemplateLists = new();

        // Canvases with the size the package gave them, which ours keeps on the root rect instead.
        public readonly List<(Helio.UI.Canvas Canvas, DataTreeDictionary Data)> Canvases = new();

        // Each converted mesh's bounds, by the local asset URL, for sizing LOD groups.
        public readonly Dictionary<string, BoundingBox> MeshBounds = new(StringComparer.OrdinalIgnoreCase);

        // LOD groups with the package's levels, converted once their renderers exist.
        public readonly List<(LodGroup Group, DataTreeDictionary Data)> LodGroups = new();

        // The version each package type was saved at, by simple name; a type missing from the table is version 0.
        public readonly Dictionary<string, int> TypeVersions = new(StringComparer.Ordinal);

        // Components whose package colour closes over our plain color, with the type; see PlainColourComponents.
        public Dictionary<string, Type> PlainColours = new(StringComparer.OrdinalIgnoreCase);

        // Lights, whose range the package scales by the slot's world scale.
        public readonly List<Light> Lights = new();

        // Font chains built as font providers, with the fonts they name.
        public readonly List<(Components.Assets.FontProvider Provider, DataTreeDictionary Data)> FontChains = new();

        public readonly List<(Components.ParticleSystem System, DataTreeDictionary Data)> ParticleSystems = new();

        // Bone name -> bind pose, per converted mesh, keyed by the local asset URL we wrote it to.
        // Captured at decode time because the skeleton has to be built before the provider has
        // finished loading the asset back.
        public readonly Dictionary<string, float4x4[]> MeshBindOrder =
            new(StringComparer.OrdinalIgnoreCase);

        public readonly Dictionary<string, Dictionary<string, float4x4>> MeshBindPoses =
            new(StringComparer.OrdinalIgnoreCase);

        // Each converted mesh's shape names in mesh order, by the local asset URL. A package names a shape by its
        // index in a renderer's weights; the mesh only says which shape that is once it has loaded.
        public readonly Dictionary<string, string[]> MeshShapeNames = new(StringComparer.OrdinalIgnoreCase);

        // The package's voice-driven mouths, with what they stored, named once the field links exist, and the
        // named-shape drivers a mouth can drive through, read and not attached.
        public readonly List<(VisemeWeightDriver Driver, DataTreeDictionary Data)> Visemes = new();
        public readonly List<DataTreeDictionary> NamedShapeDrivers = new();

        // Face tracking onto named mouth shapes, and the package's eye parts, read into our face drivers.
        public readonly List<(MouthExpressionDriver Driver, DataTreeDictionary Data)> Expressions = new();
        public readonly List<(string Type, DataTreeDictionary Data)> EyeParts = new();

        // When this frame's share of the world thread runs out.
        public long FrameEnd;

        public BuildContext(World world, List<PackageTypeMap.Resolution> types,
            Dictionary<string, Uri> assetUrls, PackageImportResult result)
        {
            World = world;
            Types = types;
            AssetUrls = assetUrls;
            Result = result;
        }
    }

    private readonly struct PendingLink
    {
        public readonly Component Owner;
        public readonly object List;
        public readonly string TargetId;

        public PendingLink(Component owner, object list, string targetId)
        {
            Owner = owner;
            List = list;
            TargetId = targetId;
        }
    }

    // SyncRefList<T>.Add(T) and SyncAssetList<A>.Add(IAssetProvider<A>) have different signatures, so
    // pick whichever public Add the list actually exposes that will take this target.
    private static bool AppendToList(object list, IWorldElement target)
    {
        foreach (var method in list.GetType().GetMethods())
        {
            if (method.Name != "Add")
                continue;
            var parameters = method.GetParameters();
            if (parameters.Length != 1)
                continue;
            if (!parameters[0].ParameterType.IsInstanceOfType(target))
                continue;
            method.Invoke(list, new object[] { target });
            return true;
        }
        return false;
    }

    // Grow a reference list by one empty entry, so the entries after it keep their index. The
    // parameterless Add() that every sync list has returns the new element already cleared.
    private static bool AppendEmptySlot(object list)
    {
        // Walked by hand: SyncAssetList declares its own Add(IAssetProvider<A>), and reflection
        // resolves a name at the first level of the hierarchy that declares it - so asking for
        // Add() with no arguments comes back null on exactly the lists this is for.
        for (var level = list.GetType(); level != null; level = level.BaseType)
        {
            foreach (var method in level.GetMethods(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (method.Name != "Add" || method.GetParameters().Length != 0 || method.ReturnType == typeof(void))
                    continue;
                method.Invoke(list, null);
                return true;
            }
        }
        return false;
    }

    private readonly struct PendingRef
    {
        public readonly ISyncRef Reference;
        public readonly string TargetId;

        public PendingRef(ISyncRef reference, string targetId)
        {
            Reference = reference;
            TargetId = targetId;
        }
    }

    // References between components are stored as the target's own id, so they can only be reconnected
    // once every component exists. This is the pass that makes an imported object VISIBLE rather than
    // merely present: a renderer with no mesh and no material renders nothing at all, and both of those
    // are references. -xlinka
    private static async Task ResolveLinksAsync(BuildContext context)
    {
        // Single references first: meshes on renderers, textures on materials. TrySet refuses a target
        // of the wrong type on its own, so a mismatch costs one silent skip rather than an exception.
        var toMembers = new List<PendingRef>();
        foreach (var pending in context.RefLinks)
        {
            await PaceAsync(context);
            // The component holding the reference was pruned after it was read.
            if (pending.Reference is Lumora.Core.Networking.Sync.SyncElement { IsDisposed: true })
                continue;
            // A reference that takes our own sprite keeps it; one that wants a picture gets the sprite's texture.
            var target = context.SpriteTextures.TryGetValue(pending.TargetId, out var texture)
                && !pending.Reference.TargetType.IsAssignableFrom(typeof(Helio.UI.Sprite))
                && context.ById.TryGetValue(texture, out var drawn) ? drawn : null;
            if (target == null && !context.ById.TryGetValue(pending.TargetId, out target))
            {
                if (context.Graphs.Members.ContainsKey(pending.TargetId))
                {
                    toMembers.Add(pending);
                    continue;
                }
                context.Result.ReferencesDangling++;
                context.RefFailures[pending.Reference] = $"target {ShortId(pending.TargetId)} was never built (stripped, skipped or on a dropped slot)";
                continue;
            }
            if (target.IsDestroyed)
            {
                context.Result.ReferencesDangling++;
                context.RefFailures[pending.Reference] = $"target {ShortId(pending.TargetId)} ({target.GetType().Name}) was built and then destroyed (pruned slot)";
                continue;
            }
            try
            {
                if (pending.Reference.TrySet(target))
                {
                    context.Result.ReferencesResolved++;
                }
                else
                {
                    context.Result.ReferencesDangling++;
                    context.RefFailures[pending.Reference] = $"target {ShortId(pending.TargetId)} is a {target.GetType().Name}, which this reference does not accept";
                }
            }
            catch (Exception ex)
            {
                context.Result.ReferencesDangling++;
                context.RefFailures[pending.Reference] = $"target {ShortId(pending.TargetId)} threw on assignment: {ex.Message}";
            }
        }

        // Then reference LISTS, appended in order. Resolved by reflection rather than by naming each
        // component: bone lists, material lists and anything else list-shaped all work the same way,
        // and hardcoding the one case we happened to need first is how bones got missed.
        //
        // The package's list REPLACES whatever the list holds: anything already in it is a stray entry made
        // between attach and now, and positions matter (materials pair with submeshes by index). -xlinka
        var cleared = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var gaps = new Dictionary<string, int>(StringComparer.Ordinal);
        int listStale = 0;
        void Gap(PendingLink link, string why)
        {
            string key = $"{link.Owner.GetType().Name}.{(link.List as Lumora.Core.Networking.Sync.ISyncMember)?.Name ?? "?"}[]: {why}";
            gaps[key] = gaps.GetValueOrDefault(key) + 1;
        }
        foreach (var link in context.Links)
        {
            await PaceAsync(context);
            if (link.Owner.IsDestroyed)
                continue;
            if (cleared.Add(link.List))
            {
                try { link.List.GetType().GetMethod("Clear", Type.EmptyTypes)?.Invoke(link.List, null); } catch { }
            }
            // An entry stored with the id of somebody's field, as for a single reference below: the id went on to
            // something else and the package loads the entry empty itself. The hole is kept all the same.
            if (!context.ById.ContainsKey(link.TargetId) && context.Graphs.Members.ContainsKey(link.TargetId)
                && ListTargetType(link.List) is { } held && HoldsWholeThings(held))
            {
                listStale++;
                try { AppendEmptySlot(link.List); } catch { }
                continue;
            }
            if (!context.ById.TryGetValue(link.TargetId, out var target) || target.IsDestroyed)
            {
                context.Result.ReferencesDangling++;
                Gap(link, target == null ? "target was never built (stripped, skipped or on a dropped slot)" : "target was built and then destroyed (pruned slot)");

                // A GAP IN A MATERIAL LIST IS NOT THE SAME AS A SHORTER LIST.
                //
                // Materials are matched to submeshes BY POSITION, so skipping one that did not
                // resolve pulls every material after it up an index and quietly puts the wrong one on
                // the wrong surface. That is worse than a hole: a mask whose first material used a
                // shader we cannot build ended up wearing the transparent fresnel meant for its
                // visor, and read as missing rather than as untextured. Leave an empty slot so
                // everything after it stays where the author put it. -xlinka
                try { AppendEmptySlot(link.List); } catch { }
                continue;
            }

            try
            {
                if (AppendToList(link.List, target))
                    context.Result.ReferencesResolved++;
                else
                {
                    context.Result.ReferencesDangling++;
                    Gap(link, $"target is a {target.GetType().Name}, which this list does not take");
                    try { AppendEmptySlot(link.List); } catch { }
                }
            }
            catch
            {
                context.Result.ReferencesDangling++;
                Gap(link, "target threw on assignment");
            }
        }
        foreach (var (key, count) in gaps)
            Logger.Debug($"Package import: dangling x{count} {key}");
        if (listStale > 0)
            Logger.Debug($"Package import: {listStale} list entr(ies) stored naming a field; empty in the package too");

        // Then references to a field: what a driver drives, what a button flips, which material slot a press
        // swaps. A package names the field by its own id, kept with its owner and its name when the owner was
        // built, and ours is the member of that name, or the entry of that list. -xlinka
        int stale = 0;
        foreach (var pending in toMembers)
        {
            await PaceAsync(context);
            if (pending.Reference is Lumora.Core.Networking.Sync.SyncElement { IsDisposed: true })
                continue;
            var record = context.Graphs.Members[pending.TargetId];
            // A reference that holds a whole thing (a texture, a component, a slot) stored with the id of somebody's
            // field names something it could never hold: the id was handed on to something else, and the package
            // loads it empty itself. Not a loss here.
            if (pending.Reference is not PackageMemberMapper.IMemberOwnerReference && HoldsWholeThings(pending.Reference.TargetType))
            {
                stale++;
                continue;
            }
            var member = record.Owner.IsDestroyed ? null : PackageNodeIndex.MemberOf(record.Owner, record.Member, record.Index, record.Within)
                ?? (record.Within == null ? PackageMemberMapper.DriveTargetOf(record.Owner, record.Member, record.Index) : null)
                ?? (pending.Reference is PackageMemberMapper.IMemberOwnerReference ? record.Owner : null);
            if (member == null)
            {
                context.Result.ReferencesDangling++;
                context.RefFailures[pending.Reference] = record.Owner.IsDestroyed
                    ? $"field {record.Member} sat on a {record.Owner.GetType().Name} that was pruned"
                    : $"field {record.Member} of {record.Owner.GetType().Name} has no counterpart here";
                continue;
            }
            try
            {
                if (pending.Reference.TrySet(member))
                    context.Result.ReferencesResolved++;
                else
                {
                    context.Result.ReferencesDangling++;
                    context.RefFailures[pending.Reference] = $"field {record.Member} of {record.Owner.GetType().Name} is a {member.GetType().Name}, which this reference does not accept";
                }
            }
            catch (Exception ex)
            {
                context.Result.ReferencesDangling++;
                context.RefFailures[pending.Reference] = $"field {record.Member} threw on assignment: {ex.Message}";
            }
        }
        if (stale > 0)
            Logger.Debug($"Package import: {stale} reference(s) stored naming a field where they hold a whole thing; empty in the package too");
    }

    private static bool HoldsWholeThings(Type wanted)
        => typeof(Component).IsAssignableFrom(wanted) || wanted == typeof(Slot) || wanted == typeof(User)
            || (wanted.IsGenericType && wanted.GetGenericTypeDefinition() == typeof(IAssetProvider<>));

    // What an entry of a reference list holds: an asset list's providers, a reference list's targets.
    private static Type? ListTargetType(object list)
    {
        for (var type = list.GetType(); type != null; type = type.BaseType)
        {
            if (!type.IsGenericType)
                continue;
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(SyncAssetList<>))
                return typeof(IAssetProvider<>).MakeGenericType(type.GetGenericArguments()[0]);
            if (definition == typeof(SyncRefList<>))
                return type.GetGenericArguments()[0];
        }
        return null;
    }

    // A package keeps the ring an entry leads to on a component beside the entry; ours keeps it on the entry.
    // Text names a font chain, not a font. The chain's own fonts are providers by now, so the chain takes their
    // files: the main one first, the fallbacks after it in order.
    private static void FinishFontChains(BuildContext context)
    {
        foreach (var (provider, data) in context.FontChains)
        {
            if (provider.IsDestroyed)
                continue;
            if (ReadId(data, "MainFont") is { } mainId && FontUrl(mainId, context) is { } main)
                provider.URL.Value = main;
            if ((data.TryGetNode("FallbackFonts") as DataTreeDictionary)?.TryGetNode("Data") is DataTreeList fallbacks)
            {
                foreach (var node in fallbacks)
                {
                    if (node is DataTreeDictionary entry && entry.TryGetNode("Data") is DataTreeValue { Value: string id } && FontUrl(id, context) is { } url
                        && url != provider.URL.Value)
                        provider.FallbackURLs.Add(url);
                }
            }
            if (provider.URL.Value == null && provider.FallbackURLs.Count > 0)
            {
                provider.URL.Value = provider.FallbackURLs[0];
                provider.FallbackURLs.RemoveAt(0);
            }
        }
    }

    // A package canvas holds its size itself and stretches its root rect over it, centred on the slot. Ours has
    // no size of its own: the root rect is the canvas. Left stretched over nothing, every imported canvas
    // measured 0 x 0, laid nothing out and took no presses. A unit scale (1000 = a metre of slot is a thousand
    // canvas pixels, with the size in metres) has no setting here either, so it moves into the slot's scale and
    // the canvas's own colliders are scaled back to keep their size in the world.
    private static void FinishCanvases(BuildContext context)
    {
        foreach (var (canvas, data) in context.Canvases)
        {
            if (canvas.IsDestroyed || canvas.Slot == null)
                continue;
            if ((data.TryGetNode("Size") as DataTreeDictionary)?.TryGetNode("Data") is not DataTreeList size)
                continue;
            var values = new List<float>(2);
            foreach (var node in size)
            {
                if (node is DataTreeValue { Value: IConvertible number })
                    values.Add(Convert.ToSingle(number, System.Globalization.CultureInfo.InvariantCulture));
            }
            if (values.Count < 2 || (values[0] <= 0f && values[1] <= 0f))
                continue;
            float unitScale = ReadNumber(data, "UnitScale") ?? 1f;
            if (unitScale <= 0f || float.IsNaN(unitScale))
                unitScale = 1f;
            // An axis sized zero or less (a list that is as tall as its rows) starts with no extent at the slot
            // and takes its size from the canvas's own fitter, which grows it from its pivot.
            var half = new float2(System.Math.Max(values[0], 0f), System.Math.Max(values[1], 0f)) * (0.5f * unitScale);
            if (unitScale != 1f)
            {
                canvas.Slot.LocalScale.Value = canvas.Slot.LocalScale.Value / unitScale;
                foreach (var collider in canvas.Slot.GetComponents<Collider>())
                {
                    collider.Offset.Value = collider.Offset.Value * unitScale;
                    if (collider is BoxCollider box)
                        box.Size.Value = box.Size.Value * unitScale;
                }
            }
            var rect = canvas.Slot.GetComponent<Helio.UI.RectTransform>() ?? canvas.Slot.AttachComponent<Helio.UI.RectTransform>();
            rect.AnchorMin.Value = new float2(0.5f, 0.5f);
            rect.AnchorMax.Value = new float2(0.5f, 0.5f);
            rect.OffsetMin.Value = -half;
            rect.OffsetMax.Value = half;
            // The size stays on the canvas too, so whatever drives it there (a mirror's shape) drives ours.
            canvas.Size.Value = new float2(values[0] > 0f ? values[0] * unitScale : 0f, values[1] > 0f ? values[1] * unitScale : 0f);
        }
    }

    // The package switches a level when the object's height falls below a fraction of the screen; ours
    // switches at a distance. Same thing for a given field of view: distance = size / (2 * fraction *
    // tan(fov / 2)), with size the largest side of every level's meshes in the group's own space and the
    // usual 60 degree vertical field of view. A level that holds the group's own slot cannot be told apart
    // from the others here, so that group keeps everything showing. -xlinka
    private static void FinishLodGroups(BuildContext context)
    {
        const float HalfFovTan = 0.57735026f;
        foreach (var (group, data) in context.LodGroups)
        {
            if (group.IsDestroyed || group.Slot == null)
                continue;
            if ((data.TryGetNode("LODs") as DataTreeDictionary)?.TryGetNode("Data") is not DataTreeList lods)
                continue;
            var levels = new List<(float Height, List<Slot> Roots)>();
            var bounds = BoundingBox.Empty;
            bool usable = true;
            foreach (var node in lods)
            {
                if (node is not DataTreeDictionary lod)
                    continue;
                float height = ReadNumber(lod, "ScreenRelativeTransitionHeight") ?? 0f;
                var roots = new List<Slot>();
                if ((lod.TryGetNode("Renderers") as DataTreeDictionary)?.TryGetNode("Data") is DataTreeList renderers)
                {
                    foreach (var entry in renderers)
                    {
                        if (entry is not DataTreeDictionary holder || holder.TryGetNode("Data") is not DataTreeValue { Value: string id }
                            || !context.ById.TryGetValue(id, out var target) || target is not Component { IsDestroyed: false } renderer
                            || renderer.Slot == null)
                            continue;
                        if (renderer.Slot == group.Slot)
                            usable = false;
                        if (!roots.Contains(renderer.Slot))
                            roots.Add(renderer.Slot);
                        if (MeshUrlOfRenderer(renderer) is { } url && context.MeshBounds.TryGetValue(url, out var meshBox) && !meshBox.IsEmpty)
                        {
                            foreach (var corner in Corners(meshBox))
                                bounds.Encapsulate(renderer.Slot.LocalPointToSpace(corner, group.Slot));
                        }
                    }
                }
                levels.Add((height, roots));
            }
            if (!usable || levels.Count == 0)
                continue;
            float size = bounds.IsEmpty ? 1f : System.Math.Max(bounds.Size.x, System.Math.Max(bounds.Size.y, bounds.Size.z));
            group.Levels.Clear();
            bool cullsAfterLast = levels[^1].Height > 0f;
            foreach (var (height, roots) in levels)
            {
                float distance = height > 0f ? size / (2f * height * HalfFovTan) : 100000f;
                var level = group.Levels.Add();
                level.Distance.Value = distance;
                foreach (var root in roots)
                    level.Renderers.Add(root);
            }
            group.CullBeyondLast.Value = cullsAfterLast;
        }
    }

    private static string? MeshUrlOfRenderer(Component renderer) => renderer switch
    {
        MeshRenderer { Mesh.Target: MeshProvider provider } => provider.URL.Value?.OriginalString,
        SkinnedMeshRenderer skinned => MeshUrlOf(skinned),
        _ => null,
    };

    private static IEnumerable<float3> Corners(BoundingBox box)
    {
        for (int i = 0; i < 8; i++)
            yield return new float3((i & 1) == 0 ? box.Min.x : box.Max.x, (i & 2) == 0 ? box.Min.y : box.Max.y, (i & 4) == 0 ? box.Min.z : box.Max.z);
    }

    // A refraction saved before its depth guard existed bent everything behind it, and old fog took a 2.2
    // gamma; the package gives old saves those values on load, so they arrive with them here too.
    private static void ApplyOldSaveDefaults(Component component, string packageType, BuildContext context)
    {
        int version = context.TypeVersions.GetValueOrDefault(packageType);
        if (version >= 1)
            return;
        if (component is RefractMaterial refract)
        {
            refract.DepthFadeDivisor.Value = 1e-6f;
            refract.DepthBias.Value = 1f;
        }
        else if (component is FogBoxVolumeMaterial fog)
        {
            fog.GammaCurve.Value = 2.2f;
        }
    }

    // The package lights range * average world scale of the light's slot, read when it draws; ours lights the
    // range as written, so the scale the light stands at in the package is folded in here, once the tree exists.
    // Its lights also fade with distance the way the package renders them (LightFalloff.Inverse).
    private static void FinishLights(BuildContext context)
    {
        foreach (var light in context.Lights)
        {
            if (light.IsDestroyed || light.Slot == null)
                continue;
            var scale = light.Slot.GlobalScale;
            float average = (System.Math.Abs(scale.x) + System.Math.Abs(scale.y) + System.Math.Abs(scale.z)) / 3f;
            if (average > 0f && float.IsFinite(average))
                light.Range.Value *= average;
            light.Falloff.Value = LightFalloff.Inverse;
        }
    }

    private static Uri? FontUrl(string id, BuildContext context)
        => context.ById.TryGetValue(id, out var target) && target is Components.Assets.FontProvider { IsDestroyed: false } font
            ? font.URL.Value
            : null;

    private static void FinishMenus(BuildContext context)
    {
        foreach (var (slot, data) in context.Submenus)
        {
            if (slot.IsDestroyed || slot.GetComponent<Components.UI.ContextMenuEntry>() is not { } entry)
                continue;
            if (ReadId(data, "ItemsRoot") is { } id && context.ById.TryGetValue(id, out var target) && target is Slot { IsDestroyed: false } opens)
                entry.Opens.Target = opens;
            if ((data.TryGetNode("SearchWholeHierarchy") as DataTreeDictionary)?.TryGetNode("Data") is DataTreeValue { Value: bool deep })
                entry.Deep.Value = deep;
        }
    }

    // A walk takes its pace from the character settings beside it. One that flies comes across as our walk flying
    // (WorldUi.Flies); one that moves any other way has no counterpart and does not come across.
    private static void FinishLocomotion(BuildContext context)
    {
        foreach (var (slot, type, data) in context.Locomotion)
        {
            if (slot.IsDestroyed || slot.GetComponent<Components.PhysicalLocomotion>() is not { } walk)
                continue;
            if (type == "PhysicalLocomotion")
            {
                if ((data.TryGetNode("Mode") as DataTreeDictionary)?.TryGetNode("Data") is DataTreeValue { Value: string mode }
                    && mode != "GroundTraction" && !WorldUi.Flies(walk, mode))
                {
                    Logger.Log($"Package import: '{slot.SlotName.Value}' moves by {mode}, which no locomotion here does; left out");
                    walk.Destroy();
                }
                continue;
            }
            if (ReadNumber(data, "Speed") is { } speed)
                walk.Speed.Value = speed;
            if (ReadNumber(data, "AirSpeed") is { } air)
                walk.AirSpeed.Value = air;
            if (ReadNumber(data, "JumpSpeed") is { } jump)
                walk.JumpSpeed.Value = jump;
        }
    }

    private static float? ReadNumber(DataTreeDictionary data, string member)
        => (data.TryGetNode(member) as DataTreeDictionary)?.TryGetNode("Data") is DataTreeValue { Value: { } value }
            && value is IConvertible ? Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture) : null;

    // The id a stored reference member points at, when it points at anything.
    private static string? ReadId(DataTreeDictionary data, string member)
        => (data.TryGetNode(member) as DataTreeDictionary)?.TryGetNode("Data") is DataTreeValue { Value: string id }
            && id.Length > 0 && !PackageMemberMapper.IsNullId(id) ? id : null;

    // Copy every member whose name and shape line up, and queue the references for the second pass.
    private static void CopyMembers(Component component, DataTreeDictionary data, BuildContext context)
    {
        int matched = PackageMemberMapper.Apply(
            component,
            data,
            (reference, id) => context.RefLinks.Add(new PendingRef(reference, id)),
            (member, name, ids) =>
            {
                foreach (var id in ids)
                    context.Links.Add(new PendingLink(component, member, id));
            },
            original =>
            {
                var signature = PackageArchive.SignatureOf(original);
                if (signature == null)
                    return null;   // points outside the package; we never fetch those
                return context.AssetUrls.TryGetValue(signature, out var local) ? local : null;
            });

        // A COMPONENT THAT TOOK NOTHING IS ALMOST ALWAYS A WRONG ALIAS.
        //
        // A member whose name does not line up is skipped in silence, which is the right call for one
        // odd field and completely the wrong one when NOT ONE of them matched: that is an alias
        // pointing at a component built on a different model, and what you get is an object wearing a
        // material with no textures on it. It renders white and nothing anywhere says why. This is how
        // a package's double-sided material sat on an avatar's whole outfit looking like a texture bug.
        //
        // The package's bookkeeping members are not counted, so the threshold is about real settings. -xlinka
        const int SubstantialMemberCount = 4;
        if (matched == 0 && CountRealMembers(data) >= SubstantialMemberCount)
        {
            Logger.Warn($"Package import: {component.GetType().Name} on '{component.Slot?.SlotName.Value}' took "
                      + $"NONE of the {CountRealMembers(data)} members the package stored for it. "
                      + "The type mapping points at a component with different member names.");
        }
    }

    private static int CountRealMembers(DataTreeDictionary data)
    {
        int count = 0;
        foreach (var pair in data.Children)
        {
            if (pair.Key is "ID" or "persistent-ID" or "persistent" or "UpdateOrder" or "Enabled"
                or "HighPriorityIntegration")
                continue;
            if (pair.Key.EndsWith("-ID", StringComparison.Ordinal))
                continue;
            count++;
        }
        return count;
    }

    // The world thread's share of a frame for the import, the staged world load's own budget; true when
    // it waited for the next frame. Counting slots instead held a cheap tree to 48 a frame and let a
    // costly step run as long as it liked: a package of 196612 slots spent 4 s of one frame on the end.
    private static async ValueTask<bool> PaceAsync(BuildContext context)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (context.FrameEnd == 0)
        {
            context.FrameEnd = now + World.StagedBudgetTicks;
            return false;
        }
        if (now < context.FrameEnd)
            return false;
        await WorldContext.NextUpdate();
        context.FrameEnd = System.Diagnostics.Stopwatch.GetTimestamp() + World.StagedBudgetTicks;
        return true;
    }

    // Returns false when this slot and everything under it was stripped, so the caller can drop it.
    //
    // Async because a package is BIG: an avatar is 600+ slots and 700+ components, and building the lot
    // in one go froze the world for the better part of a minute. The build yields back to the update
    // loop whenever its share of a frame is spent, so the import takes several frames and the game stays
    // responsive instead of locking up. -xlinka
    // The awaits below deliberately do NOT use ConfigureAwait(false). Everything in this build
    // path touches the datamodel and, through the hooks, the scene tree - and the platform only allows
    // that from the main thread. NextUpdate() resumes there correctly, but ConfigureAwait(false) on the
    // recursive calls handed the continuation straight back to the thread pool, so slot creation ended
    // up calling AddChild off-thread. Background work belongs in ConvertAssetsAsync, not here. -xlinka
    private static async Task<bool> BuildSlotAsync(DataTreeDictionary node, Slot slot, BuildContext context, string? parentId = null)
    {
        if (await PaceAsync(context) && slot.IsDestroyed)
            return false;

        // Refused BEFORE anything is built, so the whole subtree goes with it: a package's laser is a slot
        // with the component on it and its mesh, cursor and line slots underneath, and dropping only the
        // component would leave those hanging off the hand doing nothing.
        if (CarriesUserspaceConflict(node, context))
        {
            context.Result.SlotsConflicting++;
            return false;
        }

        // Same treatment for scaffolding a component generated for itself: refused before anything under
        // it is built, so the mesh and renderer inside go with it instead of surviving as an orphan.
        if (ReadValue(node, "Name") is string generatedName
            && PackageTypeMap.IsGeneratedOrphanSlot(StripMarkup(generatedName)))
        {
            context.Result.SlotsConflicting++;
            Logger.Debug($"Package import: dropped generated slot '{StripMarkup(generatedName)}' and its subtree; "
                     + "the component that owns it is not ported");
            return false;
        }

        // And the same for a nameplate the author built, because we draw one over every user already.
        if (ReadValue(node, "Name") is string suppliedName
            && PackageTypeMap.IsSuppliedByUs(StripMarkup(suppliedName)))
        {
            context.Result.SlotsConflicting++;
            Logger.Debug($"Package import: dropped '{StripMarkup(suppliedName)}' and its subtree; "
                     + "this engine supplies its own nameplate");
            return false;
        }

        context.Result.SlotsCreated++;

        string? slotId = (node.TryGetNode("ID") as DataTreeValue)?.Value?.ToString() is { Length: > 0 } id ? id : null;
        if (slotId != null)
            context.ById[slotId] = slot;

        if (ReadValue(node, "Name") is string rawName)
        {
            var name = StripMarkup(rawName);
            if (name.Length > 0)
                slot.SlotName.Value = name;
        }
        if (ReadValue(node, "Tag") is string tag && tag.Length > 0)
            slot.Tag.Value = tag;
        if (ReadValue(node, "Active") is bool active)
            slot.ActiveSelf.Value = active;

        ApplyTransform(node, slot);

        if (slotId != null)
            context.Graphs.Slots[slotId] = new PackageSlotRecord(slotId, parentId, StripMarkup(ReadValue(node, "Name") as string ?? string.Empty), slot);
        context.Graphs.RecordMembers(slot, node);

        int attached = AttachComponents(node, slot, context, slotId, out int strippedHere);

        int survivingChildren = 0;
        if (node.TryGetNode("Children") is DataTreeList children)
        {
            foreach (var child in children.Children)
            {
                if (child is not DataTreeDictionary childNode)
                    continue;

                var childSlot = slot.AddSlot("Slot");
                if (await BuildSlotAsync(childNode, childSlot, context, slotId))
                    survivingChildren++;
                else if (!childSlot.IsDestroyed)
                    childSlot.Destroy();
            }
        }

        // Prune ONLY scaffolding that stripping emptied. A slot that never carried a component in the
        // first place is structure, not leftovers - and the clearest example is a skeleton: bones hold
        // nothing at all, so a rule that drops every empty slot deletes the fingertips, then their
        // parents, then the whole rig from the bottom up, and the avatar arrives with no skeleton to
        // deform. Empty-but-original slots are kept. -xlinka
        if (attached == 0 && survivingChildren == 0 && strippedHere > 0)
        {
            context.Result.SlotsPruned++;
            context.Result.SlotsCreated--;
            return false;
        }
        return true;
    }

    private static void ApplyTransform(DataTreeDictionary node, Slot slot)
    {
        // Reflected on the way in: the package is left-handed and this engine is not. Scale is a
        // magnitude per axis and stays as it is. See PackageHandedness.
        if (ReadFloat3(node, "Position") is { } position)
            slot.LocalPosition.Value = PackageHandedness.Point(position);
        if (ReadFloatQ(node, "Rotation") is { } rotation)
            slot.LocalRotation.Value = PackageHandedness.Rotation(rotation);
        if (ReadFloat3(node, "Scale") is { } scale)
            slot.LocalScale.Value = scale;
    }

    // Links in the particle look chain. Only these - the map is a read-through, not a general cache.
    private static readonly HashSet<string> PassThroughTypes = new(StringComparer.Ordinal)
    {
        "ParticleStyle",
        "BillboardParticleRenderer",
        "MeshParticleRenderer",
        // Captured, not attached: it is read to learn that this system draws no sprite at all.
        "NullParticleRenderer",
    };

    private static bool CarriesUserspaceConflict(DataTreeDictionary node, BuildContext context)
    {
        if (node.TryGetNode("Components") is not DataTreeNode componentsNode)
            return false;

        var list = FindWorkerList(componentsNode);
        if (list == null)
            return false;

        foreach (var entry in list)
        {
            if (entry is not DataTreeDictionary worker)
                continue;
            if (worker.TryGetNode("Type") is not DataTreeValue typeValue)
                continue;

            int index = SafeInt(typeValue);
            if (index < 0 || index >= context.Types.Count)
                continue;

            if (PackageTypeMap.ConflictsWithUserspace(context.Types[index].SimpleName))
                return true;
        }
        return false;
    }

    private static int AttachComponents(DataTreeDictionary node, Slot slot, BuildContext context, string? slotId, out int stripped)
    {
        stripped = 0;
        if (node.TryGetNode("Components") is not DataTreeNode componentsNode)
            return 0;

        var list = FindWorkerList(componentsNode);
        if (list == null)
            return 0;

        int attached = 0;
        foreach (var entry in list)
        {
            if (entry is not DataTreeDictionary worker)
                continue;
            if (worker.TryGetNode("Type") is not DataTreeValue typeValue)
                continue;

            int index = SafeInt(typeValue);
            if (index < 0 || index >= context.Types.Count)
                continue;

            var resolution = context.Types[index];
            // THE PASS-THROUGH CAPTURE COMES BEFORE THE STRIP, and that ordering is the whole
            // feature. A style or a particle renderer is stripped precisely BECAUSE we read its data
            // here instead of attaching it - so dropping out above this line took the style out of
            // the table the moment it was marked stripped, and every particle system in the package
            // reported "no style component to read through" and came in on bare defaults. -xlinka
            var passData = worker.TryGetNode("Data") as DataTreeDictionary;
            if (passData != null && PassThroughTypes.Contains(resolution.SimpleName)
                && (passData.TryGetNode("ID") as DataTreeValue)?.Value?.ToString() is string capturedId
                && capturedId.Length > 0)
            {
                context.PassThrough[capturedId] = passData;
                context.PassThroughTypes[capturedId] = resolution.SimpleName;
            }

            if (passData != null && resolution.SimpleName == "SpriteProvider"
                && (passData.TryGetNode("ID") as DataTreeValue)?.Value?.ToString() is { Length: > 0 } spriteId
                && ReadId(passData, "Texture") is { } textureId)
                context.SpriteTextures[spriteId] = textureId;
            if (passData != null && resolution.SimpleName == "ContextMenuSubmenu")
                context.Submenus.Add((slot, passData));
            if (passData != null && resolution.SimpleName == "DynamicBlendShapeDriver")
                context.NamedShapeDrivers.Add(passData);
            if (passData != null && resolution.SimpleName is "EyeManager" or "EyeLinearDriver")
                context.EyeParts.Add((resolution.SimpleName, passData));
            if (passData != null && resolution.SimpleName == "PlatformColorPalette")
                WorldUi.CollectPalette(context.Graphs, passData);
            if (passData != null && resolution.SimpleName is "CharacterController" or "PhysicalLocomotion")
                context.Locomotion.Add((slot, resolution.SimpleName, passData));
            if (passData != null && resolution.SimpleName is "SnapPlane" or "MultiUserAvatarAnchor")
                context.Benches.Add((slot, resolution.SimpleName, passData));
            if (passData != null && WorldK7.Collects(resolution.SimpleName))
                context.TemplateLists.Add((slot, resolution.SimpleName, passData));

            if (resolution.Fate == PackageTypeFate.Stripped)
            {
                context.Result.ComponentsStripped++;
                stripped++;
                if (context.Graphs.Wanted && passData != null && slotId != null && index < context.TypeNames.Count
                    && PackageNodeGraphs.IsNodeType(context.TypeNames[index]))
                    context.Graphs.Nodes.Add(new PackageNodeRecord(slotId, context.TypeNames[index], passData));
                else if (context.Graphs.Wanted && passData != null)
                    context.Graphs.RecordUnattached(resolution.SimpleName, passData);
                continue;
            }

            var data = passData;

            if (resolution.Target == null || data == null)
            {
                context.Result.ComponentsSkipped++;
                if (context.Graphs.Wanted && data != null)
                    context.Graphs.RecordUnattached(resolution.SimpleName, data);
                continue;
            }

            if (TryAttach(slot, resolution, data, context))
                attached++;
            else
            {
                context.Result.ComponentsSkipped++;
                if (context.Graphs.Wanted)
                    context.Graphs.RecordUnattached(resolution.SimpleName, data);
            }
        }
        return attached;
    }

    // A package's component list is a sync list, so the actual entries sit under a nested member rather than
    // directly on the "Components" node. Find the first list of worker-shaped dictionaries.
    private static DataTreeList? FindWorkerList(DataTreeNode node)
    {
        switch (node)
        {
            case DataTreeList list:
                foreach (var child in list.Children)
                {
                    if (child is DataTreeDictionary d && d.ContainsKey("Type") && d.ContainsKey("Data"))
                        return list;
                }
                return null;

            case DataTreeDictionary dict:
                foreach (var child in dict.Children.Values)
                {
                    var found = FindWorkerList(child);
                    if (found != null)
                        return found;
                }
                return null;
        }
        return null;
    }

    // Only components we can genuinely carry across are attached. The set is deliberately narrow: these
    // are the ones that decide whether the object is VISIBLE and the right shape, which is what makes an
    // import worth having at all. Everything else is counted as skipped.
    private static bool TryAttach(Slot slot, PackageTypeMap.Resolution resolution,
        DataTreeDictionary data, BuildContext context)
    {
        var target = resolution.Target!;
        if (context.PlainColours.Count > 0 && (data.TryGetNode("ID") as DataTreeValue)?.Value is string plainId
            && context.PlainColours.TryGetValue(plainId, out var plain))
            target = plain;
        Component? component = null;

        // A second avatar root beside the first is never the one worn: each socket takes the first part that
        // fits. It keeps a scale of one, and wearing it put the avatar on at its raw model's size, up to two
        // hundred times too big. References to it go to the first. -xlinka
        if (target == typeof(AvatarForm) && slot.GetComponent<AvatarForm>() is { } firstForm)
        {
            if ((data.TryGetNode("ID") as DataTreeValue)?.Value?.ToString() is { Length: > 0 } secondId)
                context.ById[secondId] = firstForm;
            return false;
        }

        try
        {
            // Anything we have a counterpart for is attached; the member mapper then carries across
            // whatever lines up by name. Narrowing this to a hand-picked list of types was the earlier
            // approach and it left most of a package on the floor for no good reason - if we have the
            // component and its members match, there is nothing to be gained by refusing to build it.
            // A one-per-slot component that something on the slot already made for itself (a UI graphic makes
            // its rect when it attaches) IS the package's one. A second would be refused and destroyed, and the
            // package's values and every reference into it would go with it. -xlinka
            component = target.IsDefined(typeof(SingleInstancePerSlotAttribute), inherit: true)
                && slot.GetComponent(target) is { } existing && existing.GetType() == target
                ? existing
                : slot.AttachComponent(target);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Package import: could not attach {resolution.SimpleName}: {ex.Message}");
            return false;
        }

        if (component == null)
            return false;

        // The id is a bare string on the component, not a wrapped member.
        if ((data.TryGetNode("ID") as DataTreeValue)?.Value?.ToString() is string id && id.Length > 0)
            context.ById[id] = component;
        context.Graphs.RecordMembers(component, data);
        WorldUi.RecordUnsaved(context.Graphs, component, data);

        if (component is Components.ParticleSystem particles)
            context.ParticleSystems.Add((particles, data));
        if (component is Components.Assets.FontProvider chain && resolution.SimpleName == "FontChain")
            context.FontChains.Add((chain, data));
        if (component is Helio.UI.Canvas canvas)
            context.Canvases.Add((canvas, data));
        if (component is Light light)
            context.Lights.Add(light);
        if (component is LodGroup lods && resolution.SimpleName == "LODGroup")
            context.LodGroups.Add((lods, data));
        if (component is VisemeWeightDriver visemes)
            context.Visemes.Add((visemes, data));
        if (component is MouthExpressionDriver expressions)
            context.Expressions.Add((expressions, data));

        CopyMembers(component, data, context);
        WorldUi.RecordListEntries(context.ById, component, data);
        ApplyOldSaveDefaults(component, resolution.SimpleName, context);
        context.Result.ComponentsAttached++;
        return true;
    }

    // ---- assets ----

    private static async Task<Dictionary<string, Uri>> ConvertAssetsAsync(
        PackageArchive archive, DataTreeDictionary graph, PackageImportResult result,
        Dictionary<string, Dictionary<string, float4x4>> bindPoses,
        Dictionary<string, float4x4[]> bindOrder,
        Dictionary<string, BoundingBox> meshBounds,
        Dictionary<string, string[]> shapeNames)
    {
        await WorldContext.ToBackground();

        var map = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        var localDb = Engine.Current?.LocalDB;
        if (localDb == null)
            return map;

        // Only convert what the graph actually references. A package can carry assets nothing points at.
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.EnumerateTree())
        {
            if (node is DataTreeValue { IsUrl: true } value)
            {
                var signature = PackageArchive.SignatureOf(value.ExtractUrl());
                if (signature != null && archive.HasAsset(signature))
                    wanted.Add(signature);
            }
        }

        var unreadable = new Dictionary<string, int>(StringComparer.Ordinal);

        // Read in order, since the archive is one stream, and converted on several threads at once. One at a
        // time, a world of a thousand meshes spent seconds re-encoding them before a single slot was built.
        // The queue holds a few payloads ahead of the converters, so the bytes in flight stay small. -xlinka
        int converters = Engine.UseConstrainedResources ? 2 : System.Math.Clamp(Environment.ProcessorCount - 2, 2, 8);
        var queue = System.Threading.Channels.Channel.CreateBounded<(string Signature, byte[] Bytes)>(converters * 2);
        var workers = new Task[converters];
        for (int i = 0; i < converters; i++)
        {
            workers[i] = Task.Run(async () =>
            {
                await foreach (var (signature, bytes) in queue.Reader.ReadAllAsync().ConfigureAwait(false))
                    await ConvertAssetAsync(signature, bytes).ConfigureAwait(false);
            });
        }
        try
        {
            foreach (var signature in wanted)
            {
                byte[] bytes;
                try
                {
                    lock (archive)
                        bytes = archive.ReadAsset(signature);
                }
                catch (Exception ex)
                {
                    lock (map)
                        result.AssetsFailed++;
                    Logger.Warn($"Package import: asset {signature[..System.Math.Min(12, signature.Length)]} failed: {ex.Message}");
                    continue;
                }
                await queue.Writer.WriteAsync((signature, bytes)).ConfigureAwait(false);
            }
        }
        finally
        {
            queue.Writer.Complete();
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        await localDb.SaveRecordsAsync().ConfigureAwait(false);

        foreach (var (what, count) in unreadable)
        {
            result.AssetsFailed += count;
            Logger.Warn($"Package import: {count} asset(s) are {what}; whatever uses them comes in without them");
        }
        return map;

        async Task ConvertAssetAsync(string signature, byte[] bytes)
        {
            try
            {
                var kind = SniffKind(bytes, archive.MetadataKind(signature));
                // Baked cubemaps and procedural textures are in the package's own raw format; they come in
                // as pictures our image path reads.
                if (kind == null && PackageRawBitmap.IsRawBitmap(bytes))
                {
                    if (PackageRawBitmap.TryConvert(bytes, out var converted, out var extension, out var problem))
                    {
                        bytes = converted;
                        kind = extension;
                    }
                    else
                    {
                        var what = "raw bitmaps that do not convert (" + problem + ")";
                        lock (map)
                            unreadable[what] = unreadable.GetValueOrDefault(what) + 1;
                    }
                }
                switch (kind)
                {
                    case ".lmesh":
                    {
                        // Re-encode into our own mesh format so it travels through the normal asset
                        // pipeline - cached, peer-transferred, decoded by the standard path.
                        var mesh = PackageMeshReader.Read(bytes);
                        var encoded = PhosMeshSerializer.Serialize(mesh);
                        var url = new Uri(await localDb.SaveAssetAsync(encoded, ".lmesh", writeIndex: false).ConfigureAwait(false));
                        var bounds = mesh.CalculateBoundingBox();

                        float4x4[]? order = null;
                        Dictionary<string, float4x4>? binds = null;
                        if (mesh.BoneCount > 0)
                        {
                            // BY INDEX FIRST, by name only as a fallback. The renderer's Bones list is
                            // parallel to the mesh's own bone array - that is what makes skinning work
                            // at all - so position is the authoritative pairing and a name is only a
                            // hint. Names are what the author typed on a SLOT and get decorated: the
                            // radio's bones are "Bone (Col)" in the tree and plain "Bone" in the mesh,
                            // so every one of them missed, fell back to its slot transform while its
                            // siblings used bind-derived rests, and the model tore itself apart across
                            // four hundred metres. -xlinka
                            order = new float4x4[mesh.BoneCount];
                            binds = new Dictionary<string, float4x4>(StringComparer.Ordinal);
                            for (int i = 0; i < mesh.BoneCount; i++)
                            {
                                order[i] = mesh.GetBoneBindPose(i);
                                binds[mesh.GetBoneName(i)] = order[i];
                            }
                        }
                        string[]? shapes = null;
                        if (mesh.BlendShapeCount > 0)
                        {
                            shapes = new string[mesh.BlendShapeCount];
                            for (int i = 0; i < shapes.Length; i++)
                                shapes[i] = mesh.BlendShapes[i].Name;
                        }
                        lock (map)
                        {
                            map[signature] = url;
                            meshBounds[url.OriginalString] = bounds;
                            if (shapes != null)
                                shapeNames[url.OriginalString] = shapes;
                            if (order != null)
                            {
                                bindPoses[url.OriginalString] = binds!;
                                bindOrder[url.OriginalString] = order;
                            }
                            result.MeshesConverted++;
                        }
                        break;
                    }
                    case null:
                        break;
                    default:
                    {
                        // Textures and audio are the creator's original files and go in untouched.
                        var url = new Uri(await localDb.SaveAssetAsync(bytes, kind, writeIndex: false).ConfigureAwait(false));
                        bool picture = kind is ".png" or ".jpg" or ".webp" or ".gif" or ".bmp" or ".exr" or ".tga" or ".dds" or ".psd" or ".tif" or ".hdr";
                        bool chained = picture && await PackageTextureChains.TryFileAsync(archive, signature, bytes, url.OriginalString, localDb)
                            .ConfigureAwait(false);
                        lock (map)
                        {
                            map[signature] = url;
                            if (picture)
                                result.TexturesImported++;
                            if (chained)
                                result.TexturesPrecompressed++;
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                lock (map)
                    result.AssetsFailed++;
                Logger.Warn($"Package import: asset {signature[..System.Math.Min(12, signature.Length)]} failed: {ex.Message}");
            }
        }
    }

    // The metadata kind says a payload is a picture when its bytes do not: TGA has no signature at the front.
    private static string? SniffKind(byte[] b, string? metadataKind)
    {
        if (SniffKind(b) is { } kind)
            return kind;
        if (b.Length >= 4 && ((b[0] == 0 && b[1] == 1 && b[2] == 0 && b[3] == 0) || (b[0] == 't' && b[1] == 'r' && b[2] == 'u' && b[3] == 'e')
            || (b[0] == 't' && b[1] == 't' && b[2] == 'c' && b[3] == 'f')))
            return ".ttf";
        if (b.Length >= 4 && b[0] == 'O' && b[1] == 'T' && b[2] == 'T' && b[3] == 'O')
            return ".otf";
        if (b.Length >= 4 && b[0] == 'w' && b[1] == 'O' && b[2] == 'F' && (b[3] == 'F' || b[3] == '2'))
            return b[3] == 'F' ? ".woff" : ".woff2";
        if (b.Length >= 4 && b[0] == 'D' && b[1] == 'D' && b[2] == 'S' && b[3] == ' ')
            return ".dds";
        if (b.Length >= 4 && b[0] == '8' && b[1] == 'B' && b[2] == 'P' && b[3] == 'S')
            return ".psd";
        if (TiffDecoder.IsTiff(b))
            return ".tif";
        if (metadataKind == "bitmap" && b.Length > 18 && b[1] <= 1 && (b[2] is 1 or 2 or 3 or 9 or 10 or 11)
            && (b[16] is 8 or 15 or 16 or 24 or 32) && (b[12] | b[13] << 8) > 0 && (b[14] | b[15] << 8) > 0)
            return ".tga";
        return null;
    }

    private static string? SniffKind(byte[] b)
    {
        if (b.Length >= 7 && b[0] == 5 && b[1] == 'M' && b[2] == 'e' && b[3] == 's' && b[4] == 'h' && b[5] == 'X')
            return ".lmesh";
        if (b.Length >= 4 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G')
            return ".png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            return ".jpg";
        // RIFF is a CONTAINER, not a format: audio and WebP images both start with it, and the four
        // bytes at offset 8 say which. Naming a WebP ".wav" wrote perfectly good textures to disk under
        // an extension the image decoder cannot read, so it failed and retried on every one. -xlinka
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F')
            return b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P' ? ".webp" : ".wav";
        if (b.Length >= 4 && b[0] == 'O' && b[1] == 'g' && b[2] == 'g' && b[3] == 'S')
            return ".ogg";
        if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8')
            return ".gif";
        if (b.Length >= 2 && b[0] == 'B' && b[1] == 'M')
            return ".bmp";
        if (b.Length >= 4 && b[0] == 0x76 && b[1] == 0x2F && b[2] == 0x31 && b[3] == 0x01)
            return ".exr";
        return null;
    }

    // ---- graph helpers ----

    private static List<string> ReadTypeNames(DataTreeDictionary graph)
    {
        var names = new List<string>();
        if (graph.TryGetNode("Types") is DataTreeList types)
        {
            foreach (var node in types)
                names.Add((node as DataTreeValue)?.Value?.ToString() ?? string.Empty);
        }
        return names;
    }

    private static List<PackageTypeMap.Resolution> ReadTypeTable(DataTreeDictionary graph)
    {
        var list = new List<PackageTypeMap.Resolution>();
        if (graph.TryGetNode("Types") is not DataTreeList types)
            return list;
        foreach (var node in types)
            list.Add(PackageTypeMap.Resolve((node as DataTreeValue)?.Value?.ToString() ?? string.Empty));
        return list;
    }

    // Every member is stored as { ID, Data } - the value lives under "Data", never at the top.
    private static object? ReadValue(DataTreeDictionary node, string member)
    {
        if (node.TryGetNode(member) is not DataTreeDictionary holder)
            return (node.TryGetNode(member) as DataTreeValue)?.Value;
        return (holder.TryGetNode("Data") as DataTreeValue)?.Value;
    }

    // Transforms are stored as ARRAYS of numbers, not as text. Parsing them as strings silently
    // yielded nothing for every slot, so nothing was ever positioned, rotated or scaled and the whole
    // object collapsed onto the origin at its raw model scale. -xlinka
    private static float3? ReadFloat3(DataTreeDictionary node, string member)
    {
        var parts = PackageMemberMapper.ReadNumberArray(node, member);
        return parts is { Length: >= 3 } ? new float3(parts[0], parts[1], parts[2]) : null;
    }

    private static floatQ? ReadFloatQ(DataTreeDictionary node, string member)
    {
        var parts = PackageMemberMapper.ReadNumberArray(node, member);
        return parts is { Length: >= 4 } ? new floatQ(parts[0], parts[1], parts[2], parts[3]) : null;
    }

    private static int SafeInt(DataTreeValue value)
    {
        try { return Convert.ToInt32(value.Value); }
        catch { return -1; }
    }

    private static Uri? TryUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private static string CleanName(string? name)
    {
        var cleaned = StripMarkup(name);
        return cleaned.Length > 0 ? cleaned : "Imported Package";
    }

    // Names routinely carry rich-text colour tags. Left in, every slot in the hierarchy reads as markup.
    // Rich-text tags a name may be decorated with. Deliberately a CLOSED LIST.
    //
    // Angle brackets in a slot name are not automatically markup. Rig authors use them as plain naming
    // convention - "<NOIK> Hips" marks a bone the IK must leave alone - and stripping every bracketed run
    // renamed it to "Hips", which then collided with the real Hips: the skeleton refused the duplicate,
    // every bind bone that named the original failed to match and collapsed to bone 0, and the mesh
    // deformed into a knot. Strip what is genuinely formatting; leave everything else exactly as authored.
    // -xlinka
    private static readonly HashSet<string> RichTextTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "b", "i", "u", "s", "sub", "sup", "br", "nobr", "noparse",
        "color", "size", "alpha", "mark", "align", "font", "gradient", "style",
        "cspace", "indent", "line-height", "lowercase", "uppercase", "smallcaps",
        "margin", "mspace", "pos", "rotate", "space", "sprite", "voffset", "width", "link",
    };

    private static string StripMarkup(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var sb = new System.Text.StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] != '<')
            {
                sb.Append(name[i]);
                continue;
            }

            int close = name.IndexOf('>', i + 1);
            if (close < 0)
            {
                // Unterminated: it is text, not a tag.
                sb.Append(name[i]);
                continue;
            }

            if (IsRichTextTag(name.AsSpan(i + 1, close - i - 1)))
            {
                i = close;      // drop the whole tag
                continue;
            }

            sb.Append(name[i]);
        }
        return sb.ToString().Trim();
    }

    // The body between the angle brackets, without them. A tag is "</name>", "<name>" or "<name=value>".
    private static bool IsRichTextTag(ReadOnlySpan<char> body)
    {
        if (body.IsEmpty)
            return false;
        if (body[0] == '/')
            body = body[1..];

        int stop = body.IndexOfAny('=', ' ');
        if (stop >= 0)
            body = body[..stop];

        return !body.IsEmpty && RichTextTags.Contains(body.ToString());
    }
}
