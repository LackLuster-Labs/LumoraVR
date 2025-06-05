using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;

namespace LumoraVR.Source.Networking;

[GlobalClass]
public partial class AssetFetcher : Node
{
    private static readonly Dictionary<string, (Task<byte[]> task, List<Action<byte[]>> callback)> ActiveFetchTasks = new();

    public static void FetchAsset(string path, Action<byte[]> callback)
    {
        if (ActiveFetchTasks.TryGetValue(path, out var data))
        {
            data.callback.Add(callback);
            return;
        }
        (Task<byte[]> task, List<Action<byte[]>> callback) task = (Task.Run(() => StartFetchAsset(path)), [callback]);
        ActiveFetchTasks[path] = task;
    }
    private static byte[] StartFetchAsset(string path)
    {
        //var uri = new Uri(path);
        //var scheme = uri.Scheme;
        if (BuiltinAssetHelper.ValidPath(path)) return BuiltinAssetHelper.GetBuiltinAssetData(path);
        if (LocalTestAssetHelper.ValidPath(path)) return LocalTestAssetHelper.GetLocalTestAssetData(path);
        return null;
    }
    public override void _Process(double delta)
    {
        base._Process(delta);

        var toRemove = new List<string>();

        var tasks = new Dictionary<string, (Task<byte[]> task, List<Action<byte[]>> callback)>(ActiveFetchTasks);
        foreach (var pair in tasks)
        {
            var task = pair.Value.task;
            var callback = pair.Value.callback;
            if (task.IsCompleted)
            {
                foreach (var c in callback) c(task.Result);
                toRemove.Add(pair.Key);
            }
        }

        foreach (var remove in toRemove) ActiveFetchTasks.Remove(remove);
    }
}
