using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using BodyTracking.Data;

namespace BodyTracking.AR
{
    public class ARImageTargetManager : MonoBehaviour
    {
        [Header("AR Settings")]
        public ARTrackedImageManager trackedImageManager;
        public string targetImageName = "Wall 1";
        [Tooltip("If true, TrackingState.Limited counts as 'good enough' for record/play. Full Tracking is stricter; Limited is less stable but often all you get with hard targets or AR Remote.")]
        [SerializeField] private bool allowLimitedImageTracking = true;

        [Header("Tracking Stability")]
        [SerializeField] private bool useSmoothedTargetPose = false;
        [SerializeField] private float positionSmoothing = 8f;
        [SerializeField] private float rotationSmoothing = 10f;
        [SerializeField] private float maxSmoothJumpDistance = 0.25f;

        [Header("Reference Image Overlay")]
        [SerializeField] private bool showReferenceImageOverlay = true;
        [SerializeField] private Texture2D referenceImageOverlayTexture;
        [Range(0.1f, 1f)]
        [SerializeField] private float referenceImageOverlayAlpha = 0.65f;
        [SerializeField] private float referenceImageOverlayOffset = 0.002f;
        [Tooltip("If true, overlay only when TrackingState is Tracking (stricter). Off allows Limited so the poster still shows on difficult surfaces.")]
        [SerializeField] private bool detectionVisualRequiresFullTracking = false;
        [Tooltip("If true, overlay only flashes on (re)entering usable tracking. Off = overlay follows the trackable every frame (recommended for real use).")]
        [SerializeField] private bool momentaryDetectionTestMode = false;
        [SerializeField] private float detectionVisualLifetime = 0.25f;
        [Tooltip("Unity's default Plane lies in XZ with +Y normal. AR tracked images lie in the transform's image plane (normal = transform.forward). Default (-90,0,0) maps the plane onto the image; adjust if your overlay appears edge-on.")]
        [SerializeField] private Vector3 overlayPlaneLocalEulerAngles = new(90f, 0f, 0f);
        [Tooltip("If the printed poster is landscape but width/height look swapped in AR, toggle this once.")]
        [SerializeField] private bool swapOverlayWidthHeight = false;
        
        private ARTrackedImage currentTrackedImage;
        private bool isImageDetected = false;
        private bool contentAttachedToTarget = false;
        private bool loggedMissingContentForTarget = false;
        private bool hasStablePose = false;
        private bool loggedOverlayAspectWarning = false;
        private float lastDetectionVisualTime = -999f;
        private string lastTrackedImageName = "";
        private TrackingState lastTrackingState = TrackingState.None;
        
        // Content management (like TrackingLogic)
        private GameObject[] contents;
        private GameObject activeContent;
        private GameObject smoothedTargetRoot;
        private GameObject referenceImageOverlay;
        private LineRenderer detectionBoundsLine;
        private Material referenceImageOverlayMaterial;
        
        public event System.Action<Transform> OnImageTargetDetected;
        public event System.Action<Transform> OnImageTargetUpdated;
        public event System.Action OnImageTargetLost;
        
        public bool IsImageDetected => isImageDetected && currentTrackedImage != null && IsImageTrackingStateUsable(currentTrackedImage.trackingState);
        public bool HasSeenTarget => currentTrackedImage != null;
        public string LastTrackedImageName => lastTrackedImageName;
        public TrackingState LastTrackingState => lastTrackingState;
        public Vector2 CurrentTargetSize => currentTrackedImage != null ? currentTrackedImage.size : Vector2.zero;
        public Transform ImageTargetTransform => IsImageDetected ? currentTrackedImage.transform : null;
        
        private bool IsImageTrackingStateUsable(TrackingState state)
        {
            if (state == TrackingState.Tracking) return true;
            if (allowLimitedImageTracking && state == TrackingState.Limited) return true;
            return false;
        }

        void OnEnable()
        {
            // Find all child GameObjects that could be content
            contents = new GameObject[transform.childCount];
            for (int i = 0; i < transform.childCount; i++)
            {
                contents[i] = transform.GetChild(i).gameObject;
                // Initially deactivate all content
                contents[i].SetActive(false);
            }
            
            if (trackedImageManager == null)
            {
                trackedImageManager = Object.FindAnyObjectByType<ARTrackedImageManager>();
            }
            
            if (trackedImageManager != null)
            {
                trackedImageManager.trackedImagesChanged += OnTrackedImagesChanged;
                Debug.Log("[ARImageTargetManager] Subscribed to tracked images changed events");
            }
            else
            {
                Debug.LogError("[ARImageTargetManager] ARTrackedImageManager not found!");
            }
        }

