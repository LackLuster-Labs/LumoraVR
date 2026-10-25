shader_type spatial;
// Inverted-hull outline for the Xiexe toon material in its Emissive state: unshaded, the flat outline colour.
// The body is XiexeOutline_Core; a Lit outline comes as generated source over it (XiexeToonMaterial).
//
// Opaque and depth-writing on purpose - the shell's inward-facing fragments sit behind the model, so
// the depth the main pass already laid down rejects them and only the border is left.
render_mode unshaded, cull_front;

#include "res://Shaders/XiexeOutline_Core.gdshaderinc"
