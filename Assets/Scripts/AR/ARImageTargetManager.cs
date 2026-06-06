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
        [Tooltip("Auto-orient the overlay so it lies flat on the detected image regardless of AR Foundation's axis convention. Recommended ON. If off, the manual overlayPlaneLocalEulerAngles is used.")]
        [SerializeField] private bool autoOrientOverlayToSurface = true;
        [Tooltip("In-plane spin (degrees about the surface normal) applied to the auto-oriented overlay so the texture's printed orientation matches the marker. 180 is correct for the +Y-normal convention; try 0/90/270 if the print looks rotated.")]
        [SerializeField] private float overlayTextureSpinDegrees = 180f;
        [Tooltip("Fine scale tuning for the overlay. Leave at 1.0 if the marker's physical size matches the reference image library size. If the overlay looks ~110% too big, the printed marker is smaller than the library size - measure it and set the library size, or nudge this down.")]
        [Range(0.5f, 1.5f)]
        [SerializeField] private float overlayScaleMultiplier = 1f;
        [Tooltip("Manual fallback rotation used only when Auto Orient Overlay To Surface is OFF. Unity's default Plane lies in XZ with +Y normal.")]
        [SerializeField] private Vector3 overlayPlaneLocalEulerAngles = new(0f, 180f, 0f);
        [Tooltip("If the printed poster is landscape but width/height look swapped in AR, toggle this once.")]
        [SerializeField] private bool swapOverlayWidthHeight = false;
        [Tooltip("Draw a separate red wireframe rectangle around the image. Redundant with the filled overlay plane and OFF by default to avoid a second overlapping visual.")]
        [SerializeField] private bool showDetectionBoundsRectangle = false;
        
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
        private Texture2D appliedOverlayTexture;
        private float appliedOverlayAlpha = -1f;
        private bool overlayMaterialDirty = true;
        
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
                    bool enteredUsableTracking = !IsImageTrackingStateUsable(previousTrackingState);
                    if ((!momentaryDetectionTestMode || enteredUsableTracking) && TryTrackTarget(trackedImage))
                        break;
                }
                else if (IsTargetImage(trackedImage) && currentTrackedImage == trackedImage)
                {
                    isImageDetected = false;
                }
            }
            
            foreach (var trackedImage in eventArgs.removed)
                HandleImageRemoved(trackedImage);
        }

        private bool TryTrackTarget(ARTrackedImage trackedImage)
        {
            if (trackedImage == null)
                return false;

            lastTrackedImageName = trackedImage.referenceImage.name;
            lastTrackingState = trackedImage.trackingState;

            if (!IsTargetImage(trackedImage))
                return false;

            if (!IsImageTrackingStateUsable(trackedImage.trackingState))
            {
                currentTrackedImage = trackedImage;
                isImageDetected = false;
                return false;
            }
            
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
                Debug.Log($"[ARImageTargetManager] Target ready: {trackedImage.referenceImage.name}");
            }
            
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

            Quaternion overlayLocalRotation = autoOrientOverlayToSurface
                ? ComputeOverlayLocalRotation(trackedImage)
                : Quaternion.Euler(overlayPlaneLocalEulerAngles);

            referenceImageOverlay.transform.SetParent(trackedImage.transform, false);
            referenceImageOverlay.transform.localPosition = Vector3.zero;
            referenceImageOverlay.transform.localRotation = overlayLocalRotation;
            referenceImageOverlay.transform.localScale = new Vector3(w / 10f * overlayScaleMultiplier, 1f, h / 10f * overlayScaleMultiplier);
            referenceImageOverlay.SetActive(true);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // #region agent diag
            LogTrackedImageAxisDiagnostic(trackedImage);
            // #endregion
