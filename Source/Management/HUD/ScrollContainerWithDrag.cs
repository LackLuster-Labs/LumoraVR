using Godot;
using System;
using System.Collections.Generic;

namespace LumoraVR.Source.Management.HUD;

/// <summary>
/// Implements the touch and drag feature for a ScrollContainer.
/// </summary>
public partial class ScrollContainerWithDrag : ScrollContainer
{
    private bool hovering = false;
    private bool swiping = false;
    private Vector2 swipeStart;
    private Vector2 swipeMouseStart;
    private List<ulong> swipeMouseTimes = [];
    private List<Vector2> swipeMousePositions = [];

    public override void _Ready()
    {
        base._Ready();
        MouseEntered += OnMouseEntered;
        MouseExited += OnMouseExited;
    }

    private void OnMouseEntered()
    {
        hovering = true;
    }
    private void OnMouseExited()
    {
        hovering = false;
    }

    public override void _Input(InputEvent @event)
    {
        base._Input(@event);

        if (@event is InputEventMouseButton mouseEvent)
        {
            if (mouseEvent.Pressed)
            {
                if (!hovering) return;
                swiping = true;
                swipeStart = new Vector2(GetHScroll(), GetVScroll());
                swipeMouseStart = mouseEvent.Position;
                swipeMouseTimes = [Time.GetTicksMsec()];
                swipeMousePositions = [swipeMouseStart];
            }
            else
            {
                swipeMouseTimes.Add(Time.GetTicksMsec());
                swipeMousePositions.Add(mouseEvent.Position);
                Vector2 source = new(GetHScroll(), GetVScroll());
                int idx = swipeMouseTimes.Count - 1;
                ulong now = Time.GetTicksMsec();
                ulong cutoff = now - 100;
                for (int i = swipeMouseTimes.Count - 1; i >= 0; i--)
                {
                    if (swipeMouseTimes[i] >= cutoff) idx = i;
                    else break;
                }
                Vector2 flickStart = swipeMousePositions[idx];
                float flickDur = Math.Min(0.3f, (mouseEvent.Position - flickStart).Length() / 1000f);
                if (flickDur > 0.0f)
                {
                    var tween = CreateTween();
                    Vector2 delta = mouseEvent.Position - flickStart;
                    Vector2 target = source - delta * flickDur * 15.0f;
                    tween.TweenMethod(Callable.From<float>(SetHScrollFloat), source.X, target.X, flickDur).SetTrans(Tween.TransitionType.Linear).SetEase(Tween.EaseType.Out);
                    tween.TweenMethod(Callable.From<float>(SetVScrollFloat), source.Y, target.Y, flickDur).SetTrans(Tween.TransitionType.Linear).SetEase(Tween.EaseType.Out);
                }
                swiping = false;
            }
        }
        else if (swiping && @event is InputEventMouseMotion motionEvent)
        {
            Vector2 delta = motionEvent.Position - swipeMouseStart;
            SetHScrollFloat(swipeStart.X - delta.X);
            SetVScrollFloat(swipeStart.Y - delta.Y);
            swipeMouseTimes.Add(Time.GetTicksMsec());
            swipeMousePositions.Add(motionEvent.Position);
        }
    }

    void SetHScrollFloat(float value) => SetHScroll(Mathf.RoundToInt(value));
    void SetVScrollFloat(float value) => SetVScroll(Mathf.RoundToInt(value));
}
