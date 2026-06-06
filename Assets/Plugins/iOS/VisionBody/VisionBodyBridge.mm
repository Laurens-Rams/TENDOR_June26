// Native Apple Vision 3D body-pose bridge for the Unity C# P/Invoke surface in
// Assets/Scripts/Vision/VisionBodyNative.cs.
//
// Implements VisionBody_Initialize / VisionBody_Detect / VisionBody_Shutdown using
// VNDetectHumanBodyPose3DRequest (iOS 17+). Joint positions are returned in
// camera-relative meters using Vision's coordinate convention (+x right, +y up,
// camera looking down -z); the managed side applies any axis inversion.
//
// The 17-joint output order MUST match BodyTracking.Vision.VisionBodySkeleton.

#import <Foundation/Foundation.h>
#import <Vision/Vision.h>
#import <CoreVideo/CoreVideo.h>
#import <simd/simd.h>

#if !defined(__IPHONE_17_0)
// Building against an SDK that predates the Vision 3D body-pose API: compile the
// functions as unavailable stubs so the symbols still resolve at link time.
extern "C" {
    bool VisionBody_Initialize(void) { return false; }
    bool VisionBody_Detect(const uint8_t *, int, int, int, int,
                           float *, float *, int *outJointCount, float *outBodyHeight) {
        if (outJointCount)  *outJointCount = 0;
        if (outBodyHeight)  *outBodyHeight = 0.0f;
        return false;
    }
    void VisionBody_Shutdown(void) {}
}
#else

static const int kJointCount = 17;

// Order must match VisionBodySkeleton (Root, LeftHip, LeftKnee, LeftAnkle, RightHip,
// RightKnee, RightAnkle, Spine, CenterShoulder, LeftShoulder, LeftElbow, LeftWrist,
// RightShoulder, RightElbow, RightWrist, CenterHead, TopHead).
API_AVAILABLE(ios(17.0))
static NSArray<VNHumanBodyPose3DObservationJointName> *VisionBodyJointOrder(void) {
    static NSArray<VNHumanBodyPose3DObservationJointName> *order = nil;
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        order = @[
            VNHumanBodyPose3DObservationJointNameRoot,           // 0
            VNHumanBodyPose3DObservationJointNameLeftHip,        // 1
            VNHumanBodyPose3DObservationJointNameLeftKnee,       // 2
            VNHumanBodyPose3DObservationJointNameLeftAnkle,      // 3
            VNHumanBodyPose3DObservationJointNameRightHip,       // 4
            VNHumanBodyPose3DObservationJointNameRightKnee,      // 5
            VNHumanBodyPose3DObservationJointNameRightAnkle,     // 6
            VNHumanBodyPose3DObservationJointNameSpine,          // 7
            VNHumanBodyPose3DObservationJointNameCenterShoulder, // 8
            VNHumanBodyPose3DObservationJointNameLeftShoulder,   // 9
            VNHumanBodyPose3DObservationJointNameLeftElbow,      // 10
            VNHumanBodyPose3DObservationJointNameLeftWrist,      // 11
            VNHumanBodyPose3DObservationJointNameRightShoulder,  // 12
            VNHumanBodyPose3DObservationJointNameRightElbow,     // 13
            VNHumanBodyPose3DObservationJointNameRightWrist,     // 14
            VNHumanBodyPose3DObservationJointNameCenterHead,     // 15
            VNHumanBodyPose3DObservationJointNameTopHead,        // 16
        ];
    });
    return order;
}

extern "C" bool VisionBody_Initialize(void) {
    if (@available(iOS 17.0, *)) {
        return true;
    }
    return false;
}

extern "C" void VisionBody_Shutdown(void) {
    // Stateless: a fresh request and image handler are created per detection,
    // so there is nothing persistent to tear down.
}

extern "C" bool VisionBody_Detect(const uint8_t *bgra, int width, int height, int bytesPerRow,
                                  int orientation,
                                  float *outPositions, float *outConfidences,
                                  int *outJointCount, float *outBodyHeight) {
    if (outJointCount)  *outJointCount = 0;
    if (outBodyHeight)  *outBodyHeight = 0.0f;

    if (!bgra || width <= 0 || height <= 0 || bytesPerRow <= 0 ||
        !outPositions || !outConfidences) {
        return false;
    }

    if (@available(iOS 17.0, *)) {
        @autoreleasepool {
            // Wrap the managed BGRA buffer in a CVPixelBuffer without copying. The buffer
            // is pinned on the managed side for the duration of this synchronous call.
            CVPixelBufferRef pixelBuffer = NULL;
            CVReturn status = CVPixelBufferCreateWithBytes(
                kCFAllocatorDefault, (size_t)width, (size_t)height,
                kCVPixelFormatType_32BGRA, (void *)bgra, (size_t)bytesPerRow,
                NULL, NULL, NULL, &pixelBuffer);
            if (status != kCVReturnSuccess || pixelBuffer == NULL) {
                return false;
            }

            VNDetectHumanBodyPose3DRequest *request =
                [[VNDetectHumanBodyPose3DRequest alloc] init];

            VNImageRequestHandler *handler = [[VNImageRequestHandler alloc]
                initWithCVPixelBuffer:pixelBuffer
                          orientation:(CGImagePropertyOrientation)orientation
                              options:@{}];

            NSError *error = nil;
            BOOL performed = [handler performRequests:@[request] error:&error];

            bool detected = false;
            if (performed && request.results.count > 0) {
                VNHumanBodyPose3DObservation *observation =
                    (VNHumanBodyPose3DObservation *)request.results.firstObject;

                if (outBodyHeight) {
                    *outBodyHeight = observation.bodyHeight;
                }

                NSArray<VNHumanBodyPose3DObservationJointName> *order = VisionBodyJointOrder();
                int tracked = 0;
                for (int i = 0; i < kJointCount; i++) {
                    float x = 0.0f, y = 0.0f, z = 0.0f, conf = 0.0f;

                    simd_float4x4 cameraRelative;
                    NSError *jointError = nil;
                    BOOL ok = [observation getCameraRelativePosition:&cameraRelative
                                                        forJointName:order[i]
                                                               error:&jointError];
                    if (ok) {
                        // Translation lives in the 4th column of the transform.
                        x = cameraRelative.columns[3].x;
                        y = cameraRelative.columns[3].y;
                        z = cameraRelative.columns[3].z;
                        conf = 1.0f;
                        tracked++;
                    }

                    outPositions[i * 3 + 0] = x;
                    outPositions[i * 3 + 1] = y;
                    outPositions[i * 3 + 2] = z;
                    outConfidences[i] = conf;
                }

                if (outJointCount) {
                    *outJointCount = kJointCount;
                }
                detected = tracked > 0;
            }

            CVPixelBufferRelease(pixelBuffer);
            return detected;
        }
    }

    return false;
}

#endif // __IPHONE_17_0
