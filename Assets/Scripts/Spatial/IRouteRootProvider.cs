using System;
using UnityEngine;
using BodyTracking.Data;

namespace BodyTracking.Spatial
{
    /// <summary>
    /// Identifies which spatial system currently supplies the RouteRoot frame.
    /// </summary>
    public enum SpatialSourceType
    {
        None,
        Immersal,
        ImageTarget
    }

    /// <summary>
    /// Abstraction over "what supplies the stable wall frame (RouteRoot) the recorder/player work in".
    ///
    /// The recorder stores every joint in RouteRoot-local space
    /// (<c>RouteRoot.InverseTransformPoint</c>) and playback maps back via
    /// <c>RouteRoot.TransformPoint</c>. By moving this behind a provider, the same per-joint math works
    /// whether the frame comes from Immersal (primary) or the AR image target / world map (fallback).
    /// </summary>
    public interface IRouteRootProvider
    {
        /// <summary>
        /// The transform body motion is stored relative to. Should remain non-null once the provider is
        /// enabled (even before localization) so callers never NRE; check <see cref="IsLocalized"/> before
        /// trusting it for placement.
        /// </summary>
        Transform RouteRoot { get; }

        /// <summary>True when the underlying system has a usable, wall-aligned pose for RouteRoot.</summary>
        bool IsLocalized { get; }

        /// <summary>
        /// True when this provider can be used at all on the current build/scene (e.g. Immersal SDK present
        /// and configured). An unavailable provider is skipped by <see cref="RouteRootManager"/>.
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>Spatial map id (Immersal map id, or image target name) RouteRoot is anchored to.</summary>
        string MapId { get; }

        /// <summary>Optional logical route/problem id.</summary>
        string RouteId { get; }

        /// <summary>Which spatial system this provider represents.</summary>
        SpatialSourceType Source { get; }

        /// <summary>Raised when <see cref="IsLocalized"/> flips. Argument is the new localization state.</summary>
        event Action<bool> OnLocalizationChanged;

        /// <summary>Convenience: a rigid <see cref="CoordinateFrame"/> snapshot of the current RouteRoot.</summary>
        CoordinateFrame GetRouteRootFrame();
    }
}
