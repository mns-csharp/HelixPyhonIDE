using System;
using System.Runtime.InteropServices;

namespace HelixPythonNamespace.Core
{
    /// <summary>
    /// The one switch a test run uses to keep the IDE's windows away from the user's screen, mouse
    /// and keyboard (book Chapter 4, §4.6.5). <c>HELIXSTUDIO_TEST_OFFSCREEN=1</c> is set only by
    /// <c>HelixStudio.runsettings</c>; it is read once, here.
    ///
    /// <para>
    /// When it is set, top-level windows (<c>LunaForm</c> and the editor/debugger popups) are made
    /// layered at alpha 0 and <c>WS_EX_NOACTIVATE</c>, a shown <c>LunaForm</c> is placed off every
    /// screen instead of being clamped onto one, and the product's activation calls do not take the
    /// foreground. When it is not set, every function here returns its input or <c>true</c>, so the
    /// IDE behaves exactly as before for users.
    /// </para>
    /// </summary>
    public static class TestEnvironment
    {
        public const string Variable = "HELIXSTUDIO_TEST_OFFSCREEN";

        /// <summary>True only in a test run (see the class summary).</summary>
        public static readonly bool OffScreen = IsSet(Environment.GetEnvironmentVariable(Variable));

        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_CHILD = 0x40000000;
        private const uint LWA_ALPHA = 0x2;

        public static bool IsSet(string value)
        {
            return value == "1";
        }

        /// <summary>The extended style a top-level window is created with.</summary>
        public static int ExStyle(int style, int exStyle)
        {
            return ExStyle(style, exStyle, OffScreen);
        }

        public static int ExStyle(int style, int exStyle, bool offScreen)
        {
            if (!offScreen || (style & WS_CHILD) != 0) return exStyle;
            return exStyle | WS_EX_LAYERED | WS_EX_NOACTIVATE;
        }

        /// <summary>Call from <c>OnHandleCreated</c> of a window whose style went through
        /// <see cref="ExStyle(int, int)"/>: a layered window is drawn at alpha 0.</summary>
        public static void HandleCreated(IntPtr hwnd, int style)
        {
            if (OffScreen && (style & WS_CHILD) == 0) SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);
        }

        /// <summary>May the product activate a window or bring it to the foreground?</summary>
        public static bool MayActivate { get { return MayActivateWhen(OffScreen); } }

        public static bool MayActivateWhen(bool offScreen)
        {
            return !offScreen;
        }

        /// <summary>Does a shown <c>LunaForm</c> pull itself onto the nearest screen?</summary>
        public static bool ClampOnShown { get { return ClampOnShownWhen(OffScreen); } }

        public static bool ClampOnShownWhen(bool offScreen)
        {
            return !offScreen;
        }

        /// <summary>
        /// The x of a window moved off every screen: left of the whole virtual screen, by a fixed
        /// shift so windows keep their offsets from each other.
        /// </summary>
        public static int OffScreenLeft(int left, int virtualScreenWidth)
        {
            return left - virtualScreenWidth - 8000;
        }

        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    }
}