#endif

            if (detectionBoundsLine != null)
            {
                detectionBoundsLine.transform.SetParent(trackedImage.transform, false);
                detectionBoundsLine.transform.localPosition = Vector3.zero;
                detectionBoundsLine.transform.localRotation = overlayLocalRotation;
                detectionBoundsLine.transform.localScale = Vector3.one;
                detectionBoundsLine.gameObject.SetActive(true);
                UpdateDetectionBounds(new Vector2(w, h));
            }
        }

        void RefreshOverlayMaterial()
        {
            if (referenceImageOverlayMaterial == null)
                return;

            if (!overlayMaterialDirty &&
                appliedOverlayTexture == referenceImageOverlayTexture &&
                Mathf.Approximately(appliedOverlayAlpha, referenceImageOverlayAlpha))
            {
                return;
            }

            // Material properties rarely change, so avoid reassigning them every tracked-image frame.
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

            appliedOverlayTexture = referenceImageOverlayTexture;
            appliedOverlayAlpha = referenceImageOverlayAlpha;
            overlayMaterialDirty = false;
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

                if (showDetectionBoundsRectangle)
                {
                    // Parent the wireframe to the tracked image (NOT the 1/10-scaled plane) so its
                    // metre-based corner positions render at the true image size instead of being
                    // shrunk by the plane's scale.
                    GameObject boundsObject = new GameObject("DetectedImageBounds");
                    boundsObject.transform.SetParent(trackedImage.transform, false);
                    detectionBoundsLine = boundsObject.AddComponent<LineRenderer>();
                    detectionBoundsLine.useWorldSpace = false;
                    detectionBoundsLine.loop = false;
                    detectionBoundsLine.positionCount = 5;
                    detectionBoundsLine.startWidth = 0.01f;
                    detectionBoundsLine.endWidth = 0.01f;
                }
            }

            if (referenceImageOverlayMaterial == null)
            {
                var shader = Shader.Find("Unlit/Transparent")
                    ?? Shader.Find("Unlit/Texture")
                    ?? Shader.Find("Sprites/Default");
                referenceImageOverlayMaterial = new Material(shader);
                overlayMaterialDirty = true;
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

        /// <summary>
        /// Lays the Unity Plane (local +Y normal) flat onto the detected image by aligning its
        /// normal with the tracked image's true surface normal. The surface normal is the tracked
        /// image local axis most aligned with the direction to the camera (image tracking only locks
        /// when the camera faces the marker). Works for either AR Foundation axis convention.
        /// </summary>
        private Quaternion ComputeOverlayLocalRotation(ARTrackedImage trackedImage)
        {
            Transform t = trackedImage.transform;
            Camera cam = Camera.main;
            // Direction from the image toward the camera (the surface normal points roughly this way).
            Vector3 worldToCam = cam != null ? (cam.transform.position - t.position) : t.up;
            Vector3 localToCam = t.InverseTransformDirection(worldToCam);

            // Pick the dominant local axis as the surface normal, preserving sign so it faces the camera.
            float ax = Mathf.Abs(localToCam.x);
            float ay = Mathf.Abs(localToCam.y);
            float az = Mathf.Abs(localToCam.z);

            Vector3 localNormal;
            if (ay >= ax && ay >= az)
                localNormal = new Vector3(0f, Mathf.Sign(localToCam.y), 0f);
            else if (az >= ax && az >= ay)
                localNormal = new Vector3(0f, 0f, Mathf.Sign(localToCam.z));
            else
                localNormal = new Vector3(Mathf.Sign(localToCam.x), 0f, 0f);

            // Map the plane's +Y (mesh normal) onto the image's surface normal. This keeps the
            // plane's local X/Z (width/height scale axes) in the image plane with correct aspect.
            Quaternion flatten = Quaternion.FromToRotation(Vector3.up, localNormal);
            // Spin in-plane about the normal so the texture's printed orientation matches the marker.
            Quaternion spin = Quaternion.AngleAxis(overlayTextureSpinDegrees, localNormal);
            return spin * flatten;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // #region agent diag
        private float lastAxisDiagTime = -999f;
        private void LogTrackedImageAxisDiagnostic(ARTrackedImage trackedImage)
        {
            if (Time.time - lastAxisDiagTime < 1.0f) return;
            lastAxisDiagTime = Time.time;

            var t = trackedImage.transform;
            Camera cam = Camera.main;
            Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
            Vector3 toCam = (camPos - t.position).normalized;

            float dotRight = Vector3.Dot(t.right, toCam);    // local +X (expected width axis)
            float dotUp = Vector3.Dot(t.up, toCam);          // local +Y
            float dotFwd = Vector3.Dot(t.forward, toCam);    // local +Z

            // The local axis whose |dot with toCam| is largest is the surface normal (perpendicular to wall).
            string normalAxis;
            float ar = Mathf.Abs(dotRight), au = Mathf.Abs(dotUp), af = Mathf.Abs(dotFwd);
            if (au >= ar && au >= af) normalAxis = dotUp >= 0 ? "+Y(up)" : "-Y(up)";
            else if (af >= ar && af >= au) normalAxis = dotFwd >= 0 ? "+Z(forward)" : "-Z(forward)";
            else normalAxis = dotRight >= 0 ? "+X(right)" : "-X(right)";

            Vector3 appliedEuler = referenceImageOverlay != null
                ? referenceImageOverlay.transform.localRotation.eulerAngles
                : Vector3.zero;

            Debug.Log($"[AXIS-DIAG] '{trackedImage.referenceImage.name}' size={trackedImage.size} | " +
                      $"dot(right/X,toCam)={dotRight:F2} dot(up/Y,toCam)={dotUp:F2} dot(fwd/Z,toCam)={dotFwd:F2} | " +
                      $"SURFACE NORMAL = local {normalAxis} | autoOrient={autoOrientOverlayToSurface} appliedLocalEuler={appliedEuler}");
        }
        // #endregion
#endif

        private void HideReferenceImageOverlay()
        {
            if (referenceImageOverlay != null)
            {
                referenceImageOverlay.SetActive(false);
            }

            if (detectionBoundsLine != null)
            {
                detectionBoundsLine.gameObject.SetActive(false);
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
            if (detectionBoundsLine != null)
            {
                Destroy(detectionBoundsLine.gameObject);
            }

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