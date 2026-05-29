using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Windows.Widgets.Providers;
using PrusaConnect.Core.Aggregator;
using PrusaConnect.Core.PrusaConnect;
using PrusaConnect.Core.Storage;
using PrusaConnect.Widget.Diagnostics;
using PrusaConnect.Widget.Rendering;

namespace PrusaConnect.Widget;

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[Guid(ClsidString)]
public sealed class WidgetProvider : IWidgetProvider, IWidgetProvider2
{
    public const string ClsidString = "B9DE1AA2-389D-4D14-B146-61B1040C0B59";
    public static Guid ClsidGuid { get; } = new Guid(ClsidString);

    internal const string VerbSavePrinter = "save-printer";

    private static readonly Lazy<PrinterStore> _store =
        new(() => new PrinterStore(AppPaths.PrintersDirectory));
    private static readonly Lazy<ISecretsStore> _secrets =
        new(() => new DpapiSecretsStore(AppPaths.SecretsDirectory));
    private static readonly Lazy<PinnedWidgetStore> _pinned =
        new(() => new PinnedWidgetStore(AppPaths.PinnedWidgetsDirectory));
    private static readonly Lazy<RebindRequestStore> _rebinds =
        new(() => new RebindRequestStore(AppPaths.PinnedWidgetsDirectory));
    private static readonly Lazy<ConnectTokenCache> _connectTokens =
        new(() => new ConnectTokenCache(_secrets.Value));
    private static readonly Lazy<PrinterStatusService> _statusService =
        new(() => new PrinterStatusService(
            _secrets.Value, _connectTokens.Value, ConnectAccountDefaults.PrusaConnectCloud));

    private static readonly ConcurrentDictionary<string, WidgetSession> _sessions = new();

