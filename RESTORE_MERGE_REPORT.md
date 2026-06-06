# Restore merge report

Restored the recent June 2026 app files from `_Recovered/` into the live Unity project.

- Copied new files: 66
- Overwritten files: 0
- Missing from `_Recovered/`: 2
- Skipped duplicate Move AI files: 3

## Skipped duplicate Move AI files

These recovered files were intentionally not copied because the live project already has newer top-level versions at `Assets/Scripts/MoveAIFusion*.cs`. Copying these would create duplicate classes.

- `Assets/Scripts/MoveAI/MoveAIFusionAsset.cs`
- `Assets/Scripts/MoveAI/MoveAIFusionBaker.cs`
- `Assets/Scripts/MoveAI/MoveAIFusionCoordinator.cs`

## Copied files

- `Assets/3rdParty/AVProMovieCapture/Editor/Scripts/PostProcessBuild_iOS.cs`
- `Assets/AI/BlazePose/.setup_pending`
- `Assets/AI/BlazePose/IMPORT_MODELS.txt`
- `Assets/Plugins/iOS/TendorVideoPhotosExport.mm`
- `Assets/Plugins/iOS/TendorVideoPhotosExport.mm.meta`
- `Assets/Resources/ComputeShaders/ImageTransform.compute`
- `Assets/Scenes/ImageTargetTestOnly.unity`
- `Assets/Scenes/ImageTargetTestOnly.unity.meta`
- `Assets/Scenes/NewVersion.unity`
- `Assets/Scripts/AI/BlazePose2DOverlay.cs`
- `Assets/Scripts/AI/BlazePose3DVisualizer.cs`
- `Assets/Scripts/AI/BlazePoseDepthLift.cs`
- `Assets/Scripts/AI/BlazePoseOrientation.cs`
- `Assets/Scripts/AI/BlazePoseRunner.cs`
- `Assets/Scripts/AI/BlazePoseSkeleton.cs`
- `Assets/Scripts/AI/BlazeUtils.cs`
- `Assets/Scripts/AR/ARImageTargetManager.cs`
- `Assets/Scripts/AR/ARTrackedImageDebugQuad.cs`
- `Assets/Scripts/AR/ARTrackedImageDebugQuad.cs.meta`
- `Assets/Scripts/AR/ARWorldMapPersistence.cs`
- `Assets/Scripts/AR/ARWorldMapPersistence.cs.meta`
- `Assets/Scripts/Animation/CharacterSwitcher.cs`
- `Assets/Scripts/Animation/FBXCharacterController.cs`
- `Assets/Scripts/BodyTrackingController.cs`
- `Assets/Scripts/Data/PoseData.cs`
- `Assets/Scripts/Debug/DebugSessionLog.cs`
- `Assets/Scripts/Editor/AROcclusionSetup.cs`
- `Assets/Scripts/Editor/AppIconSetup.cs`
- `Assets/Scripts/Editor/AvaturnMaterialBinder.cs`
- `Assets/Scripts/Editor/BlazePoseSceneSetup.cs`
- `Assets/Scripts/Editor/BodyTrackingUIRebuilder.cs`
- `Assets/Scripts/Editor/CharacterFbxSetupUtility.cs`
- `Assets/Scripts/Editor/CharacterFbxSetupWindow.cs`
- `Assets/Scripts/Editor/CharacterSwitcherSetup.cs`
- `Assets/Scripts/Editor/GymLightingSetup.cs`
- `Assets/Scripts/Editor/ImmersalSetupTool.cs`
- `Assets/Scripts/Editor/IosAvProFrameworkEmbedFix.cs`
- `Assets/Scripts/Editor/IosPhotosFrameworkLinker.cs`
- `Assets/Scripts/Editor/MoveAIFusionSceneSetup.cs`
- `Assets/Scripts/Editor/MoveAITestTools.cs`
- `Assets/Scripts/Editor/SceneValidationTool.cs`
- `Assets/Scripts/Editor/_BlazePoseCleanup.cs`
- `Assets/Scripts/MoveAI/MoveJointMap.cs`
- `Assets/Scripts/Playback/BodyTrackingPlayer.cs`
- `Assets/Scripts/Playback/FusedCharacterPlayer.cs`
- `Assets/Scripts/Playback/PlaybackCompareVisualizer.cs`
- `Assets/Scripts/Recording/ARKitBodyPoseSource.cs`
- `Assets/Scripts/Recording/BodyTrackingRecorder.cs`
- `Assets/Scripts/Recording/IBodyPoseSource.cs`
- `Assets/Scripts/Recording/RecordingPostProcessor.cs`
- `Assets/Scripts/Spatial/IRouteRootProvider.cs`
- `Assets/Scripts/Spatial/ImageTargetRouteRootProvider.cs`
- `Assets/Scripts/Spatial/ImmersalDelayedInitializer.cs`
- `Assets/Scripts/Spatial/ImmersalMapSwitcher.cs`
- `Assets/Scripts/Spatial/ImmersalRouteRootProvider.cs`
- `Assets/Scripts/Spatial/RouteRootManager.cs`
- `Assets/Scripts/Storage/RecordingStorage.cs`
- `Assets/Scripts/Tests/Editor/PoseDataTests.cs`
- `Assets/Scripts/UI/BodyTrackingUI.cs`
- `Assets/Scripts/UI/DebugVisualsController.cs`
- `Assets/Scripts/UI/UIFactory.cs`
- `Assets/Scripts/UI/UISafeArea.cs`
- `Assets/Scripts/UI/UITokens.cs`
- `Assets/Scripts/Utils/DebugVisualizationMaterials.cs`
- `Assets/Scripts/Utils/VideoPhotosExport.cs`
- `Assets/XR/XRGeneralSettings.asset`

## Missing from recovery

- `Assets/AI/BlazePose/pose_detection.onnx.meta`
- `Assets/AI/BlazePose/pose_landmarks_detector_heavy.onnx.meta`
