using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace TENDOR.AR
{
    /// <summary>
    /// Minimal debug visual: one translucent quad per tracked image, sized from <see cref="ARTrackedImage.size"/>.
    /// Intended for isolated image-tracking tests (no planes / room mesh).
    /// </summary>
    [RequireComponent(typeof(ARTrackedImageManager))]
    public sealed class ARTrackedImageDebugQuad : MonoBehaviour
    {
        [SerializeField] Color quadColor = new(0f, 0.85f, 0.2f, 0.35f);

        ARTrackedImageManager m_ImageManager;
        Material m_Material;
        readonly Dictionary<TrackableId, GameObject> m_Quads = new();
        readonly Dictionary<TrackableId, Vector2> m_LastSize = new();

        void Awake()
        {
            m_ImageManager = GetComponent<ARTrackedImageManager>();
        }

        void OnEnable()
        {
            if (m_ImageManager == null)
                m_ImageManager = GetComponent<ARTrackedImageManager>();

            var shader = Shader.Find("Unlit/Color");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");

            m_Material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            m_Material.color = quadColor;

            m_ImageManager.trackablesChanged.AddListener(OnTrackedImagesChanged);
        }

        void OnDisable()
        {
            m_ImageManager.trackablesChanged.RemoveListener(OnTrackedImagesChanged);
            foreach (var kv in m_Quads)
                Destroy(kv.Value);
            m_Quads.Clear();

            if (m_Material != null)
                Destroy(m_Material);
        }

        void OnTrackedImagesChanged(ARTrackablesChangedEventArgs<ARTrackedImage> args)
        {
            foreach (var removed in args.removed)
                RemoveQuad(removed.Key);

            foreach (var added in args.added)
                UpdateOrCreateQuad(added);

            foreach (var updated in args.updated)
                UpdateOrCreateQuad(updated);
        }

        void RemoveQuad(TrackableId id)
        {
            if (!m_Quads.TryGetValue(id, out var go))
                return;

            var filter = go.GetComponent<MeshFilter>();
            if (filter != null && filter.sharedMesh != null)
                Destroy(filter.sharedMesh);

            Destroy(go);
            m_Quads.Remove(id);
            m_LastSize.Remove(id);
        }

        void UpdateOrCreateQuad(ARTrackedImage trackedImage)
        {
            if (trackedImage.trackingState == TrackingState.None)
            {
                RemoveQuad(trackedImage.trackableId);
                return;
            }

            var size = trackedImage.size;
            if (size.x <= 1e-4f || size.y <= 1e-4f)
                return;

            if (!m_Quads.TryGetValue(trackedImage.trackableId, out var quadGo))
            {
                quadGo = new GameObject("TrackedImageDebugQuad");
                quadGo.transform.SetParent(trackedImage.transform, false);
                quadGo.AddComponent<MeshFilter>().mesh = BuildQuadMesh(size.x, size.y);
                var renderer = quadGo.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = m_Material;
                m_Quads[trackedImage.trackableId] = quadGo;
                m_LastSize[trackedImage.trackableId] = size;
            }
            else if (!m_LastSize.TryGetValue(trackedImage.trackableId, out var last) ||
                     Mathf.Abs(last.x - size.x) > 1e-4f ||
                     Mathf.Abs(last.y - size.y) > 1e-4f)
            {
                var filter = quadGo.GetComponent<MeshFilter>();
                if (filter.sharedMesh != null)
                    Destroy(filter.sharedMesh);

                filter.mesh = BuildQuadMesh(size.x, size.y);
                m_LastSize[trackedImage.trackableId] = size;
            }

            quadGo.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            quadGo.transform.localScale = Vector3.one;
        }

        static Mesh BuildQuadMesh(float widthMeters, float heightMeters)
        {
            var hx = widthMeters * 0.5f;
            var hy = heightMeters * 0.5f;
            var mesh = new Mesh
            {
                name = "ARTrackedImageDebugQuadMesh"
            };
            mesh.vertices = new[]
            {
                new Vector3(-hx, -hy, 0f),
                new Vector3(-hx, hy, 0f),
                new Vector3(hx, hy, 0f),
                new Vector3(hx, -hy, 0f)
            };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(1f, 0f)
            };
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return mesh;
        }
    }
}
