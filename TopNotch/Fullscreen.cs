using System.Runtime.InteropServices;

namespace TopNotch
{
    /// <summary>
    /// Whether an app has the whole screen to itself right now.
    ///
    /// Windows already holds an opinion on exactly this, because it is the state in which a
    /// notification must not be put in front of somebody: the shell tracks whether what is in front
    /// is a game, a video playing fullscreen, or a presentation, and hands the answer out through
    /// SHQueryUserNotificationState. So this asks the shell rather than measuring windows itself.
    /// That distinction matters: a maximised window covers the screen too, and the island must not
    /// step aside every time somebody maximises something.
    /// </summary>
    internal static class Fullscreen
    {
        // The shell's own states, of which the first three are "an app has the screen". The rest
        // describe an ordinary desktop and are none of this app's business - only the ones that
        // mean the screen is taken are named, so that what is being asked for stays legible.
        private enum State
        {
            Busy = 2,                    // a fullscreen app, or one drawing with Direct3D
            RunningD3DFullscreen = 3,
            PresentationMode = 4
        }

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out State state);

        /// <summary>
        /// True while an app is in fullscreen. False whenever the question cannot be answered, which
        /// leaves the island where it is: a widget that hid because a system call failed would be
        /// worse than one that occasionally failed to hide.
        /// </summary>
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
