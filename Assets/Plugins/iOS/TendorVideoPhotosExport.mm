#import <Foundation/Foundation.h>
#import <Photos/Photos.h>

typedef void (*TendorSaveVideoCallback)(int success);

static void InvokeCallback(TendorSaveVideoCallback callback, BOOL success)
{
    if (callback != NULL)
        callback(success ? 1 : 0);
}

extern "C" void TendorSaveVideoToPhotos(const char* path, TendorSaveVideoCallback callback)
{
    if (path == NULL || strlen(path) == 0)
    {
        InvokeCallback(callback, NO);
        return;
    }

    NSString* videoPath = [NSString stringWithUTF8String:path];
    NSURL* fileURL = [NSURL fileURLWithPath:videoPath];

    if (![[NSFileManager defaultManager] fileExistsAtPath:videoPath])
    {
        InvokeCallback(callback, NO);
        return;
    }

    void (^saveBlock)(void) = ^{
        [[PHPhotoLibrary sharedPhotoLibrary] performChanges:^{
            [PHAssetCreationRequest creationRequestForAssetFromVideoAtFileURL:fileURL];
        } completionHandler:^(BOOL success, NSError* _Nullable error) {
            InvokeCallback(callback, success);
        }];
    };

    if (@available(iOS 14, *))
    {
        [PHPhotoLibrary requestAuthorizationForAccessLevel:PHAccessLevelAddOnly
                                                   handler:^(PHAuthorizationStatus status) {
            if (status == PHAuthorizationStatusAuthorized || status == PHAuthorizationStatusLimited)
                saveBlock();
            else
                InvokeCallback(callback, NO);
        }];
    }
    else
    {
        [PHPhotoLibrary requestAuthorization:^(PHAuthorizationStatus status) {
            if (status == PHAuthorizationStatusAuthorized)
                saveBlock();
            else
                InvokeCallback(callback, NO);
        }];
    }
}
