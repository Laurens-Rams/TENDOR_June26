using BodyTracking.Spatial;
using Immersal;
using Immersal.REST;
using Immersal.XR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.XR.ARFoundation;
using Unity.XR.CoreUtils;

namespace BodyTracking.Editor
{
    /// <summary>
    /// Scene setup and login helpers for the Immersal SDK. The built-in "Immersal SDK/Login" menu
    /// fails when no ImmersalSDK object exists in the scene (NullReferenceException). These tools add
    /// the required scene objects first and provide a login flow that works without that prerequisite.
    /// </summary>
    public class ImmersalSetupTool : EditorWindow
    {
        private const string DefaultServer = "https://api.immersal.com";
        private const string ImmersalPrefabPath = "Prefabs/ImmersalSDK";

        private string email = "";
        private string password = "";
        private string token = "";
        private string statusMessage = "";
        private Vector2 scroll;
        private UnityWebRequest pendingLoginRequest;

        [MenuItem("TENDOR/Immersal/Setup Scene")]
        public static void SetupScene()
        {
            var sdk = EnsureImmersalSdkInScene();
            if (sdk == null)
            {
                EditorUtility.DisplayDialog(
                    "Immersal Setup",
                    "Could not add ImmersalSDK.\n\nMake sure the com.immersal.core package is installed, then try again.",
                    "OK");
                return;
            }

            EnsureXrSpaceInScene();
            EnsureRouteRootProvider();

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Selection.activeGameObject = sdk.gameObject;

            Debug.Log("[ImmersalSetupTool] Immersal SDK added to scene.");
            EditorUtility.DisplayDialog(
                "Immersal Setup",
                "ImmersalSDK and XR Space were added to the scene.\n\n" +
                "Next: TENDOR > Immersal > Login",
                "OK");
        }

        [MenuItem("TENDOR/Immersal/Login")]
        public static void ShowLoginWindow()
        {
            GetWindow<ImmersalSetupTool>("Immersal Login");
        }

        void OnEnable()
        {
            var sdk = Object.FindFirstObjectByType<ImmersalSDK>();
            if (sdk != null && !string.IsNullOrEmpty(sdk.developerToken))
                token = sdk.developerToken;
            else if (PlayerPrefs.HasKey("token"))
                token = PlayerPrefs.GetString("token");
        }

        void OnDisable()
        {
            EditorApplication.update -= PollLoginRequest;
            pendingLoginRequest?.Dispose();
            pendingLoginRequest = null;
        }

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            EditorGUILayout.HelpBox(
                "Log in with your Immersal account, or paste a developer token.\n\n" +
                "Run Setup Scene first if ImmersalSDK is not in the Hierarchy yet.",
                MessageType.Info);

            if (!string.IsNullOrEmpty(statusMessage))
                EditorGUILayout.HelpBox(statusMessage, MessageType.None);

            GUILayout.Label("Account login", EditorStyles.boldLabel);
            email = EditorGUILayout.TextField("Email", email);
            password = EditorGUILayout.PasswordField("Password", password);

            EditorGUILayout.BeginHorizontal();
            GUI.enabled = pendingLoginRequest == null;
            if (GUILayout.Button("Login"))
                BeginLogin(new SDKLoginRequest { login = email, password = password });
            GUI.enabled = true;

            if (GUILayout.Button("Setup Scene"))
                SetupScene();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(12);
            GUILayout.Label("Developer token", EditorStyles.boldLabel);
            token = EditorGUILayout.TextField("Token", token);

            if (GUILayout.Button("Apply Token to Scene"))
                ApplyToken(token);

            EditorGUILayout.Space(8);
            ValidateSceneState();

            EditorGUILayout.EndScrollView();
        }

        private void ValidateSceneState()
        {
            GUILayout.Label("Scene status", EditorStyles.boldLabel);

            var sdk = Object.FindFirstObjectByType<ImmersalSDK>();
            DrawStatus("ImmersalSDK in scene", sdk != null);
            DrawStatus("Developer token set", sdk != null && !string.IsNullOrEmpty(sdk.developerToken));
            DrawStatus("XR Space", Object.FindFirstObjectByType<XRSpace>() != null);
            DrawStatus("XR Map(s)", Object.FindObjectsByType<XRMap>(FindObjectsSortMode.None).Length > 0);
            DrawStatus("RouteRoot provider", Object.FindFirstObjectByType<ImmersalRouteRootProvider>() != null);
            DrawStatus("AR Session", Object.FindFirstObjectByType<ARSession>() != null);
            DrawStatus("XR Origin", Object.FindFirstObjectByType<XROrigin>() != null);
        }