        void OnDisable()
        {
            if (trackedImageManager != null)
            {
                trackedImageManager.trackedImagesChanged -= OnTrackedImagesChanged;
            }
        }

        private void OnTrackedImagesChanged(ARTrackedImagesChangedEventArgs eventArgs)
        {
            foreach (var trackedImage in eventArgs.added)
            {
                Debug.Log($"[ARImageTargetManager] Image added: {trackedImage.referenceImage.name}");
                if (TryTrackTarget(trackedImage))
                    break;
            }

            foreach (var trackedImage in eventArgs.updated)
            {
                TrackingState previousTrackingState = IsTargetImage(trackedImage) ? lastTrackingState : TrackingState.None;
                lastTrackedImageName = trackedImage.referenceImage.name;
                lastTrackingState = trackedImage.trackingState;

                if (IsImageTrackingStateUsable(trackedImage.trackingState))
                {
                    Debug.Log($"[ARImageTargetManager] Image updated ({trackedImage.trackingState}): {trackedImage.referenceImage.name}");
                    bool enteredUsableTracking = !IsImageTrackingStateUsable(previousTrackingState);
                    if ((!momentaryDetectionTestMode || enteredUsableTracking) && TryTrackTarget(trackedImage))
                        break;
                }
                else
                {
                    Debug.Log($"[ARImageTargetManager] Image updated, not usable: {trackedImage.referenceImage.name}, state: {trackedImage.trackingState}");
                    if (IsTargetImage(trackedImage) && currentTrackedImage == trackedImage)
                    {
                        isImageDetected = false;
                    }
                }
            }
            
            foreach (var trackedImage in eventArgs.removed)
            {
                Debug.Log($"[ARImageTargetManager] Image removed: {trackedImage.referenceImage.name}");
                HandleImageRemoved(trackedImage);
            }
        }

        private bool TryTrackTarget(ARTrackedImage trackedImage)
        {
            if (trackedImage == null)
                return false;

            lastTrackedImageName = trackedImage.referenceImage.name;
            lastTrackingState = trackedImage.trackingState;

            if (!IsTargetImage(trackedImage))
            {
                Debug.Log($"[ARImageTargetManager] Ignoring image '{trackedImage.referenceImage.name}', looking for '{targetImageName}'");
                return false;
            }

            if (!IsImageTrackingStateUsable(trackedImage.trackingState))
            {
                currentTrackedImage = trackedImage;
                isImageDetected = false;
                Debug.Log($"[ARImageTargetManager] Target '{targetImageName}' found but state not usable yet: {trackedImage.trackingState}");
                return false;
            }

            Debug.Log($"[ARImageTargetManager] ✅ Target usable ({trackedImage.trackingState}): {trackedImage.referenceImage.name}");
            
            bool wasAlreadyTracking = currentTrackedImage == trackedImage && isImageDetected && IsImageTrackingStateUsable(trackedImage.trackingState);
            currentTrackedImage = trackedImage;
            isImageDetected = true;
            lastDetectionVisualTime = Time.time;
            UpdateReferenceImageOverlay(trackedImage);
            
            if (wasAlreadyTracking)
            {
                OnImageTargetUpdated?.Invoke(trackedImage.transform);
            }
            else
            {
                OnImageTargetDetected?.Invoke(trackedImage.transform);
            }
            
            Debug.Log($"[ARImageTargetManager] Target tracking ready at position: {trackedImage.transform.position}");
            
            return true;
        }

        private void HandleImageRemoved(ARTrackedImage trackedImage)
        {
            if (!IsTargetImage(trackedImage)) return;
            
            currentTrackedImage = null;
            isImageDetected = false;
            contentAttachedToTarget = false;
            loggedMissingContentForTarget = false;
            hasStablePose = false;
            
            // Deactivate and detach content
            if (activeContent != null)
            {
                activeContent.SetActive(false);
                activeContent.transform.parent = this.transform; // Return to original parent
                activeContent = null;
            }

            HideReferenceImageOverlay();
            
            OnImageTargetLost?.Invoke();
            Debug.Log("[ARImageTargetManager] Image target lost, content detached");
        }

