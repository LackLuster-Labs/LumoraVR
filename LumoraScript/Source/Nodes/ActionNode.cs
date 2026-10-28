// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Changes the world. Ports come from the parameters after a leading context, and the In, Next and Failed
// flow ports are added without being declared. The runtime runs the body inside the script principal's
// scope, so a write is judged by the gate exactly like the author's own would be.
//
// A call that could not do what it was asked says why with Fail, and a refusal says so on its own. With
// failed wired, such a call leaves by failed instead of next; with nothing there it carries on by next. An
// input bound to something since destroyed fails by its port's name before the body runs. A body that throws
// ArgumentException for an input it cannot use, or InvalidOperationException for a thing it could not do,
// leaves by failed with that message when failed is wired, and stops the call as a fault when it is not.
public abstract class ActionNode : ScriptNode
{
    public const string NextExit = "next";
    public const string FailedExit = "failed";
}
