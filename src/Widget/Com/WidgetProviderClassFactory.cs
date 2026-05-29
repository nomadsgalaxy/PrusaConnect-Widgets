using System;
using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets.Providers;
using PrusaConnect.Widget.Diagnostics;
using WinRT;

namespace PrusaConnect.Widget.Com;

/// <summary>
/// IClassFactory for the COM-activated WidgetProvider.
///
/// The host cares about two interfaces - IWidgetProvider (lifecycle) and
/// IWidgetProvider2 (adds OnCustomizationRequested) - and QIs for whichever it
/// needs. If the CCW is typed only against IWidgetProvider, the QI for
/// IWidgetProvider2 fails silently: the Customize item shows but does nothing.
/// So we hand back a MarshalInspectable&lt;IWidgetProvider2&gt; CCW, which has
/// the right vtable.
///
/// A fresh <see cref="WidgetProvider"/> is created per CreateInstance; shared
/// per-process state lives in WidgetProvider's static dictionaries.
/// </summary>
internal sealed class WidgetProviderClassFactory : IClassFactory
{
    private const int S_OK = 0;
    private const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
    private const int E_NOINTERFACE = unchecked((int)0x80004002);

    public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
    {
        ppvObject = IntPtr.Zero;
        Log.Write($"ClassFactory.CreateInstance riid={riid}");

        if (pUnkOuter != IntPtr.Zero)
        {
            return CLASS_E_NOAGGREGATION;
        }

        try
        {
            // always marshal as the most-derived interface (IWidgetProvider2).
            // the host asks for IUnknown here then QIs the CCW; if we built it as
            // T=IWidgetProvider, the QI for IWidgetProvider2 fails and Customize
            // no-ops. T=IWidgetProvider2 exposes both (it `requires`
            // IWidgetProvider in WinRT metadata).
            if (riid == typeof(IWidgetProvider).GUID
                || riid == typeof(IWidgetProvider2).GUID
                || riid == Guid.Parse(ComGuids.IUnknown))
            {
                ppvObject = MarshalInspectable<IWidgetProvider2>.FromManaged(new WidgetProvider());
                Log.Write($"ClassFactory: returned IWidgetProvider2 CCW, ppv=0x{ppvObject:X}, requested riid={riid}");
                return S_OK;
            }

            Log.Write($"ClassFactory: rejecting riid={riid} (not IWidgetProvider / IWidgetProvider2 / IUnknown)");
            return E_NOINTERFACE;
        }
        catch (Exception ex)
        {
            Log.Error("ClassFactory.CreateInstance threw", ex);
            return ex.HResult == 0 ? E_NOINTERFACE : ex.HResult;
        }
    }

    public int LockServer(bool fLock) => S_OK;
}
