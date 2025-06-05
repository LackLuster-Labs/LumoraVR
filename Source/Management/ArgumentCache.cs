using System;
using System.Collections.Generic;
#if DEBUG
using System.Diagnostics;
#endif
using System.Linq;
using System.Text;
using LumoraVR.Source.Logging;
using Godot;

namespace LumoraVR.Source.Management
{
    public partial class ArgumentCache : Node
    {
        public static ArgumentCache Instance { get; private set; }
        private readonly Dictionary<string, string> _argCache = new();
        private readonly HashSet<String> _flagCache = new();
        public readonly IReadOnlyDictionary<string, string> Arguments;

        ArgumentCache()
        {
            Arguments = _argCache.AsReadOnly();
            if (Instance is not null) throw new("wtf"); //just realised this why - Linka 
            Instance = this;
        }

        public bool IsFlagActive(string name)
        {
            return _flagCache.Contains(name);
        }

        public override void _Ready()
        {
            base._Ready();
            string[] argsstrings = OS.GetCmdlineArgs();
            System.Collections.IEnumerator args = argsstrings.GetEnumerator();
            string key = null;
            StringBuilder value = new();

            void Add()
            {
                if (!string.IsNullOrEmpty(key))
                {
                    String valstring = value.ToString();
                    int i;
                    for (i = 0; i < key.Length; i++)
                    {
                        if (key[i] != '-') break;
                    }

                    value.Clear();
                    if (!String.IsNullOrEmpty(valstring))
                    {
                        _argCache.Add(key.Substring(i), valstring);
                    }
                    else
                    {
                        _flagCache.Add(key.Substring(i));
                    }

                    key = null;
                }
                else
                {
                    Logger.Warn("Argument key is null");
                    value.Clear();
                }
            }

            while (args.MoveNext())
            {
                string arg = args.Current?.ToString();
                if (arg.StartsWith("-"))
                {
                    Add();

                    if (arg.Contains("="))
                    {
                        string[] split = arg.Split("=");
                        key = split[0];
                        if (split.Length > 1)
                            value.Append(split[1]);
                    }
                    else
                        key = arg;

                    continue;
                }

                value.Append(arg);
            }
            Add();
#if DEBUG
            foreach (var pair in _argCache)
            {
                Logger.Log($"[ARGPARSE] {pair.Key} = {pair.Value}");
            }
            foreach (string flag in _flagCache)
            {
                Logger.Log($"[ARGPARSE] Flag: {flag}");
            }
            if (IsFlagActive("vs-debug"))
            {
                Debugger.Launch();
            }
#endif
        }
    }
}