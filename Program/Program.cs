using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Timer = System.Timers.Timer;

namespace TestConsole
{
    class Program
    {
        static HttpClient _client { get; set; }
        static Timer _host_timer { get; set; }
        static Timer _close_timer { get; set; }

        static void Main(string[] args)
        {
            Console.WriteLine("Hello World!");
        }

        public static async Task Main()
        {
            SetupHttpClient();
            SetupHostLookup_Timer();
            SetupTermination_Timer();

            while (true)
            {
                Console.WriteLine($"Running batch - TimeSecond: {DateTime.Now.Second}");

                var tasks = Enumerable.Range(0, 5).Select(async _ =>
                {
                    try
                    {
                        await _client.GetAsync("http://alpha.com");
                    }
                    catch (Exception exc)
                    {
                        Console.WriteLine(exc);
                    }
                }).ToArray();
                await Task.WhenAll(tasks);
                await Task.Delay(5_000);
            }
        }

        static void SetupHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = 5,
                // We'll leverage this new callback
                ConnectCallback = ConnectCallback_DnsCheck
            };

            _client = new HttpClient(handler);
        }

        static void SetupHostLookup_Timer()
        {
            _host_timer = new Timer
            {
                Interval = 5_000,
                AutoReset = true
            };
            _host_timer.Elapsed += (_, _) =>
            {
                _host_timer.Stop();
                try
                {
                    OnTick_HostLookup();
                }
                catch
                {
                    // ignored
                }

                _host_timer.Start();
            };
            _host_timer.Start();
        }

        static void SetupTermination_Timer()
        {
            _close_timer = new Timer
            {
                Interval = 1_000,
                AutoReset = true
            };
            _close_timer.Elapsed += (_, _) =>
            {
                _close_timer.Stop();
                try
                {
                    OnTick_Termination();
                }
                catch
                {
                    // ignored
                }

                _close_timer.Start();
            };
            _close_timer.Start();
        }

        static async ValueTask<Stream> ConnectCallback_DnsCheck(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);

                // Everything is as default implementation except here we register
                // and track socket to host
                PairSocketToHost(socket, context.DnsEndPoint.Host);

                return new NetworkStream(socket, true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        static ConcurrentDictionary<string, List<Socket>> _host_sockets { get; } = new();
        static ConcurrentDictionary<string, List<IPAddress>> _host_ips { get; } = new();
        static bool NoDnsChanges(string host, List<IPAddress> ips) => _host_ips[host].All(ips.Contains) && _host_ips[host].Count == ips.Count;

        public static void PairSocketToHost(Socket socket, string host)
        {
            Console.WriteLine($"Opening Socket: {socket.Handle}");

            _host_sockets.AddOrUpdate(host, new List<Socket> {socket}, (_, list) =>
            {
                list.Add(socket);
                return list;
            });

            _host_ips.TryAdd(host, new List<IPAddress>());
        }

        static void OnTick_HostLookup()
        {
            foreach (var host in _host_ips.Keys)
            {
                // Read IPs from Host
                var ips = Dns.GetHostAddresses(host).ToList();
                var seed = _host_ips[host].Count == 0;

                // Detect Change in DNS (different IPs or IP count)
                if (NoDnsChanges(host, ips))
                    continue;

                _host_ips[host] = ips;

                if (seed)
                {
                    Console.WriteLine($"Seeding host ({host})");
                    continue;
                }

                // Send sockets off to termination
                Console.WriteLine($"Change detected in host ({host})");
                var sockets = _host_sockets[host].ToList();
                _host_sockets[host].Clear();

                foreach (var s in sockets)
                    _termination.TryAdd(s);
            }

            Cleanup();
        }

        static void Cleanup()
        {
            // Already disconnected sockets are removed for GC
            foreach (var pair in _host_sockets)
                _host_sockets[pair.Key] = pair.Value.Where(s => s.Connected).ToList();
        }

        static BlockingCollection<Socket> _termination { get; } = new();

        static void OnTick_Termination()
        {
            _termination.TryTake(out var sck);

            if (sck == null) return;

            try
            {
                Console.WriteLine($"Closing Socket: {sck.Handle}");
                sck.Shutdown(SocketShutdown.Both);
            }
            finally
            {
                sck.Close();
            }
        }
    }
}