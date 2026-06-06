using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

#if UNITY_IOS
using UnityEngine.XR.ARKit;
#endif

namespace BodyTracking.AR
{
    [Serializable]
    public class WorldMapManifest
    {
        public string recordingFile;
        public string worldMapFile;
        public string imageTargetName;
        public string anchorId;
        public Vector3 rootOffset;
        public float rootYaw;
        public string createdAtUtc;
    }

    /// <summary>
    /// iOS/ARKit-only local ARWorldMap persistence.
    /// Saves/loads world maps per recording id and stores a small manifest.
    /// </summary>
    public class ARWorldMapPersistence : MonoBehaviour
    {
        const string RecordingsFolder = "BodyTrackingRecordings";
        const string WorldMapExtension = ".arexperience";
        const string ManifestSuffix = "_manifest.json";

        [SerializeField] ARSession arSession;
        [SerializeField] float relocalizationWarmupSeconds = 1.0f;
        [SerializeField] float maxRelocalizationSeconds = 15f;

        GameObject worldMapPlaybackAnchor;

        public bool IsRelocalized { get; private set; }
        public string LastLoadedRecordingId { get; private set; }
        public string LastStatusMessage { get; private set; } = "WorldMap idle";

        public event Action<string> OnStatusChanged;
        public event Action<string> OnWorldMapLoaded;
        public event Action<string> OnWorldMapSaved;
        public event Action<string> OnWorldMapFailed;

        string RecordingsPath => Path.Combine(Application.persistentDataPath, RecordingsFolder);

        public bool IsWorldMapSupported()
        {
#if UNITY_IOS
            if (arSession == null)
                arSession = UnityEngine.Object.FindAnyObjectByType<ARSession>();
            return arSession != null &&
                   arSession.subsystem is ARKitSessionSubsystem &&
                   ARKitSessionSubsystem.worldMapSupported;
#else
            return false;
#endif
        }

        public bool HasWorldMap(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return false;
            return File.Exists(GetWorldMapPath(recordingId));
        }

        public Transform GetOrCreatePlaybackAnchor(Vector3 pos, Quaternion rot, Vector3 scale)
        {
            if (worldMapPlaybackAnchor == null)
            {
                worldMapPlaybackAnchor = new GameObject("WorldMapPlaybackAnchor");
            }

            worldMapPlaybackAnchor.transform.SetPositionAndRotation(pos, rot);
            // Keep a rigid anchor. Recorded/trackable scale can differ per run and shrink/expand replay.
            worldMapPlaybackAnchor.transform.localScale = Vector3.one;
            return worldMapPlaybackAnchor.transform;
        }

        public IEnumerator SaveWorldMapForRecording(string recordingId, string imageTargetName)
        {
            if (!IsWorldMapSupported())
            {
                SetStatus("WorldMap save skipped (not supported on this platform/session).");
                yield break;
            }

#if UNITY_IOS
            var sessionSubsystem = arSession.subsystem as ARKitSessionSubsystem;
            if (sessionSubsystem == null)
            {
                Fail($"WorldMap save failed: ARKit session subsystem unavailable for '{recordingId}'.");
                yield break;
            }

            var request = sessionSubsystem.GetARWorldMapAsync();
            while (!request.status.IsDone())
                yield return null;

            if (request.status.IsError())
            {
                request.Dispose();
                Fail($"WorldMap save failed: request status {request.status}");
                yield break;
            }

            var worldMap = request.GetWorldMap();
            request.Dispose();

            try
            {
                Directory.CreateDirectory(RecordingsPath);
                var serialized = worldMap.Serialize(Allocator.Temp);
                File.WriteAllBytes(GetWorldMapPath(recordingId), serialized.ToArray());
                serialized.Dispose();
                worldMap.Dispose();

                SaveManifest(recordingId, imageTargetName);
                LastLoadedRecordingId = recordingId;
                IsRelocalized = false;
                SucceedSave($"WorldMap saved for '{recordingId}'");
            }
            catch (Exception e)
            {
                Fail($"WorldMap save failed for '{recordingId}': {e.Message}");
            }
#else
            yield break;
#endif
        }

