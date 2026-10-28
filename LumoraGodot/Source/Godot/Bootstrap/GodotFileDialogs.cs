// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Godot;
using Lumora.Core.Input;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Source.Godot.Bootstrap;

// The OS open and save dialogs off DisplayServer. Desktop only: on a phone the picker hands back a content URI
// that plain file reads cannot open, so there the callers keep to a typed path. -xlinka
public sealed class GodotFileDialogs : IFileDialogs
{
	public static bool Available
	{
		get
		{
			if (OS.HasFeature("android") || OS.HasFeature("ios"))
				return false;
			try
			{
				return DisplayServer.HasFeature(DisplayServer.Feature.NativeDialogFile);
			}
			catch (Exception)
			{
				return false;
			}
		}
	}

	public bool PickSave(string title, string folder, string fileName, string filter, Action<string?> done)
		=> Show(title, folder, fileName, filter, DisplayServer.FileDialogMode.SaveFile, done);

	public bool PickOpen(string title, string folder, string filter, Action<string?> done)
		=> Show(title, folder, string.Empty, filter, DisplayServer.FileDialogMode.OpenFile, done);

	private static bool Show(string title, string folder, string fileName, string filter,
		DisplayServer.FileDialogMode mode, Action<string?> done)
	{
		if (!Available || done == null)
			return false;
		try
		{
			var callback = Callable.From((bool status, string[] paths, int filterIndex) =>
				done(status && paths != null && paths.Length > 0 && !string.IsNullOrEmpty(paths[0]) ? paths[0] : null));
			var error = DisplayServer.FileDialogShow(title ?? string.Empty, folder ?? string.Empty, fileName ?? string.Empty,
				false, mode, string.IsNullOrEmpty(filter) ? Array.Empty<string>() : new[] { filter }, callback);
			return error == Error.Ok;
		}
		catch (Exception ex)
		{
			LumoraLogger.Warn($"File dialog: could not open: {ex.Message}");
			return false;
		}
	}
}
