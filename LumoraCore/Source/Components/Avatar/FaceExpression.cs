// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Components.Avatar;

// What a named face shape follows when face tracking drives it: one mouth channel, an average of a pair, or a
// reading of tracked channels together (a smile with the upper lip down is a closed smile, with it up a grin).
// The Sideways and Vertical ones sit at 0.5 at rest and run 0 to 1 across the two ways. Every one of them is made
// of values the tracker measures; nothing here stands in for a muscle it does not see. Saved by ordinal: append
// only. -xlinka
public enum FaceExpression
{
    SmileLeft,
    SmileRight,
    Smile,
    SmileClosedLeft,
    SmileClosedRight,
    SmileClosed,
    GrinLeft,
    GrinRight,
    Grin,
    SmirkLeft,
    SmirkRight,
    FrownLeft,
    FrownRight,
    Frown,
    JawOpen,
    JawLeft,
    JawRight,
    JawSideways,
    JawForward,
    Pout,
    CheekPuffLeft,
    CheekPuffRight,
    CheekPuff,
    CheekSuck,
    UpperLipRaiseLeft,
    UpperLipRaiseRight,
    UpperLipRaise,
    LowerLipDownLeft,
    LowerLipDownRight,
    LowerLipDown,
    UpperLipLeft,
    UpperLipRight,
    UpperLipSideways,
    LowerLipLeft,
    LowerLipRight,
    LowerLipSideways,
    UpperLipOverturn,
    LowerLipOverturn,
    UpperLipTuck,
    LowerLipTuck,
    LowerLipOverlay,
    TongueOut,
    TongueRaise,
    TongueExtend,
    TongueUp,
    TongueDown,
    TongueLeft,
    TongueRight,
    TongueVertical,
    TongueSideways,
    TongueRoll,
    TongueUpLeft,
    TongueUpRight,
    TongueDownLeft,
    TongueDownRight,
}
