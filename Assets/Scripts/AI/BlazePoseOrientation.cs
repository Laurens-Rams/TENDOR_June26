using UnityEngine;

namespace BodyTracking.AI
{
    /// <summary>
    /// Maps BlazePose landmark UVs from the native AR camera sensor frame (often landscape, e.g. 1920x1440)
    /// into portrait display / intrinsics / depth coordinates on iOS.
    /// </summary>
    public static class BlazePoseOrientation
    {
        public struct Remap
        {
            public bool swapXY;
            public bool flipX;
            public bool flipY;
        }

        /// <summary>
        /// Infer remap when sensor texture and camera intrinsics disagree on aspect (portrait phone, landscape sensor).
        /// </summary>
        public static Remap DetectRemap(int sensorWidth, int sensorHeight, Vector2Int intrinsicsResolution)
        {
            var remap = new Remap { flipY = true }; // ML UV origin is bottom-left; pixel rows are top-down.

            if (sensorWidth <= 0 || sensorHeight <= 0)
                return remap;

            bool sensorLandscape = sensorWidth > sensorHeight;
            bool intrinsicsPortrait = intrinsicsResolution.y > intrinsicsResolution.x;
            if (sensorLandscape && intrinsicsPortrait)
            {
                remap.swapXY = true;
                remap.flipX = true;
            }

            return remap;
        }

        public static Remap DetectRemap(int sensorWidth, int sensorHeight)
        {
            bool sensorLandscape = sensorWidth > sensorHeight;
            bool screenPortrait = Screen.height > Screen.width;
            var remap = new Remap { flipY = true };
            if (sensorLandscape && screenPortrait)
            {
                remap.swapXY = true;
                remap.flipX = true;
            }
            return remap;
        }

        public static Vector2 ApplyRemap(Vector2 uv, Remap remap)
        {
            if (remap.swapXY)
                uv = new Vector2(uv.y, uv.x);
            if (remap.flipX)
                uv.x = 1f - uv.x;
            if (remap.flipY)
                uv.y = 1f - uv.y;
            return uv;
        }

        /// <summary>Aspect-fit rect where the oriented camera image appears on screen (matches AR background).</summary>
        public static Rect GetDisplayRect(int sensorWidth, int sensorHeight, Remap remap)
        {
            float orientedW = remap.swapXY ? sensorHeight : sensorWidth;
            float orientedH = remap.swapXY ? sensorWidth : sensorHeight;
            if (orientedW <= 0f || orientedH <= 0f)
                return new Rect(0, 0, Screen.width, Screen.height);

            float sensorAspect = orientedW / orientedH;
            float screenAspect = (float)Screen.width / Screen.height;

            if (screenAspect > sensorAspect)
            {
                float w = Screen.height * sensorAspect;
                return new Rect((Screen.width - w) * 0.5f, 0f, w, Screen.height);
            }

            float h = Screen.width / sensorAspect;
            return new Rect(0f, (Screen.height - h) * 0.5f, Screen.width, h);
        }

        public static Vector2 SensorUvToScreen(Vector2 sensorUv, int sensorWidth, int sensorHeight, Remap remap)
        {
            Vector2 displayUv = ApplyRemap(sensorUv, remap);
            var rect = GetDisplayRect(sensorWidth, sensorHeight, remap);
            return new Vector2(rect.x + displayUv.x * rect.width, rect.y + displayUv.y * rect.height);
        }

        public static Vector2 SensorUvToPixel(Vector2 sensorUv, Vector2Int resolution, Remap remap)
        {
            Vector2 uv = ApplyRemap(sensorUv, remap);
            return new Vector2(uv.x * resolution.x, uv.y * resolution.y);
        }
    }
}
