using BodyTracking.Utils;
using UnityEngine;

namespace BodyTracking.AI
{
    /// <summary>
    /// Stage 2 gate visualization: renders the depth-lifted BlazePose joints as world-space spheres + bone
    /// lines so they can be compared against the real person / live ARKit skeleton. Reuses
    /// <see cref="DebugVisualizationMaterials"/> for URP-compatible materials.
    /// </summary>
    public class BlazePose3DVisualizer : MonoBehaviour
    {
        [SerializeField] BlazePoseDepthLift depthLift;
        [SerializeField, Min(0.005f)] float jointRadius = 0.04f;
        [SerializeField, Min(0.001f)] float boneWidth = 0.02f;
        [SerializeField] Color depthColor = new Color(0.1f, 1f, 0.4f, 1f);
        [SerializeField] Color fallbackColor = new Color(1f, 0.6f, 0.1f, 1f);
        [SerializeField] Color boneColor = new Color(1f, 1f, 1f, 0.85f);

        Transform[] m_Joints;
        Renderer[] m_JointRenderers;
        LineRenderer[] m_Bones;
        Material m_DepthMat;
        Material m_FallbackMat;
        Material m_BoneMat;
        Transform m_Root;

        void Awake()
        {
            if (depthLift == null)
                depthLift = FindAnyObjectByType<BlazePoseDepthLift>();

            m_DepthMat = DebugVisualizationMaterials.CreateSolidColorMaterial(depthColor);
            m_FallbackMat = DebugVisualizationMaterials.CreateSolidColorMaterial(fallbackColor);
            m_BoneMat = DebugVisualizationMaterials.CreateLineMaterial(boneColor);

            BuildVisuals();
        }

        void OnEnable()
        {
            if (depthLift != null)
                depthLift.OnWorldPose += HandleWorldPose;
        }

        void OnDisable()
        {
            if (depthLift != null)
                depthLift.OnWorldPose -= HandleWorldPose;
        }

        void BuildVisuals()
        {
            var rootGo = new GameObject("BlazePose3D");
            rootGo.transform.SetParent(transform, false);
            m_Root = rootGo.transform;

            int n = BlazePoseSkeleton.NumKeypoints;
            m_Joints = new Transform[n];
            m_JointRenderers = new Renderer[n];
            m_Bones = new LineRenderer[n];

            for (int i = 0; i < n; i++)
            {
                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = $"Joint_{i}";
                var col = sphere.GetComponent<Collider>();
                if (col != null)
                    Destroy(col);
                sphere.transform.SetParent(m_Root, false);
                sphere.transform.localScale = Vector3.one * (jointRadius * 2f);
                var rend = sphere.GetComponent<Renderer>();
                if (m_DepthMat != null)
                    rend.sharedMaterial = m_DepthMat;
                m_Joints[i] = sphere.transform;
                m_JointRenderers[i] = rend;

                if (BlazePoseSkeleton.Parents[i] >= 0)
                {
                    var boneGo = new GameObject($"Bone_{i}");
                    boneGo.transform.SetParent(m_Root, false);
                    var lr = boneGo.AddComponent<LineRenderer>();
                    lr.useWorldSpace = true;
                    lr.positionCount = 2;
                    lr.startWidth = boneWidth;
                    lr.endWidth = boneWidth;
                    if (m_BoneMat != null)
                        lr.sharedMaterial = m_BoneMat;
                    m_Bones[i] = lr;
                }
            }

            SetActive(false);
        }

        void HandleWorldPose(BlazePoseWorldResult world)
        {
            if (m_Root == null)
                return;
            if (world == null || !world.valid)
            {
                SetActive(false);
                return;
            }

            int n = BlazePoseSkeleton.NumKeypoints;
            for (int i = 0; i < n; i++)
            {
                var joint = world.joints[i];
                var t = m_Joints[i];
                t.gameObject.SetActive(joint.tracked);
                if (joint.tracked)
                {
                    t.position = joint.worldPosition;
                    if (m_JointRenderers[i] != null)
                        m_JointRenderers[i].sharedMaterial = joint.hasDepth ? m_DepthMat : m_FallbackMat;
                }

                var bone = m_Bones[i];
                if (bone == null)
                    continue;
                int parent = BlazePoseSkeleton.Parents[i];
                bool show = joint.tracked && world.joints[parent].tracked;
                bone.gameObject.SetActive(show);
                if (show)
                {
                    bone.SetPosition(0, joint.worldPosition);
                    bone.SetPosition(1, world.joints[parent].worldPosition);
                }
            }
        }

        void SetActive(bool active)
        {
            if (m_Joints != null)
                foreach (var j in m_Joints)
                    if (j != null) j.gameObject.SetActive(active);
            if (m_Bones != null)
                foreach (var b in m_Bones)
                    if (b != null) b.gameObject.SetActive(active);
        }
    }
}
