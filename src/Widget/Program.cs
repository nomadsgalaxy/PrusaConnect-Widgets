using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using PrusaConnect.Widget.Auth;
using PrusaConnect.Widget.Com;
using PrusaConnect.Widget.Diagnostics;
using PrusaConnect.Widget.Settings;
using Windows.ApplicationModel.Activation;

namespace PrusaConnect.Widget;

internal static class Program
{
    private const string ComServerArg = "-RegisterProcessAsComServer";
    private const string InstanceKey = "prusa-connect-widget-settings";

    [STAThread]
    private static int Main(string[] args)
    {
        Log.Write($"---- Main entered with {args.Length} args: [{string.Join(", ", args)}] ----");

        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();

            bool isComServer = args.Any(a =>
                string.Equals(a, ComServerArg, StringComparison.OrdinalIgnoreCase));

            if (isComServer)
            {
                // Widget COM server path bypasses the UI single-instance gate.
                return RunWidgetComServer();
            }

            return RunSettingsApp();
        }
        catch (Exception ex)
        {
            Log.Error("Fatal in Main", ex);
            return -1;
        }
    }

    private static int RunSettingsApp()
    {
        var activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();

        // single-instance the settings UI so the OAuth redirect (a Protocol
        // activation in a fresh process) gets forwarded to the open window
        // instead of opening a second one.
        var keyInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!keyInstance.IsCurrent)
        {
            Log.Write($"Already running; redirecting activation (kind={activatedArgs.Kind}) to existing instance.");
            keyInstance.RedirectActivationToAsync(activatedArgs).AsTask().GetAwaiter().GetResult();
            return 0;
        }

        // handle the first activation here (this is the canonical instance)
        HandleActivation(activatedArgs);

        // And any subsequent ones forwarded from sibling processes.
        keyInstance.Activated += (sender, e) =>
        {
            Log.Write($"Received forwarded activation kind={e.Kind}");
            HandleActivation(e);
        };

        Application.Start(p =>
        {
            var queue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(queue));

            _ = new App();
            var window = new SettingsWindow();
            App.SettingsWindow = window;
            window.Closed += (_, _) => Application.Current.Exit();
            window.Activate();
            WindowInterop.CenterAndActivate(window);
        });

        Log.Write("Settings app exited.");
        return 0;
    }

    private static void HandleActivation(AppActivationArguments args)
    {
        switch (args.Kind)
        {
            case ExtendedActivationKind.Protocol:
                if (args.Data is ProtocolActivatedEventArgs proto)
                {
                    Log.Write($"Protocol activation: {proto.Uri}");
                    bool handled = AuthFlowCoordinator.TryHandleRedirect(proto.Uri);
                    Log.Write($"AuthFlow handled redirect: {handled}");
                }
                break;
            case ExtendedActivationKind.Launch:
                // Plain user launch - settings window opens via Application.Start.
                break;
            default:
                Log.Write($"Unhandled activation kind: {args.Kind}");
                break;
        }
    }

    private static int RunWidgetComServer()
    {
        Log.Write("RunWidgetComServer: starting WinUI Application pump first, COM registration inside callback.");

        Application.Start(p =>
        {
            try
            {
                var queue = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(queue));
                _ = new App();

                Log.Write("Pump alive; registering WidgetProvider class factory.");

                var factory = new WidgetProviderClassFactory();
                int hr = NativeMethods.CoRegisterClassObject(
                    WidgetProvider.ClsidGuid,
                    factory,
                    NativeMethods.CLSCTX_LOCAL_SERVER,
                    NativeMethods.REGCLS_MULTIPLEUSE,
                    out uint cookie);

                if (hr < 0)
                {
                    Log.Write($"CoRegisterClassObject failed: 0x{hr:X8}");
                    Application.Current.Exit();
                    return;
                }
                Log.Write($"CoRegisterClassObject ok; cookie={cookie}. Ready for host activation.");

                // render every widget the host already has, so off-screen tiles
                // (which the host won't Activate) still get content and stick
                WidgetProvider.SeedFromHost();
            }
            catch (Exception ex)
            {
                Log.Error("Application.Start callback threw", ex);
                Application.Current?.Exit();
            }
        });

        Log.Write("Application.Start returned; COM server exiting.");
        return 0;
    }
}
