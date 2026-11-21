#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;

namespace SplitWireTurkey.SplitWireX
{
    /// <summary>Reads which SplitWire methods are installed/running (Windows services + one-time runs).</summary>
    public static class XStatus
    {
        public enum State { None, Stopped, Running }

        public sealed record Method(string Name, string ShortName, State State);

        public sealed class Snapshot
        {
            public List<Method> Methods { get; } = new();
            public HashSet<string> OneTime { get; } = new();
            public List<string> ActiveNames { get; } = new();
        }

        // Display name, chip label, services that belong to it, one-time run key
        private static readonly (string name, string chip, string[] services, string? oneTime)[] Map =
        {
            ("WireSock",   "WireSock", new[] { "WireSockAppService", "WireSockConnectService", "wiresock-client-service" }, null),
            ("ByeDPI",     "ByeDPI",   new[] { "ByeDPI", "ProxiFyreService" }, null),
            ("Zapret",     "Zapret",   new[] { "zapret", "winws1" }, "Zapret"),
            ("Zapret2",    "Zapret2",  new[] { "zapret2", "winws2" }, "Zapret2"),
            ("GoodbyeDPI", "GDPI",     new[] { "GoodbyeDPI" }, "GoodbyeDPI"),
            ("DNSCrypt",   "DNS",      new[] { "dnscrypt-proxy" }, null),
        };

        public static Snapshot Capture()
        {
            var snap = new Snapshot();
            var services = new Dictionary<string, ServiceControllerStatus>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var sc in ServiceController.GetServices())
                {
                    using (sc)
                    {
                        try { services[sc.ServiceName] = sc.Status; } catch { }
                    }
                }
            }
            catch { }

            foreach (var r in XConsole.GetActiveRuns()) snap.OneTime.Add(r.Name);

            foreach (var (name, chip, svcNames, oneTime) in Map)
            {
                var state = State.None;
                foreach (var s in svcNames)
                {
                    if (!services.TryGetValue(s, out var st)) continue;
                    if (st == ServiceControllerStatus.Running || st == ServiceControllerStatus.StartPending)
                    {
                        state = State.Running;
                        break;
                    }
                    state = State.Stopped;
                }

                bool runningOnce = oneTime != null && snap.OneTime.Contains(oneTime);
                if (runningOnce) state = State.Running;

                snap.Methods.Add(new Method(name, chip, state));
                // DNS alone is not "protection"
                if (state == State.Running && name != "DNSCrypt")
                {
                    snap.ActiveNames.Add(runningOnce ? $"{name} (once)" : name);
                }
            }
            return snap;
        }
    }
}
