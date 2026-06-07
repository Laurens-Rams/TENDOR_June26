#import <UIKit/UIKit.h>

static void TendorImpact(UIImpactFeedbackStyle style)
{
    if (@available(iOS 10.0, *))
    {
        UIImpactFeedbackGenerator* generator = [[UIImpactFeedbackGenerator alloc] initWithStyle:style];
        [generator prepare];
        [generator impactOccurred];
    }
}

extern "C" void TendorHapticLight()
{
    TendorImpact(UIImpactFeedbackStyleLight);
}

extern "C" void TendorHapticMedium()
{
    TendorImpact(UIImpactFeedbackStyleMedium);
}

extern "C" void TendorHapticSuccess()
{
    if (@available(iOS 10.0, *))
    {
        UINotificationFeedbackGenerator* generator = [[UINotificationFeedbackGenerator alloc] init];
        [generator prepare];
        [generator notificationOccurred:UINotificationFeedbackTypeSuccess];
    }
}
