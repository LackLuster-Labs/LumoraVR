// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.Components.UI;
using Lumora.Godot.Hooks;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Source.UI;

public partial class DashboardToggle : Node
{
	private static DashboardToggle? _instance;
	private UserspaceDashboard _dashboard = null!;

	public static DashboardToggle? Instance => _instance;
	public static bool IsDashboardVisible { get; private set; }

	// Escape does two jobs: back out of whatever dash screen is up, and close the dash. The first
	// runs off the raw key event, the second off the rebindable action, and the action's edge can
	// land a frame later than the event depending on where this node sits in the tree - so a
	// consumed back-out latches here and eats the next toggle edge instead of racing it. The timeout
	// covers the case where the toggle was rebound off Escape entirely and no edge ever arrives.
	// -xlinka
	private bool _escapeConsumedByScreen;
	private double _escapeConsumedAge;
	private const double EscapeConsumeWindow = 0.35;

	public override void _Ready()
	{
		base._Ready();
		_instance = this;
		IsDashboardVisible = false;
		AddChild(new SettingsApplier { Name = "SettingsApplier" });
	}

	public override void _Input(InputEvent @event)
	{
		if (@event is InputEventMouseButton mouseBtn && mouseBtn.Pressed)
		{
			if (TryGetDashboard(out var dashboard) && dashboard.IsOpen.Value)
			{
				float delta = 0f;
				if (mouseBtn.ButtonIndex == MouseButton.WheelUp) delta = 1f;
				else if (mouseBtn.ButtonIndex == MouseButton.WheelDown) delta = -1f;
				if (delta != 0f)
				{
					// A carried window slides on the wheel, so it must reach the mouse driver unhandled.
					if (Lumora.Core.Components.UserInputState.FocusedScrollWheelCaptured)
						return;
					if (dashboard.FeedAxis(new Lumora.Core.Math.float2(0f, delta)))
						GetViewport()?.SetInputAsHandled();
					return;
				}
			}
		}

		if (@event is not InputEventKey key || !key.Pressed)
			return;

		// Escape stops a middle-button scroll and does nothing else: no screen backs out, the dash stays as it is.
		// Left unhandled so the keyboard still sees the key that ends the scroll.
		if (key.Keycode == Key.Escape && Lumora.Core.Components.Interaction.DesktopAutoscroll.IsActive)
		{
			_escapeConsumedByScreen = true;
			_escapeConsumedAge = 0.0;
			return;
		}

		// Helio fields consume the keyboard driver themselves. Feeding the dashboard as well
		// would edit its search box behind the focused field or swallow that field's typed text.
		if (Helio.UI.TextInput.Focused is { IsDestroyed: false })
			return;

		if (key.Keycode == Key.Escape)
		{
			// Escape inside an open dash backs out of whatever screen is up. Only that part is
			// handled here; the open/close toggle itself is an action, polled in _Process, so it can
			// be rebound and so a pad's Start button reaches it too.
			if (TryGetDashboard(out var escDash) && (escDash.IsOpen.Value || escDash.PanelTakesKeys) && escDash.FeedEscape())
			{
				_escapeConsumedByScreen = true;
				_escapeConsumedAge = 0.0;
				GetViewport()?.SetInputAsHandled();
				return;
			}
			return;
		}

		// Everything below is the dash search box TYPING, not controls: characters, backspace and
		// enter belong to whatever field has focus, the same way a text field owns the keyboard
		// while it is focused. Nothing here is rebindable and nothing here should be. An app out in
		// the room with a field waiting takes them with the dash closed too.
		if (TryGetDashboard(out var dash) && (dash.IsOpen.Value || dash.PanelTakesKeys))
		{
			// Ctrl+V and Ctrl+C are the keyboard's Paste and Copy keys here. A field typed into directly reads
			// them itself. Ctrl with Alt is AltGr on many layouts and still types its character. -xlinka
			bool shortcut = (key.CtrlPressed || key.MetaPressed) && !key.AltPressed && key.Keycode >= Key.A && key.Keycode <= Key.Z;
			if (shortcut)
			{
				if (!key.Echo && (key.Keycode == Key.V || key.Keycode == Key.C))
				{
					dash.FeedKey(key.Keycode == Key.V ? DashboardKey.Paste : DashboardKey.Copy);
					GetViewport()?.SetInputAsHandled();
				}
				return;
			}
			if (key.Keycode == Key.Backspace)
			{
				dash.FeedSearchBackspace();
				GetViewport()?.SetInputAsHandled();
				return;
			}
			if (key.Keycode == Key.Enter || key.Keycode == Key.KpEnter)
			{
				if (dash.FeedEnter())
					GetViewport()?.SetInputAsHandled();
				return;
			}
			long ch = key.Unicode;
			if (ch >= 32 && ch != 127 && System.Text.Rune.IsValid((int)ch))
			{
				foreach (char c in char.ConvertFromUtf32((int)ch))
					dash.FeedSearchChar(c);
				GetViewport()?.SetInputAsHandled();
			}
		}
	}

