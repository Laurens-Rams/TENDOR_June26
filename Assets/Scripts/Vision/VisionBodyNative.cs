using System;
using System.Runtime.InteropServices;

namespace BodyTracking.Vision
{
    /// <summary>
    /// P/Invoke surface for the native Apple Vision 3D body-pose plugin
    /// (Assets/Plugins/iOS/VisionBody/VisionBodyBridge.mm). On non-iOS targets and in
    /// the Editor these resolve to stubs that report "no body" so the rest of the
    /// pipeline stays compilable and runnable.
    /// </summary>
    internal static class VisionBodyNative
    {
        public const int JointCount = VisionBodySkeleton.JointCount;

#if UNITY_IOS && !UNITY_EDITOR
        const string Lib = "__Internal";

        [DllImport(Lib)]
        public static extern bool VisionBody_Initialize();

        [DllImport(Lib)]
        public static extern bool VisionBody_Detect(
            IntPtr bgra, int width, int height, int bytesPerRow, int orientation,
            [Out] float[] outPositions, [Out] float[] outConfidences,
            out int outJointCount, out float outBodyHeight);

        [DllImport(Lib)]
        public static extern void VisionBody_Shutdown();
#else
        public static bool VisionBody_Initialize() => false;

        public static bool VisionBody_Detect(
            IntPtr bgra, int width, int height, int bytesPerRow, int orientation,
            float[] outPositions, float[] outConfidences,
            out int outJointCount, out float outBodyHeight)
        {
            outJointCount = 0;
            outBodyHeight = 0f;
            return false;
        }

        public static void VisionBody_Shutdown() { }
#endif
    }
}