        private bool IsTargetImage(ARTrackedImage trackedImage)
        {
            return trackedImage != null && trackedImage.referenceImage.name == targetImageName;
        }

        void Update()
        {
            if (currentTrackedImage == null) return;
            if (!IsImageTrackingStateUsable(currentTrackedImage.trackingState)) return;

            if (momentaryDetectionTestMode)
            {
                if (Time.time - lastDetectionVisualTime > detectionVisualLifetime)
                {
                    HideReferenceImageOverlay();
                }
                return;
            }

            UpdateReferenceImageOverlay(currentTrackedImage);
            // Fire even when there is no named child content — tracking still uses the AR marker transform.
            OnImageTargetUpdated?.Invoke(currentTrackedImage.transform);
        }

        private void UpdateSmoothedTargetPose(ARTrackedImage trackedImage, bool snap)
        {
            if (!useSmoothedTargetPose || trackedImage == null)
                return;

            if (smoothedTargetRoot == null)
            {
                smoothedTargetRoot = new GameObject("SmoothedImageTarget");
            }

            if (!hasStablePose || snap || Vector3.Distance(smoothedTargetRoot.transform.position, trackedImage.transform.position) > maxSmoothJumpDistance)
            {
                smoothedTargetRoot.transform.SetPositionAndRotation(trackedImage.transform.position, trackedImage.transform.rotation);
                hasStablePose = true;
                return;
            }

            float positionT = 1f - Mathf.Exp(-positionSmoothing * Time.deltaTime);
            float rotationT = 1f - Mathf.Exp(-rotationSmoothing * Time.deltaTime);
            smoothedTargetRoot.transform.position = Vector3.Lerp(smoothedTargetRoot.transform.position, trackedImage.transform.position, positionT);
            smoothedTargetRoot.transform.rotation = Quaternion.Slerp(smoothedTargetRoot.transform.rotation, trackedImage.transform.rotation, rotationT);
        }

        private void UpdateReferenceImageOverlay(ARTrackedImage trackedImage)
        {
            if (!showReferenceImageOverlay ||
                trackedImage == null ||
                (detectionVisualRequiresFullTracking && trackedImage.trackingState != TrackingState.Tracking))
            {
                HideReferenceImageOverlay();
                return;
            }

            EnsureReferenceImageOverlay(trackedImage);
            if (referenceImageOverlay == null) return;

            RefreshOverlayMaterial();

            if (referenceImageOverlayTexture != null)
                LogOverlayAspectMismatchIfNeeded(trackedImage, referenceImageOverlayTexture);

            float w = swapOverlayWidthHeight ? trackedImage.size.y : trackedImage.size.x;
            float h = swapOverlayWidthHeight ? trackedImage.size.x : trackedImage.size.y;

            referenceImageOverlay.transform.SetParent(trackedImage.transform, false);
            referenceImageOverlay.transform.localPosition = Vector3.zero;
            referenceImageOverlay.transform.localRotation = Quaternion.Euler(overlayPlaneLocalEulerAngles);
            referenceImageOverlay.transform.localScale = new Vector3(w / 10f, 1f, h / 10f);
            referenceImageOverlay.SetActive(true);

            UpdateDetectionBounds(new Vector2(w, h));
        }

        void RefreshOverlayMaterial()
        {
            if (referenceImageOverlayMaterial == null)
                return;

            referenceImageOverlayMaterial.mainTexture = referenceImageOverlayTexture;
            if (referenceImageOverlayTexture != null)
                referenceImageOverlayMaterial.color = new Color(1f, 1f, 1f, referenceImageOverlayAlpha);
            else
            {
                var color = Color.yellow;
                color.a = referenceImageOverlayAlpha;
                referenceImageOverlayMaterial.color = color;
            }

            if (referenceImageOverlayMaterial.HasProperty("_Cull"))
                referenceImageOverlayMaterial.SetInt("_Cull", (int)CullMode.Off);
        }