        public IEnumerator LoadWorldMapForRecording(string recordingId)
        {
            if (!IsWorldMapSupported())
            {
                SetStatus("WorldMap load skipped (not supported on this platform/session).");
                yield break;
            }

#if UNITY_IOS
            var worldMapPath = GetWorldMapPath(recordingId);
            if (!File.Exists(worldMapPath))
            {
                Fail($"No world map file found for '{recordingId}'");
                yield break;
            }

            var sessionSubsystem = arSession.subsystem as ARKitSessionSubsystem;
            if (sessionSubsystem == null)
            {
                Fail($"WorldMap load failed: ARKit session subsystem unavailable for '{recordingId}'.");
                yield break;
            }

            byte[] bytes = File.ReadAllBytes(worldMapPath);
            var data = new NativeArray<byte>(bytes, Allocator.Temp);
            if (!ARWorldMap.TryDeserialize(data, out var worldMap) || !worldMap.valid)
            {
                data.Dispose();
                Fail($"WorldMap load failed: invalid data for '{recordingId}'");
                yield break;
            }
            data.Dispose();

            bool applySucceeded = false;
            try
            {
                arSession.Reset();
                sessionSubsystem.ApplyWorldMap(worldMap);
                worldMap.Dispose();

                LastLoadedRecordingId = recordingId;
                IsRelocalized = false;
                SetStatus($"WorldMap applied for '{recordingId}', relocalizing...");
                applySucceeded = true;
            }
            catch (Exception e)
            {
                Fail($"WorldMap load/apply failed for '{recordingId}': {e.Message}");
            }

            if (!applySucceeded)
                yield break;

            float elapsed = 0f;
            while (elapsed < relocalizationWarmupSeconds)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            elapsed = 0f;
            while (elapsed < maxRelocalizationSeconds)
            {
                var mappingStatus = sessionSubsystem.worldMappingStatus;
                if (mappingStatus == ARWorldMappingStatus.Mapped)
                {
                    IsRelocalized = true;
                    SucceedLoad($"WorldMap relocalized for '{recordingId}' ({mappingStatus})");
                    yield break;
                }

                elapsed += Time.deltaTime;
                yield return null;
            }

            IsRelocalized = false;
            Fail($"WorldMap relocalization timed out for '{recordingId}' (last status: {sessionSubsystem.worldMappingStatus})");
#else
            yield break;
#endif
        }

        void SaveManifest(string recordingId, string imageTargetName)
        {
            var manifest = new WorldMapManifest
            {
                recordingFile = recordingId + ".json",
                worldMapFile = recordingId + WorldMapExtension,
                imageTargetName = imageTargetName,
                anchorId = "image_target_anchor",
                rootOffset = Vector3.zero,
                rootYaw = 0f,
                createdAtUtc = DateTime.UtcNow.ToString("o")
            };

            string json = JsonUtility.ToJson(manifest, true);
            File.WriteAllText(GetManifestPath(recordingId), json);
        }

        string GetWorldMapPath(string recordingId) => Path.Combine(RecordingsPath, recordingId + WorldMapExtension);
        string GetManifestPath(string recordingId) => Path.Combine(RecordingsPath, recordingId + ManifestSuffix);

        void SetStatus(string message)
        {
            LastStatusMessage = message;
            Debug.Log("[ARWorldMapPersistence] " + message);
            OnStatusChanged?.Invoke(message);
        }

        void SucceedSave(string message)
        {
            SetStatus(message);
            OnWorldMapSaved?.Invoke(message);
        }

        void SucceedLoad(string message)
        {
            SetStatus(message);
            OnWorldMapLoaded?.Invoke(message);
        }

        void Fail(string message)
        {
            SetStatus(message);
            OnWorldMapFailed?.Invoke(message);
        }
    }
}
