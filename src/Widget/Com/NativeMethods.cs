using System;
using System.Runtime.InteropServices;

namespace PrusaConnect.Widget.Com;

// classic DllImport on purpose, not LibraryImport: the IUnknown-marshalled object
// param CoRegisterClassObject takes isn't supported by the LibraryImport generator
// (SYSLIB1052), and the runtime marshaller is also what keeps the class factory
// reachable when WinRT.ComWrappers is the default - see WidgetProviderClassFactory
// for the matching FromManaged pattern.
internal static class NativeMethods
{
    public const uint CLSCTX_LOCAL_SERVER = 0x4;
    public const uint REGCLS_MULTIPLEUSE = 0x1;

    [DllImport("ole32.dll")]
    public static extern int CoRegisterClassObject(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        [MarshalAs(UnmanagedType.IUnknown)] object pUnk,
        uint dwClsContext,
        uint flags,
        out uint lpdwRegister);

    [DllImport("ole32.dll")]
    public static extern int CoRevokeClassObject(uint dwRegister);

    [DllImport("ole32.dll")]
    public static extern int CoResumeClassObjects();
}
