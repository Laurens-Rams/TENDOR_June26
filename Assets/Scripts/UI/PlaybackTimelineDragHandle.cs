using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using BodyTracking;

namespace BodyTracking.UI
{
    /// <summary>
    /// Draggable vertical-timeline playhead. Seeks playback while dragging and temporarily disables
    /// checkpoint buttons so move circles are not selected when the pointer passes over them.
    /// </summary>
    public class PlaybackTimelineDragHandle : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        private BodyTrackingController controller;
        private RectTransform trackArea;
        private Button[] checkpointButtons;

        private bool dragging;
        private bool wasPlaying;
        private bool wasPaused;

        public bool IsDragging => dragging;
        public System.Action OnScrubbed;

        public void Initialize(BodyTrackingController ctrl, RectTransform track, Button[] checkpoints)
        {
            controller = ctrl;
            trackArea = track;
            checkpointButtons = checkpoints;
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            dragging = true;
            SetCheckpointsInteractable(false);

            if (controller == null)
                return;

            wasPlaying = controller.IsPlaying;
            wasPaused = controller.IsPaused;

            if (!controller.IsPlaying)
            {
                controller.LoadLatestRecording();
                if (controller.StartPlayback())
                    controller.PausePlayback();
            }
            else if (!controller.IsPaused)
            {
                controller.PausePlayback();
            }

            ApplyDragPosition(eventData);
        }

        public void OnDrag(PointerEventData eventData)
        {
            ApplyDragPosition(eventData);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            ApplyDragPosition(eventData);
            dragging = false;

            if (controller != null && wasPlaying && !wasPaused)
                controller.ResumePlayback();
        }

        private void ApplyDragPosition(PointerEventData eventData)
        {
            if (controller == null || trackArea == null)
                return;

            float normalized = ScreenPointToNormalized(eventData.position, eventData.pressEventCamera);
            float duration = controller.PlaybackDuration;
            if (duration > 0f)
                controller.SeekPlaybackTime(normalized * duration);

            OnScrubbed?.Invoke();
        }

        private float ScreenPointToNormalized(Vector2 screenPoint, Camera eventCamera)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                trackArea, screenPoint, eventCamera, out var localPoint);

            var rect = trackArea.rect;
            return Mathf.Clamp01((localPoint.y - rect.yMin) / rect.height);
        }

        private void SetCheckpointsInteractable(bool interactable)
        {
            if (checkpointButtons == null)
                return;

            foreach (var btn in checkpointButtons)
            {
                if (btn != null)
                    btn.interactable = interactable;
            }
        }
    }
}
