using UnityEngine;

namespace BodyTracking.UI
{
    /// <summary>
    /// Centralized design-token source for the whole app UI.
    ///
    /// This is the single place that defines the visual language (colors, spacing, corner radius and
    /// typography). Build every UI element from these tokens instead of hard-coding values per element,
    /// so the interface stays consistent and is trivial to re-theme.
    ///
    /// Why a static class (and not a ScriptableObject): the brief allows either; a static token class
    /// needs no asset/GUID wiring, works identically at edit-time and runtime, and keeps the UI builder
    /// dependency-free. If per-scene theming is ever needed, this can be promoted to a ScriptableObject
    /// with the same field names.
    /// </summary>
    public static class UITokens
    {
        // ----------------------------------------------------------------------------------------
        // COLOR PALETTE
        // A small, deliberately limited palette. Surfaces are dark + translucent so the AR camera
        // feed shows through, keeping the UI minimal and unobtrusive over live video.
        // ----------------------------------------------------------------------------------------

        /// <summary>App backdrop / scrim (rarely full-screen; used for subtle dimming).</summary>
        public static readonly Color Background = new Color(0.05f, 0.05f, 0.06f, 0.75f);

        /// <summary>Primary surface for bars and panels (dark, translucent over the camera feed).</summary>
        public static readonly Color Surface = new Color(0.11f, 0.11f, 0.12f, 0.90f);

        /// <summary>Raised surface for controls sitting on top of a surface (buttons, pills).</summary>
        public static readonly Color SurfaceElevated = new Color(0.20f, 0.20f, 0.22f, 0.95f);

        /// <summary>Primary accent (interactive / brand). iOS system blue.</summary>
        public static readonly Color Primary = new Color(0.04f, 0.52f, 1.00f, 1f);

        /// <summary>Text/icon color on dark surfaces.</summary>
        public static readonly Color OnSurface = new Color(0.96f, 0.96f, 0.98f, 1f);

        /// <summary>Secondary, lower-emphasis text (captions, hints, inactive labels).</summary>
        public static readonly Color Muted = new Color(0.62f, 0.62f, 0.66f, 1f);

        /// <summary>Destructive / record state. iOS system red.</summary>
        public static readonly Color Danger = new Color(1.00f, 0.27f, 0.23f, 1f);

        /// <summary>Positive / localized / ready. iOS system green.</summary>
        public static readonly Color Success = new Color(0.19f, 0.82f, 0.35f, 1f);

        /// <summary>Caution / waiting / partial state. iOS system amber.</summary>
        public static readonly Color Warning = new Color(1.00f, 0.62f, 0.04f, 1f);

        /// <summary>Disabled tint applied to non-interactive controls.</summary>
        public static readonly Color Disabled = new Color(0.45f, 0.45f, 0.48f, 0.45f);

        /// <summary>Hairline divider / outline color.</summary>
        public static readonly Color Outline = new Color(1f, 1f, 1f, 0.10f);

        // ----------------------------------------------------------------------------------------
        // SPACING SCALE (4 / 8 / 12 / 16 / 24)
        // Use these for padding, gaps and margins so rhythm stays consistent everywhere.
        // ----------------------------------------------------------------------------------------
        public const float Space4 = 4f;
        public const float Space8 = 8f;
        public const float Space12 = 12f;
        public const float Space16 = 16f;
        public const float Space24 = 24f;

        // ----------------------------------------------------------------------------------------
        // CORNER RADIUS
        // ----------------------------------------------------------------------------------------
        public const int RadiusSmall = 8;
        public const int RadiusMedium = 14;
        public const int RadiusLarge = 20;
        /// <summary>Effectively a full pill/circle for the sprite generator.</summary>
        public const int RadiusPill = 1000;

        // ----------------------------------------------------------------------------------------
        // TYPOGRAPHY (TextMeshPro point sizes)
        // Three roles only: title, body, caption. Keep the type scale tight for a clean look.
        // ----------------------------------------------------------------------------------------
        public const float FontTitle = 28f;
        public const float FontBody = 22f;
        public const float FontCaption = 18f;

        // ----------------------------------------------------------------------------------------
        // COMPONENT SIZING (derived constants for the transport / status components)
        // ----------------------------------------------------------------------------------------
        /// <summary>Diameter of the primary transport button (play/pause).</summary>
        public const float TransportPrimaryDiameter = 58f;
        /// <summary>Diameter of secondary transport buttons (record / stop).</summary>
        public const float TransportSecondaryDiameter = 46f;
        /// <summary>Height of the status pills in the top bar.</summary>
        public const float PillHeight = 28f;
        /// <summary>Top-left round toolbar icons (screen toggle, settings) — matches playback side controls.</summary>
        public const float ToolbarIconDiameter = 44f;
        /// <summary>Height of the scrub/timeline slider track.</summary>
        public const float ScrubTrackHeight = 5f;
        /// <summary>Diameter of the scrub handle.</summary>
        public const float ScrubHandleDiameter = 20f;

        // ----------------------------------------------------------------------------------------
        // PLAYBACK SCREEN (vertical move timeline + floating transport)
        // Dark glass controls + teal accent; kept translucent so AR feed shows through.
        // ----------------------------------------------------------------------------------------
        public static readonly Color PlaybackTrack = new Color(1f, 1f, 1f, 0.42f);
        public static readonly Color PlaybackNode = new Color(0.06f, 0.18f, 0.14f, 0.68f);
        public static readonly Color PlaybackNodeActive = new Color(0f, 0.82f, 0.72f, 0.82f);
        public static readonly Color PlaybackPlayhead = new Color(0f, 0.82f, 0.72f, 0.78f);
        /// <summary>Secondary transport circles (−, +, speed, loop).</summary>
        public static readonly Color PlaybackTransportBtn = new Color(0.10f, 0.12f, 0.12f, 0.48f);
        /// <summary>Primary play/pause — teal to match the timeline, not system blue.</summary>
        public static readonly Color PlaybackTransportPlay = new Color(0f, 0.82f, 0.72f, 0.62f);
        public const float PlaybackNodeDiameter = 36f;
        public const float PlaybackPlayheadDiameter = 44f;
        public const float PlaybackTrackWidth = 3f;
    }
}