    // the 4 definitions our manifest declares (the host caps a provider at ~4).
    // the platform can still report stale widgets for defs we removed (old
    // prusa.printer.05-10) - ignore those, the board can't show them anyway.
    private static readonly System.Collections.Generic.HashSet<string> ValidDefinitions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "prusa.printer.01", "prusa.printer.02", "prusa.printer.03", "prusa.printer.04",
        };

    public void CreateWidget(WidgetContext widgetContext)
    {
        Log.Write($"CreateWidget id={widgetContext.Id} def={widgetContext.DefinitionId}");
        try
        {
            // the host wants content right away on create. we render async in the
            // poll loop, so a tile created-then-hidden before the first poll would
            // never get an UpdateWidget - which wedges it in the host and blocks
            // re-pinning that def. push a placeholder now so it always has content.
            PushLoadingCard(widgetContext.Id);
            StartSession(widgetContext);
        }
        catch (Exception ex)
        {
            Log.Error($"CreateWidget {widgetContext.Id} threw", ex);
        }
    }

    /// <summary>
    /// Push a minimal "loading" card so a fresh widget has content right away,
    /// even if the host hides it before the poll loop renders. No network.
    /// </summary>
    private static void PushLoadingCard(string widgetId)
    {
        try
        {
            var card = new
            {
                type = "AdaptiveCard",
                version = "1.5",
                body = new object[]
                {
                    new { type = "TextBlock", text = "PRUSA", weight = "Bolder", size = "ExtraLarge", horizontalAlignment = "Center" },
                    new { type = "TextBlock", text = "Loading…", isSubtle = true, horizontalAlignment = "Center", spacing = "Small" },
                },
            };
            // one outbound call on the hot path - skip the extra GetCustomState
            // RPC for a loading card
            var options = new WidgetUpdateRequestOptions(widgetId)
            {
                Template = JsonSerializer.Serialize(card),
                Data = "{}",
                CustomState = string.Empty,
            };
            WidgetManager.GetDefault().UpdateWidget(options);
            Log.Write($"PushLoadingCard {widgetId} sent");
        }
        catch (Exception ex)
        {
            Log.Error($"PushLoadingCard {widgetId} threw", ex);
        }
    }

    public void DeleteWidget(string widgetId, string customState)
    {
        Log.Write($"DeleteWidget id={widgetId}");
        if (_sessions.TryRemove(widgetId, out var session))
        {
            session.Dispose();
        }
        _pinned.Value.Remove(widgetId);
    }

    public void Activate(WidgetContext widgetContext)
    {
        Log.Write($"Activate id={widgetContext.Id}");
        try
        {
            StartSession(widgetContext);
        }
        catch (Exception ex)
        {
            Log.Error($"Activate {widgetContext.Id} threw", ex);
        }
    }

    public void Deactivate(string widgetId)
    {
        Log.Write($"Deactivate id={widgetId}");
        try
        {
            if (_sessions.TryGetValue(widgetId, out var session))
            {
                session.Stop();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Deactivate {widgetId} threw", ex);
        }
    }

    public void OnActionInvoked(WidgetActionInvokedArgs args)
    {
        Log.Write("OnActionInvoked ENTRY");
        try
        {
            string? verb = args?.Verb;
            string? widgetId = args?.WidgetContext?.Id;
            string? data = args?.Data;
            Log.Write($"OnActionInvoked id={widgetId} verb='{verb}' data='{data}'");

            if (string.Equals(verb, VerbSavePrinter, StringComparison.Ordinal) && args is not null)
            {
                HandleSavePrinter(args);
            }
        }
        catch (Exception ex)
        {
            Log.Error("OnActionInvoked threw", ex);
        }
    }

    public void OnWidgetContextChanged(WidgetContextChangedArgs contextChangedArgs)
    {
        Log.Write($"OnWidgetContextChanged id={contextChangedArgs.WidgetContext.Id}");
        try
        {
            StartSession(contextChangedArgs.WidgetContext);
        }
        catch (Exception ex)
        {
            Log.Error("OnWidgetContextChanged threw", ex);
        }
    }

    // explicit interface impl so the compiler binds this to IWidgetProvider2's
    // exact vtable slot - the implicit (public) form didn't dispatch; the host's
    // COM call returned at the marshal layer without hitting managed code.
    void IWidgetProvider2.OnCustomizationRequested(WidgetCustomizationRequestedArgs args)
    {
        Log.Write("OnCustomizationRequested ENTRY (explicit IWidgetProvider2 impl)");
        try
        {
            if (args is null)
            {
                Log.Write("OnCustomizationRequested called with null args (?!)");
                return;
            }
            var ctx = args.WidgetContext;
            if (ctx is null)
            {
                Log.Write("OnCustomizationRequested args.WidgetContext is null");
                return;
            }
            Log.Write($"OnCustomizationRequested id={ctx.Id} def={ctx.DefinitionId}");
            PushCustomizeCard(ctx);
            Log.Write($"OnCustomizationRequested {ctx.Id} done");
        }
        catch (Exception ex)
        {
            Log.Error("OnCustomizationRequested threw", ex);
        }
    }

    /// <summary>
    /// On COM-server start, render EVERY widget the host has - not just the ones
    /// it later Activates. The host only Activates visible tiles, so off-screen
    /// ones would never get content and could be dropped. GetWidgetInfos() gives
    /// every pinned tile content + a live session.
    /// </summary>
    internal static void SeedFromHost()
    {
        try
        {
            var infos = WidgetManager.GetDefault().GetWidgetInfos();
            if (infos is null || infos.Length == 0)
            {
                Log.Write("SeedFromHost: host reports no widgets");
                return;
            }
            Log.Write($"SeedFromHost: seeding {infos.Length} widget(s) from host");
            foreach (var info in infos)
            {
                var ctx = info?.WidgetContext;
                if (ctx is null || string.IsNullOrEmpty(ctx.Id)) continue;
                if (!ValidDefinitions.Contains(ctx.DefinitionId))
                {
                    Log.Write($"SeedFromHost: skipping orphan widget {ctx.Id} for removed def {ctx.DefinitionId}");
                    continue;
                }
                try
                {
                    PushLoadingCard(ctx.Id);
                    StartSession(ctx);
                }
                catch (Exception ex) { Log.Error($"SeedFromHost {ctx.Id} threw", ex); }
            }
        }
        catch (Exception ex)
        {
            Log.Error("SeedFromHost threw", ex);
        }
    }

    private static void StartSession(WidgetContext ctx)
    {
        if (!ValidDefinitions.Contains(ctx.DefinitionId))
        {
            Log.Write($"StartSession: ignoring widget {ctx.Id} for undeclared def {ctx.DefinitionId}");
            return;
        }

        var session = _sessions.GetOrAdd(
            ctx.Id,
            id => new WidgetSession(id, ctx.DefinitionId, _store.Value, _secrets.Value,
                _pinned.Value, _rebinds.Value, _statusService.Value));

        // Track the tile's current size so we render the right template.
        try { session.SetSize(ctx.Size.ToString()); } catch { /* older SDK */ }

        // source of truth for the binding is OUR pinned-widgets.json, NOT the
        // host's customState - that round-trip is unreliable (it clobbered
        // kind/printer changes on every Activate). the snapshot survives COM
        // restarts and the session ctor, so it's the durable {kind, printerId}.
        string custom;
        var snap = _pinned.Value.GetAll().FirstOrDefault(s => s.WidgetId == ctx.Id);
        if (snap is not null && (!string.IsNullOrEmpty(snap.BoundPrinterId) || !string.IsNullOrEmpty(snap.Kind)))
        {
            // durable record: the tile's kind + binding (a printer id, or for
            // farm tiles the team's org id). keep the kind even with no binding so
            // a farm tile stays a farm tile and an unset printer tile keeps
            // showing "Tile not set".
            custom = JsonSerializer.Serialize(new
            {
                kind = string.IsNullOrEmpty(snap.Kind) ? "printer-status" : snap.Kind,
                printerId = snap.BoundPrinterId ?? string.Empty,
            });
        }
        else
        {
            // fresh pin: NEVER auto-assign a printer. the user sets every tile in
            // settings, so leave it unbound and show "tile not set" until they
            // pick something. (still honor any host customState that already has a
            // binding, e.g. a tile the host restored.)
            custom = GetCustomState(ctx.Id);
            if (string.IsNullOrEmpty(ParsePrinterIdFromCustomState(custom)))
            {
                Log.Write($"Fresh pin {ctx.Id}: left unbound (user sets tiles in settings)");
            }
        }

        session.ApplyCustomState(custom);
        session.Start();
    }

    private static void PushCustomizeCard(WidgetContext ctx)
    {
        _store.Value.RefreshIfStale();
        var printers = _store.Value.GetAll();

        string? currentPrinterId = ParsePrinterIdFromCustomState(GetCustomState(ctx.Id));

        // customize card: a ChoiceSet of every imported printer + a Save action.
        // the host renders it as a settings form.
        var choices = printers.Select(p => new
        {
            title = string.IsNullOrEmpty(p.Model) ? p.Name : $"{p.Name} - {p.Model}",
            value = p.Id,
        }).ToArray();

        object card;
        if (choices.Length == 0)
        {
            card = new
            {
                type = "AdaptiveCard",
                version = "1.5",
                body = new object[]
                {
                    new
                    {
                        type = "TextBlock",
                        text = "No printers configured",
                        weight = "Bolder",
                        size = "Medium",
                    },
                    new
                    {
                        type = "TextBlock",
                        text = "Open the Prusa Connect Widget app from the Start menu to add a printer (manually or by importing from Prusa Connect).",
                        wrap = true,
                        isSubtle = true,
                        size = "Small",
                    },
                },
            };
        }
        else
        {
            card = new
            {
                type = "AdaptiveCard",
                version = "1.5",
                body = new object[]
                {
                    new
                    {
                        type = "TextBlock",
                        text = "Choose a printer",
                        weight = "Bolder",
                        size = "Medium",
                    },
                    new
                    {
                        type = "TextBlock",
                        text = "Pick which printer this widget tile should show.",
                        wrap = true,
                        isSubtle = true,
                        size = "Small",
                        spacing = "None",
                    },
                    new
                    {
                        type = "Input.ChoiceSet",
                        id = "printerId",
                        value = currentPrinterId ?? string.Empty,
                        choices = choices,
                        style = "compact",
                        placeholder = "Choose a printer",
                        isRequired = true,
                        errorMessage = "Pick a printer to continue.",
                    },
                },
                actions = new object[]
                {
                    new
                    {
                        type = "Action.Execute",
                        verb = VerbSavePrinter,
                        title = "Save",
                        style = "positive",
                    },
                },
            };
        }

        string templateJson = JsonSerializer.Serialize(card);
        var options = new WidgetUpdateRequestOptions(ctx.Id)
        {
            Template = templateJson,
            Data = "{}",
            CustomState = GetCustomState(ctx.Id),
        };
        WidgetManager.GetDefault().UpdateWidget(options);
    }

    private static string GetCustomState(string widgetId)
    {
        try
        {
            return WidgetManager.GetDefault().GetWidgetInfo(widgetId)?.CustomState ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error($"GetCustomState({widgetId}) threw", ex);
            return string.Empty;
        }
    }

    private static void HandleSavePrinter(WidgetActionInvokedArgs args)
    {
        string? newPrinterId = null;
        try
        {
            using var doc = JsonDocument.Parse(args.Data);
            if (doc.RootElement.TryGetProperty("printerId", out var idEl))
            {
                newPrinterId = idEl.GetString();
            }
        }
        catch (JsonException ex)
        {
            Log.Error($"HandleSavePrinter: bad data JSON", ex);
            return;
        }

        if (string.IsNullOrEmpty(newPrinterId))
        {
            Log.Write("HandleSavePrinter: no printerId in submit payload");
            return;
        }

        // persist the binding into customState - the host hands it back on every
        // CreateWidget/Activate, so it survives host / COM-server restarts
        string newCustomState = JsonSerializer.Serialize(new { printerId = newPrinterId });
        Log.Write($"HandleSavePrinter id={args.WidgetContext.Id} -> printer {newPrinterId}");

        if (_sessions.TryGetValue(args.WidgetContext.Id, out var session))
        {
            session.ApplyCustomState(newCustomState);
            // stop+start forces an immediate refresh instead of waiting a tick
            session.Stop();
            session.Start();
        }

        // interim "loading" render so the host drops the customize form before
        // the first poll finishes
        var loadingCard = new
        {
            type = "AdaptiveCard",
            version = "1.5",
            body = new object[]
            {
                new { type = "TextBlock", text = "Loading…", weight = "Bolder", horizontalAlignment = "Center" },
            },
        };
        var options = new WidgetUpdateRequestOptions(args.WidgetContext.Id)
        {
            Template = JsonSerializer.Serialize(loadingCard),
            Data = "{}",
            CustomState = newCustomState,
        };
        WidgetManager.GetDefault().UpdateWidget(options);
    }

    private static string? ParsePrinterIdFromCustomState(string customState)
    {
        if (string.IsNullOrWhiteSpace(customState)) return null;
        try
        {
            using var doc = JsonDocument.Parse(customState);
            return doc.RootElement.TryGetProperty("printerId", out var el)
                ? el.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
