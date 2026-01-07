using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Lumora.Core;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Provides a stable mapping between component types and serialized identifiers.
/// Uses assembly-qualified names for deterministic round-tripping.
/// </summary>
public static class ComponentTypeRegistry
{
	private static readonly object _lock = new();
	private static readonly Dictionary<string, Type> _nameToType = new(StringComparer.Ordinal);
	private static readonly Dictionary<Type, string> _typeToName = new();
	private static bool _initialized;

	static ComponentTypeRegistry()
	{
		Initialize();
		AppDomain.CurrentDomain.AssemblyLoad += (_, args) =>
		{
			try
			{
				RegisterAssembly(args.LoadedAssembly);
			}
			catch (Exception ex)
			{
				AquaLogger.Error($"ComponentTypeRegistry: Failed to scan assembly {args.LoadedAssembly.FullName}: {ex.Message}");
			}
		};
	}

	private static void Initialize()
	{
		lock (_lock)
		{
			if (_initialized)
				return;

			foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				RegisterAssembly(assembly);
			}

			_initialized = true;
		}
	}

	private static void RegisterAssembly(Assembly assembly)
	{
		Type[] types;
		try
		{
			types = assembly.GetTypes();
		}
		catch (ReflectionTypeLoadException ex)
		{
			types = ex.Types.Where(t => t != null).ToArray();
		}

		foreach (var type in types)
		{
			if (type == null || type.IsAbstract)
				continue;

			if (typeof(Component).IsAssignableFrom(type))
			{
				RegisterType(type);
			}
		}
	}

	public static void RegisterType(Type type)
	{
		if (type == null || !typeof(Component).IsAssignableFrom(type))
			return;

		lock (_lock)
		{
			if (_typeToName.ContainsKey(type))
				return;

			var id = type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
			if (string.IsNullOrEmpty(id))
				return;

			_typeToName[type] = id;
			_nameToType[id] = type;
		}
	}

	public static string GetTypeId(Type type)
	{
		if (type == null)
			throw new ArgumentNullException(nameof(type));

		lock (_lock)
		{
			if (_typeToName.TryGetValue(type, out var id))
				return id;
		}

		RegisterType(type);
		lock (_lock)
		{
			return _typeToName[type];
		}
	}

	public static void Encode(BinaryWriter writer, Type type)
	{
		if (writer == null)
			throw new ArgumentNullException(nameof(writer));

		writer.Write(GetTypeId(type));
	}

	public static Type Decode(BinaryReader reader)
	{
		if (reader == null)
			throw new ArgumentNullException(nameof(reader));

		var id = reader.ReadString();
		return ResolveType(id);
	}

	public static Type ResolveType(string id)
	{
		if (string.IsNullOrEmpty(id))
			return null;

		lock (_lock)
		{
			if (_nameToType.TryGetValue(id, out var cached))
				return cached;
		}

		var type = Type.GetType(id, throwOnError: false);
		if (type == null)
		{
			foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				type = assembly.GetType(id, throwOnError: false);
				if (type != null)
					break;
			}
		}

		if (type != null && typeof(Component).IsAssignableFrom(type))
		{
			RegisterType(type);
			return type;
		}

		AquaLogger.Error($"ComponentTypeRegistry: Failed to resolve component type '{id}'");
		return null;
	}
}