	public override void _Process(double delta)
	{
		// The dash toggle is the one action that must survive its own set being blocked: the menu set
		// asserts itself while the dash is open, and it sits at the top of the priority order, so it
		// keeps evaluating and the same control closes what it opened.
		if (_escapeConsumedByScreen)
		{
			_escapeConsumedAge += delta;
			if (_escapeConsumedAge > EscapeConsumeWindow)
				_escapeConsumedByScreen = false;
		}

		if (Lumora.Core.Engine.Current?.InputInterface?.Actions?.Menu.ToggleDashboard.Pressed == true)
		{
			if (_escapeConsumedByScreen)
				_escapeConsumedByScreen = false;
			else
				ToggleDashboard();
		}

		IsDashboardVisible = TryGetDashboard(out var dashboard) && dashboard.IsOpen.Value;
		HandlePad(IsDashboardVisible || dashboard?.PanelTakesKeys == true ? dashboard : null, delta);
	}

	// The pad's way back and its scroll. Back closes a focused text field first (the chat box, a panel's
	// field), then backs a dash screen out the way Escape does, then closes the dash. The right stick scrolls
	// whatever the dash cursor is over, faster the further it is pushed. The radial menu's back is the hand
	// tool's, which owns that menu. -xlinka
	private const float PadScrollNotchesPerSecond = 10f;

	private void HandlePad(UserspaceDashboard? dash, double delta)
	{
		var menu = Lumora.Core.Engine.Current?.InputInterface?.Actions?.Menu;
		if (menu == null)
			return;

		if (menu.Back.Pressed)
		{
			if (Helio.UI.TextInput.Focused is { IsDestroyed: false } field)
				field.Unfocus();
			else if (dash != null && !dash.FeedEscape() && dash.IsOpen.Value)
				HideDashboard();
		}

		if (dash == null || !dash.IsOpen.Value)
			return;
		var scroll = menu.Scroll.Value;
		if (scroll.x == 0f && scroll.y == 0f)
			return;
		float rate = PadScrollNotchesPerSecond * (float)delta * scroll.Length;
		if (Lumora.Core.Components.UserInputState.FocusedScrollWheelCaptured)
			return;
		dash.FeedAxis(new Lumora.Core.Math.float2(scroll.x * rate, scroll.y * rate));
	}

	public void ToggleDashboard()
	{
		if (!TryGetDashboard(out var dashboard))
		{
			LumoraLogger.Warn("DashboardToggle: UserspaceDashboard not found.");
			IsDashboardVisible = false;
			return;
		}

		if (dashboard.IsOpen.Value)
			HideDashboard();
		else
			ShowDashboard();
	}

	public void ShowDashboard()
	{
		if (!TryGetDashboard(out var dashboard))
		{
			LumoraLogger.Warn("DashboardToggle: Cannot show dashboard, UserspaceDashboard not found.");
			IsDashboardVisible = false;
			return;
		}

		dashboard.Open();
		IsDashboardVisible = true;
		// Mouse stays captured: it steers the hand laser over the world-space
		// dash surface, not an OS cursor over a flat blit.
	}

	public void HideDashboard()
	{
		if (TryGetDashboard(out var dashboard))
		{
			dashboard.Close();
		}

		IsDashboardVisible = false;
		// No capture here: the mouse driver takes the mouse back next frame, and only for a focused window.
	}

	private bool TryGetDashboard(out UserspaceDashboard dashboard)
	{
		if (_dashboard != null && !_dashboard.IsDestroyed)
		{
			dashboard = _dashboard;
			return true;
		}

		_dashboard = Lumora.Core.Engine.Current?.WorldManager?.UserspaceWorld?.RootSlot?.GetComponentInChildren<UserspaceDashboard>(true)!;
		dashboard = _dashboard!;
		return dashboard != null && !dashboard.IsDestroyed;
	}

	public override void _ExitTree()
	{
		if (_instance == this)
		{
			_instance = null;
		}

		IsDashboardVisible = false;
		base._ExitTree();
	}
}