        private static void DrawStatus(string label, bool ok)
        {
            EditorGUILayout.LabelField(ok ? $"✅ {label}" : $"❌ {label}");
        }

        private void BeginLogin(SDKLoginRequest loginRequest)
        {
            statusMessage = "Logging in…";
            Repaint();

            string server = GetServerUrl();
            string jsonString = JsonUtility.ToJson(loginRequest);

            pendingLoginRequest = UnityWebRequest.Put($"{server}/{SDKLoginRequest.endpoint}", jsonString);
            pendingLoginRequest.method = UnityWebRequest.kHttpVerbPOST;
            pendingLoginRequest.useHttpContinue = false;
            pendingLoginRequest.SetRequestHeader("Content-Type", "application/json");
            pendingLoginRequest.SetRequestHeader("Accept", "application/json");
            pendingLoginRequest.SendWebRequest();

            EditorApplication.update -= PollLoginRequest;
            EditorApplication.update += PollLoginRequest;
        }

        private void PollLoginRequest()
        {
            if (pendingLoginRequest == null || !pendingLoginRequest.isDone)
                return;

            EditorApplication.update -= PollLoginRequest;

            var request = pendingLoginRequest;
            pendingLoginRequest = null;

#if UNITY_2020_1_OR_NEWER
                if (request.result != UnityWebRequest.Result.Success)
#else
                if (request.isNetworkError || request.isHttpError)
#endif
                {
                    statusMessage = $"Login failed: {request.error}\n{request.downloadHandler.text}";
                    Debug.LogError(statusMessage);
                }
                else
                {
                    var loginResult = JsonUtility.FromJson<SDKLoginResult>(request.downloadHandler.text);
                    if (loginResult.error == "none" && !string.IsNullOrEmpty(loginResult.token))
                    {
                        token = loginResult.token;
                        ApplyToken(token);
                        statusMessage = "Login successful. Token applied to ImmersalSDK in scene.";
                        Debug.Log("[ImmersalSetupTool] Login successful.");
                    }
                    else
                    {
                        statusMessage = $"Login rejected: {loginResult.error}";
                        Debug.LogError(statusMessage);
                }
            }

            request.Dispose();
            Repaint();
        }

        private static string GetServerUrl()
        {
            var sdk = Object.FindFirstObjectByType<ImmersalSDK>();
            return sdk != null ? sdk.localizationServer : DefaultServer;
        }

        private static void ApplyToken(string developerToken)
        {
            if (string.IsNullOrWhiteSpace(developerToken))
            {
                EditorUtility.DisplayDialog("Immersal Login", "Token is empty.", "OK");
                return;
            }

            var sdk = EnsureImmersalSdkInScene();
            if (sdk == null) return;

            sdk.developerToken = developerToken.Trim();
            PlayerPrefs.SetString("token", sdk.developerToken);
            PlayerPrefs.Save();

            EditorUtility.SetDirty(sdk);
            PrefabUtility.RecordPrefabInstancePropertyModifications(sdk);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

            Debug.Log("[ImmersalSetupTool] Developer token applied.");
        }

        private static ImmersalSDK EnsureImmersalSdkInScene()
        {
            var existing = Object.FindFirstObjectByType<ImmersalSDK>();
            if (existing != null)
                return existing;

            var prefab = Resources.Load<GameObject>(ImmersalPrefabPath);
            if (prefab == null)
            {
                Debug.LogError("[ImmersalSetupTool] Could not load ImmersalSDK prefab from Resources/Prefabs/ImmersalSDK.");
                return null;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = "ImmersalSDK";
            Undo.RegisterCreatedObjectUndo(instance, "Add ImmersalSDK");
            return instance.GetComponent<ImmersalSDK>();
        }

        private static void EnsureXrSpaceInScene()
        {
            if (Object.FindFirstObjectByType<XRSpace>() != null)
                return;

            var go = new GameObject("XR Space");
            go.AddComponent<XRSpace>();
            Undo.RegisterCreatedObjectUndo(go, "Add XR Space");
        }

        private static void EnsureRouteRootProvider()
        {
            if (Object.FindFirstObjectByType<ImmersalRouteRootProvider>() != null)
                return;

            var controller = Object.FindFirstObjectByType<BodyTrackingController>();
            var host = controller != null ? controller.gameObject : Object.FindFirstObjectByType<RouteRootManager>()?.gameObject;
            if (host == null)
                host = new GameObject("SpatialSystems");

            if (host.GetComponent<RouteRootManager>() == null)
                host.AddComponent<RouteRootManager>();

            if (host.GetComponent<ImmersalRouteRootProvider>() == null)
                host.AddComponent<ImmersalRouteRootProvider>();
        }
    }
}
