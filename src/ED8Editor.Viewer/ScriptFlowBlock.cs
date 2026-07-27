using System.Drawing;

namespace ED8Editor.Viewer;

/// <summary>
/// What the flow canvas needs to draw one instruction: its title bar, the line
/// summarising its operands and the colour that classifies it. The canvas draws
/// these; it does not host a control per instruction, so opening or editing a
/// scene costs a repaint instead of thousands of window creations.
/// </summary>
internal sealed record ScriptFlowBlock(
    int Instruction,
    string Header,
    string Summary,
    Color HeaderColor);
