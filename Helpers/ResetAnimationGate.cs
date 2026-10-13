using CodexBarWin.Models;

namespace CodexBarWin.Helpers;

public static class ResetAnimationGate
{
    public static bool ShouldAnimate(ResetAnimationMode mode, bool batterySaverActive) => mode switch
    {
        ResetAnimationMode.Always => true,
        ResetAnimationMode.Never => false,
        _ => !batterySaverActive
    };
}
