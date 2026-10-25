using System.Runtime.InteropServices;

namespace TopNotch
{
    internal static class Fullscreen
    {
        private enum State
        {
            Busy = 2,
            RunningD3DFullscreen = 3,
            PresentationMode = 4
        }

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out State state);

        internal static bool Active
        {
            get
            {
                try
                {
                    if (SHQueryUserNotificationState(out State state) != 0) return false;

                    return state is State.Busy or State.RunningD3DFullscreen or State.PresentationMode;
                }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
                {
                    return false;
                }
            }
        }
    }
}
