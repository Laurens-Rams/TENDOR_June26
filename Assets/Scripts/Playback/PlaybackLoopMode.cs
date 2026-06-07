namespace BodyTracking.Playback
{
    /// <summary>How playback wraps when it reaches the end of the timeline (or a move segment).</summary>
    public enum PlaybackLoopMode
    {
        /// <summary>Loop the entire recording from start to end.</summary>
        Full,
        /// <summary>Loop only between the selected checkpoint and the next one.</summary>
        Segment
    }
}
