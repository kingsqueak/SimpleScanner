// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

// A software unplug and replug of a USB device, for a radio whose data pipe has jammed.
//
// Seen on SDRplay RSP1s on macOS (2026-11-25): after half an hour to two hours of scanning the
// radio sends one oversized packet ("babble error" on its bulk endpoint 0x81); macOS halts the
// endpoint and SDRplay's service never clears it, so no more samples come until the radio is
// replugged. Re-enumerating the device does the same as a replug. The service holds the radio
// for exclusive use while a program streams from it, so the stream must be stopped first.
#include <string.h>

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

#ifdef __APPLE__
#include <CoreFoundation/CoreFoundation.h>
#include <IOKit/IOKitLib.h>
#include <IOKit/IOCFPlugIn.h>
#include <IOKit/usb/IOUSBLib.h>

static int int_property(io_service_t svc, CFStringRef key)
{
    int value = -1;
    CFTypeRef v = IORegistryEntryCreateCFProperty(svc, key, kCFAllocatorDefault, 0);
    if (!v) return -1;
    if (CFGetTypeID(v) == CFNumberGetTypeID()) CFNumberGetValue((CFNumberRef)v, kCFNumberIntType, &value);
    CFRelease(v);
    return value;
}

static int serial_matches(io_service_t svc, const char *serial)
{
    if (!serial || !*serial) return 1;
    CFTypeRef v = IORegistryEntryCreateCFProperty(svc, CFSTR(kUSBSerialNumberString), kCFAllocatorDefault, 0);
    if (!v) return 0;
    char buf[128] = {0};
    int ok = CFGetTypeID(v) == CFStringGetTypeID()
        && CFStringGetCString((CFStringRef)v, buf, sizeof buf, kCFStringEncodingUTF8)
        && strcmp(buf, serial) == 0;
    CFRelease(v);
    return ok;
}

// Re-enumerates every device with this vendor id and (when given) serial number.
// Returns 0 when at least one was re-enumerated, 1 when none was found, else the first IOReturn error.
EXPORT int ss_usb_reenumerate(int vendor, const char *serial)
{
    // USB matching takes a vendor only together with a product, so every device is listed and sorted here.
    CFMutableDictionaryRef match = IOServiceMatching(kIOUSBDeviceClassName);
    if (!match) return -1;
    io_iterator_t it;
    if (IOServiceGetMatchingServices(kIOMainPortDefault, match, &it) != KERN_SUCCESS) return -1;
    int result = 1;
    io_service_t svc;
    while ((svc = IOIteratorNext(it)))
    {
        if (int_property(svc, CFSTR(kUSBVendorID)) != vendor || !serial_matches(svc, serial)) { IOObjectRelease(svc); continue; }
        IOCFPlugInInterface **plug = 0;
        SInt32 score;
        kern_return_t kr = IOCreatePlugInInterfaceForService(svc, kIOUSBDeviceUserClientTypeID, kIOCFPlugInInterfaceID, &plug, &score);
        IOObjectRelease(svc);
        if (kr != KERN_SUCCESS || !plug) { if (result == 1) result = kr ? kr : -1; continue; }
        IOUSBDeviceInterface650 **dev = 0;
        (*plug)->QueryInterface(plug, CFUUIDGetUUIDBytes(kIOUSBDeviceInterfaceID650), (LPVOID *)&dev);
        (*plug)->Release(plug);
        if (!dev) { if (result == 1) result = -1; continue; }
        IOReturn r = (*dev)->USBDeviceOpenSeize(dev);
        if (r == kIOReturnSuccess)
        {
            r = (*dev)->USBDeviceReEnumerate(dev, 0);
            (*dev)->USBDeviceClose(dev);
        }
        (*dev)->Release(dev);
        if (r == kIOReturnSuccess) result = 0;
        else if (result != 0) result = r;
    }
    IOObjectRelease(it);
    return result;
}

// Whether the device with this vendor id and serial number is on USB, and whether a program (SDRplay's
// service, while a program streams from the radio) holds it, or one of its interfaces, for exclusive use.
// Returns -1 when it is not on USB, 0 when it is there and free, 1 when it is held.
EXPORT int ss_usb_state(int vendor, const char *serial)
{
    CFMutableDictionaryRef match = IOServiceMatching(kIOUSBDeviceClassName);
    if (!match) return -1;
    io_iterator_t it;
    if (IOServiceGetMatchingServices(kIOMainPortDefault, match, &it) != KERN_SUCCESS) return -1;
    int result = -1;
    io_service_t svc;
    while ((svc = IOIteratorNext(it)))
    {
        if (int_property(svc, CFSTR(kUSBVendorID)) == vendor && serial_matches(svc, serial))
        {
            CFTypeRef owner = IORegistryEntrySearchCFProperty(svc, kIOServicePlane, CFSTR("UsbExclusiveOwner"),
                kCFAllocatorDefault, kIORegistryIterateRecursively);
            if (owner) { CFRelease(owner); result = 1; }
            else if (result < 0) result = 0;
        }
        IOObjectRelease(svc);
    }
    IOObjectRelease(it);
    return result;
}
#else
EXPORT int ss_usb_reenumerate(int vendor, const char *serial)
{
    (void)vendor; (void)serial;
    return -1;      // not done on this system
}

EXPORT int ss_usb_state(int vendor, const char *serial)
{
    (void)vendor; (void)serial;
    return -2;      // not known on this system
}
#endif