        private void EnsureReferenceImageOverlay(ARTrackedImage trackedImage)
        {
            if (referenceImageOverlay == null)
            {
                referenceImageOverlay = GameObject.CreatePrimitive(PrimitiveType.Plane);
                referenceImageOverlay.name = "DetectedImageArea";

                Collider overlayCollider = referenceImageOverlay.GetComponent<Collider>();
                if (overlayCollider != null)
                {
                    Destroy(overlayCollider);
                }

                GameObject boundsObject = new GameObject("DetectedImageBounds");
                boundsObject.transform.SetParent(referenceImageOverlay.transform, false);
                detectionBoundsLine = boundsObject.AddComponent<LineRenderer>();
                detectionBoundsLine.useWorldSpace = false;
                detectionBoundsLine.loop = false;
                detectionBoundsLine.positionCount = 5;
                detectionBoundsLine.startWidth = 0.01f;
                detectionBoundsLine.endWidth = 0.01f;
            }

            if (referenceImageOverlayMaterial == null)
            {
                var shader = Shader.Find("Unlit/Transparent")
                    ?? Shader.Find("Unlit/Texture")
                    ?? Shader.Find("Sprites/Default");
                referenceImageOverlayMaterial = new Material(shader);
            }

            RefreshOverlayMaterial();

            Renderer overlayRenderer = referenceImageOverlay.GetComponent<Renderer>();
            if (overlayRenderer != null)
            {
                overlayRenderer.material = referenceImageOverlayMaterial;
            }

            if (detectionBoundsLine != null)
            {
                if (detectionBoundsLine.material == null)
                {
                    detectionBoundsLine.material = new Material(Shader.Find("Sprites/Default"));
                }
                detectionBoundsLine.startColor = Color.red;
                detectionBoundsLine.endColor = Color.red;
            }
        }

        private void UpdateDetectionBounds(Vector2 imageSize)
        {
            if (detectionBoundsLine == null) return;

            float hx = imageSize.x * 0.5f;
            float hz = imageSize.y * 0.5f;
            float y = referenceImageOverlayOffset;
            detectionBoundsLine.SetPosition(0, new Vector3(-hx, y, -hz));
            detectionBoundsLine.SetPosition(1, new Vector3(-hx, y, hz));
            detectionBoundsLine.SetPosition(2, new Vector3(hx, y, hz));
            detectionBoundsLine.SetPosition(3, new Vector3(hx, y, -hz));
            detectionBoundsLine.SetPosition(4, new Vector3(-hx, y, -hz));
        }

        private void HideReferenceImageOverlay()
        {
            if (referenceImageOverlay != null)
            {
                referenceImageOverlay.SetActive(false);
            }
        }

        private void LogOverlayAspectMismatchIfNeeded(ARTrackedImage trackedImage, Texture2D overlayTexture)
        {
            if (loggedOverlayAspectWarning || trackedImage == null || overlayTexture == null || trackedImage.size.y <= 0f)
                return;

            float textureAspect = (float)overlayTexture.width / overlayTexture.height;
            float libraryAspect = trackedImage.size.x / trackedImage.size.y;
            if (Mathf.Abs(textureAspect - libraryAspect) / textureAspect > 0.05f)
            {
                Debug.LogWarning($"[ARImageTargetManager] Reference image aspect mismatch. Texture aspect {textureAspect:F3}, library aspect {libraryAspect:F3}, tracked size {trackedImage.size.x:F3}m x {trackedImage.size.y:F3}m. Update the reference image physical size if overlay does not fit.");
                loggedOverlayAspectWarning = true;
            }
        }

        public CoordinateFrame GetCurrentCoordinateFrame()
        {
            if (IsImageDetected)
            {
                return new CoordinateFrame(currentTrackedImage.transform);
            }
            return default;
        }

        // Public methods for debugging
        public void ToggleContentVisibility()
        {
            if (activeContent != null)
            {
                activeContent.SetActive(!activeContent.activeSelf);
                Debug.Log($"[ARImageTargetManager] Content visibility toggled: {activeContent.activeSelf}");
            }
        }

        public void ListAvailableContent()
        {
            Debug.Log($"[ARImageTargetManager] Available content objects:");
            for (int i = 0; i < contents.Length; i++)
            {
                if (contents[i] != null)
                {
                    Debug.Log($"  - {contents[i].name} (active: {contents[i].activeSelf})");
                }
            }
        }

        void OnDestroy()
        {
            if (referenceImageOverlay != null)
            {
                Destroy(referenceImageOverlay);
            }

            if (smoothedTargetRoot != null)
            {
                Destroy(smoothedTargetRoot);
            }

            if (referenceImageOverlayMaterial != null)
            {
                Destroy(referenceImageOverlayMaterial);
            }
        }
    }
} 