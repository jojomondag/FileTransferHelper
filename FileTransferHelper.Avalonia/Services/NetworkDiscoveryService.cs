using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FileTransferHelper.Models;
using Zeroconf;

namespace FileTransferHelper.Services;

public sealed class NetworkDiscoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public IReadOnlyList<HostCandidate> CachedHosts()
    {
        try
        {
            if (!File.Exists(AppPaths.DeviceCachePath))
            {
                return [];
            }

            var items = JsonSerializer.Deserialize<List<DeviceCacheItem>>(File.ReadAllText(AppPaths.DeviceCachePath), JsonOptions) ?? [];
            return SortHosts(items
                .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Address))
                .Select(item => new HostCandidate(
                    item.Name.Trim(),
                    item.Address.Trim(),
                    string.IsNullOrWhiteSpace(item.Source) ? "sparad" : item.Source.Trim(),
                    item.Username?.Trim() ?? string.Empty,
                    item.UseKey,
                    item.KeyPath?.Trim() ?? string.Empty,
                    item.HardwareId?.Trim() ?? string.Empty))
                .ToList());
        }
        catch (Exception exc) when (exc is IOException or JsonException or UnauthorizedAccessException)
        {
            Log($"Kunde inte läsa devices.json: {exc.Message}");
            return [];
        }
    }

    public void SaveCache(IEnumerable<HostCandidate> hosts)
    {
        var unique = new Dictionary<string, HostCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in hosts)
        {
            if (!unique.TryGetValue(host.Address, out var existing) || CandidateQuality(host) > CandidateQuality(existing))
            {
                unique[host.Address] = host;
            }
        }

        var items = SortHosts(unique.Values).Select(host => new DeviceCacheItem
        {
            Name = host.Name,
            Address = host.Address,
            Source = host.Source,
            Username = host.Username,
            UseKey = host.UseKey,
            KeyPath = host.KeyPath,
            HardwareId = host.HardwareId,
            UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        }).ToList();

        File.WriteAllText(AppPaths.DeviceCachePath, JsonSerializer.Serialize(items, JsonOptions));
    }

    public async Task<IReadOnlyList<HostCandidate>> QuickVerifyCachedAsync(Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var cached = CachedHosts();
        if (cached.Count == 0)
        {
            return [];
        }

        progress?.Invoke("Kontrollerar sparade enheter...");
        Log("");
        Log("=== Snabbkontroll av sparade enheter ===");

        var reachable = new ConcurrentBag<HostCandidate>();
        await Parallel.ForEachAsync(cached, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(16, Math.Max(1, cached.Count)), CancellationToken = cancellationToken }, async (host, token) =>
        {
            var open = await PortIsOpenAsync(host.Address, AppPaths.SshPort, TimeSpan.FromMilliseconds(600), token);
            if (!open)
            {
                Log($"Sparad enhet svarar inte: {host.Label}");
                return;
            }

            var refreshed = await ProbeSshAsync(host.Address, token);
            if (refreshed is not null && CandidateQuality(refreshed) > CandidateQuality(host))
            {
                refreshed = MergeHostMetadata(refreshed, host);
                reachable.Add(refreshed);
                Log($"Sparad enhet svarar med uppdaterat namn: {refreshed.Label}");
            }
            else
            {
                reachable.Add(host);
                Log($"Sparad enhet svarar: {host.Label}");
            }
        });

        return SortHosts(reachable);
    }

    public async Task<IReadOnlyList<HostCandidate>> DiscoverAsync(Action<string>? progress = null, bool useCachedFirst = true, CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<string, HostCandidate>(StringComparer.OrdinalIgnoreCase);
        Log("");
        Log("=== Ny nätverkssökning ===");

        void Add(HostCandidate candidate)
        {
            if (found.TryGetValue(candidate.Address, out var existing))
            {
                if (CandidateQuality(candidate) > CandidateQuality(existing))
                {
                    candidate = MergeHostMetadata(candidate, existing);
                    found[candidate.Address] = candidate;
                    Log($"Ersätter {existing.Label} [{existing.Source}] med {candidate.Label} [{candidate.Source}]");
                }
                else
                {
                    found[candidate.Address] = MergeHostMetadata(existing, candidate);
                    Log($"Behåller {existing.Label} [{existing.Source}]; ignorerar dubblett {candidate.Label} [{candidate.Source}]");
                }

                return;
            }

            found[candidate.Address] = candidate;
            Log($"Lade till: {candidate.Label} [{candidate.Source}]");
        }

        if (useCachedFirst)
        {
            foreach (var candidate in await QuickVerifyCachedAsync(progress, cancellationToken))
            {
                Add(candidate);
            }
        }

        foreach (var candidate in await KnownRaspberryNamesAsync(cancellationToken))
        {
            Add(candidate);
        }

        progress?.Invoke("Söker via mDNS...");
        foreach (var candidate in await MdnsHostsAsync(cancellationToken))
        {
            Add(candidate);
        }

        progress?.Invoke("Skannar lokala nätverket efter SSH...");
        foreach (var candidate in await ScanLocalNetworksAsync(cancellationToken))
        {
            Add(candidate);
        }

        var results = await LabelDuplicateNetworkInterfacesAsync(found.Values, cancellationToken);
        if (results.Count > 0)
        {
            SaveCache(results);
        }

        Log($"Slutresultat: {results.Count} enhet(er)");
        foreach (var candidate in results)
        {
            Log($"  {candidate.Label} [{candidate.Source}]");
        }

        return results;
    }

    private async Task<IReadOnlyList<HostCandidate>> KnownRaspberryNamesAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<HostCandidate>();
        var hostnameGuesses = new (string Hostname, string DisplayName)[]
        {
            ("raspberrypi.local", "raspberrypi.local"),
            ("raspberrypi", "raspberrypi"),
            ("pi.local", "pi.local"),
            ("piserver.local", "PISERVER"),
            ("piserver", "PISERVER"),
            ("pi-tv.local", "Pi TV"),
            ("pi-tv", "Pi TV"),
            ("pitv.local", "Pi TV"),
            ("pitv", "Pi TV")
        };

        foreach (var (hostname, displayName) in hostnameGuesses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IPAddress[] addresses;
            try
            {
                addresses = await Dns.GetHostAddressesAsync(hostname, cancellationToken);
            }
            catch
            {
                Log($"Standardnamn saknas: {hostname}");
                continue;
            }

            var address = addresses.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork)?.ToString();
            if (address is not null && await PortIsOpenAsync(address, AppPaths.SshPort, TimeSpan.FromMilliseconds(600), cancellationToken))
            {
                Log($"Standardnamn fungerar: {hostname} -> {address}");
                candidates.Add(new HostCandidate(displayName, address, "standardnamn"));
            }
        }

        return candidates;
    }

    private async Task<IReadOnlyList<HostCandidate>> MdnsHostsAsync(CancellationToken cancellationToken)
    {
        var hosts = new List<HostCandidate>();
        foreach (var (serviceType, source) in new[]
        {
            ("_ssh._tcp.local.", "mDNS SSH"),
            ("_sftp-ssh._tcp.local.", "mDNS SFTP"),
            ("_workstation._tcp.local.", "mDNS workstation")
        })
        {
            try
            {
                var results = await ZeroconfResolver.ResolveAsync(serviceType, TimeSpan.FromMilliseconds(1600));
                var count = 0;
                foreach (var result in results)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var address = result.IPAddress;
                    if (string.IsNullOrWhiteSpace(address) || !IPAddress.TryParse(address, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    var displayName = BestMdnsDisplayName(result.DisplayName, result.Id);
                    var hardwareId = HardwareIdFromMdnsName(result.DisplayName) ?? HardwareIdFromMdnsName(result.Id) ?? "";
                    hosts.Add(new HostCandidate(displayName, address, source, HardwareId: hardwareId));
                    count++;
                }

                Log($"{source} hittade {count} tjänst(er)");
            }
            catch (Exception exc)
            {
                Log($"{source} misslyckades: {exc.Message}");
            }
        }

        return hosts;
    }

    private async Task<IReadOnlyList<HostCandidate>> ScanLocalNetworksAsync(CancellationToken cancellationToken)
    {
        var addresses = CandidateScanAddresses();
        if (addresses.Count == 0)
        {
            Log("Ingen lokal nätverksadress kunde skannas");
            return [];
        }

        Log($"Skannar {addresses.Count} adress(er) efter SSH på port 22");
        var hosts = new ConcurrentBag<HostCandidate>();
        await Parallel.ForEachAsync(addresses, new ParallelOptions { MaxDegreeOfParallelism = 96, CancellationToken = cancellationToken }, async (address, token) =>
        {
            var candidate = await ProbeSshAsync(address, token);
            if (candidate is not null)
            {
                hosts.Add(candidate);
            }
        });

        return SortHosts(hosts);
    }

    private static IReadOnlyList<string> CandidateScanAddresses()
    {
        var ownAddresses = new HashSet<string>();
        var networks = new List<(uint Network, uint Mask, string OwnAddress)>();

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                {
                    continue;
                }

                var own = unicast.Address.ToString();
                if (own.StartsWith("127.", StringComparison.Ordinal) || own.StartsWith("169.254.", StringComparison.Ordinal))
                {
                    continue;
                }

                var ip = ToUInt32(unicast.Address);
                var mask = ToUInt32(unicast.IPv4Mask);
                ownAddresses.Add(own);
                networks.Add((ip & mask, mask, own));
            }
        }

        if (networks.Count == 0)
        {
            var own = PrimaryIpv4();
            if (own is not null && IPAddress.TryParse(own, out var address))
            {
                ownAddresses.Add(own);
                var ip = ToUInt32(address);
                var mask = ToUInt32(IPAddress.Parse("255.255.255.0"));
                networks.Add((ip & mask, mask, own));
            }
        }

        var results = new List<string>();
        var seen = new HashSet<string>();
        foreach (var (network, mask, own) in networks)
        {
            var totalAddresses = (ulong)(uint.MaxValue - mask) + 1;
            var hostCount = totalAddresses <= 2 ? 0 : totalAddresses - 2;
            var scanMask = mask;
            var scanNetwork = network;
            if (hostCount > 254)
            {
                var ownIp = ToUInt32(IPAddress.Parse(own));
                scanMask = ToUInt32(IPAddress.Parse("255.255.255.0"));
                scanNetwork = ownIp & scanMask;
                hostCount = 254;
            }

            for (uint offset = 1; offset <= hostCount; offset++)
            {
                var value = FromUInt32(scanNetwork + offset).ToString();
                if (ownAddresses.Contains(value) || !seen.Add(value))
                {
                    continue;
                }

                results.Add(value);
            }
        }

        return results;
    }

    private async Task<HostCandidate?> ProbeSshAsync(string address, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(350));
            await client.ConnectAsync(address, AppPaths.SshPort, timeoutCts.Token);
            await using var stream = client.GetStream();
            stream.ReadTimeout = 350;
            var buffer = new byte[80];
            var readTask = stream.ReadAsync(buffer, cancellationToken).AsTask();
            var completed = await Task.WhenAny(readTask, Task.Delay(350, cancellationToken));
            var banner = completed == readTask
                ? System.Text.Encoding.ASCII.GetString(buffer, 0, Math.Max(0, readTask.Result)).Trim()
                : string.Empty;

            var fallbackName = banner.Contains("raspberry", StringComparison.OrdinalIgnoreCase) ? "Raspberry Pi" : "SSH-enhet";
            Log($"SSH hittad: {address}, banner={(string.IsNullOrEmpty(banner) ? "<ingen banner>" : banner)}");
            var (name, source) = await FriendlyNameForAddressAsync(address, fallbackName, cancellationToken);
            Log($"Namnval för {address}: {name} ({source})");
            return new HostCandidate(name, address, source);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> PortIsOpenAsync(string address, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await client.ConnectAsync(address, port, timeoutCts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<(string Name, string Source)> FriendlyNameForAddressAsync(string address, string fallbackName, CancellationToken cancellationToken)
    {
        foreach (var resolver in new Func<string, CancellationToken, Task<string?>>[]
        {
            ReverseDnsNameAsync,
            PingNameAsync,
            NetbiosNameAsync
        })
        {
            var name = await resolver(address, cancellationToken);
            if (!string.IsNullOrWhiteSpace(name))
            {
                Log($"{resolver.Method.Name} för {address}: {name}");
                return (name, "port 22 + namn");
            }

            Log($"{resolver.Method.Name} för {address}: ingen träff");
        }

        return (fallbackName, "port 22");
    }

    private static async Task<string?> ReverseDnsNameAsync(string address, CancellationToken cancellationToken)
    {
        try
        {
            var host = await Dns.GetHostEntryAsync(address, cancellationToken);
            return CleanHostname(host.HostName);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> PingNameAsync(string address, CancellationToken cancellationToken)
    {
        var output = await RunProcessAsync("ping", $"-a -n 1 -w 500 {address}", TimeSpan.FromMilliseconds(1500), cancellationToken);
        var firstLine = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
        var match = Regex.Match(firstLine, @"\b([A-Za-z0-9][A-Za-z0-9_.-]+)\s+\[" + Regex.Escape(address) + @"\]");
        return match.Success ? CleanHostname(match.Groups[1].Value) : null;
    }

    private static async Task<string?> NetbiosNameAsync(string address, CancellationToken cancellationToken)
    {
        var output = await RunProcessAsync("nbtstat", $"-A {address}", TimeSpan.FromSeconds(2), cancellationToken);
        var ignoredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "WORKGROUP", "MSHOME", "__MSBROWSE__" };
        foreach (var line in output.Split(Environment.NewLine))
        {
            var match = Regex.Match(line, @"\s*([A-Za-z0-9_-]{1,15})\s+<00>\s+UNIQUE", RegexOptions.IgnoreCase);
            if (match.Success && !ignoredNames.Contains(match.Groups[1].Value.Trim()))
            {
                return CleanHostname(match.Groups[1].Value.Trim());
            }
        }

        return null;
    }

    private static async Task<string> RunProcessAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return string.Empty;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            return await outputTask;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string BestMdnsDisplayName(string? serviceName, string? serverName)
    {
        var cleanedService = CleanHostname(serviceName);
        var cleanedServer = CleanHostname(serverName);
        if (!string.IsNullOrWhiteSpace(cleanedService) &&
            !cleanedService.StartsWith("ssh ", StringComparison.OrdinalIgnoreCase) &&
            !cleanedService.StartsWith("sftp ", StringComparison.OrdinalIgnoreCase))
        {
            return cleanedService;
        }

        return cleanedServer ?? cleanedService ?? "mDNS-enhet";
    }

    private static string? HardwareIdFromMdnsName(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return null;
        }

        var match = Regex.Match(hostname, @"\[([0-9a-f]{2}(?::[0-9a-f]{2}){5})\]", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static string? CleanHostname(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return null;
        }

        hostname = hostname.Trim().TrimEnd('.');
        hostname = Regex.Replace(
            hostname,
            @"\s*\[[0-9a-f]{2}(?::[0-9a-f]{2}){5}\]\s*$",
            "",
            RegexOptions.IgnoreCase).Trim();
        return string.IsNullOrEmpty(hostname) || hostname.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? null : hostname;
    }

    private static int CandidateQuality(HostCandidate candidate)
    {
        var genericNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ssh-enhet", "raspberry pi", "mdns-enhet" };
        var score = 0;
        if (!genericNames.Contains(candidate.Name.Trim()))
        {
            score += 10;
        }

        if (candidate.Source.StartsWith("mDNS", StringComparison.OrdinalIgnoreCase))
        {
            score += 3;
        }

        if (candidate.Source == "standardnamn")
        {
            score += 2;
        }

        if (candidate.Source.Contains("namn", StringComparison.OrdinalIgnoreCase))
        {
            score += 1;
        }

        return score;
    }

    private static HostCandidate MergeHostMetadata(HostCandidate primary, HostCandidate fallback)
    {
        return primary with
        {
            Username = string.IsNullOrWhiteSpace(primary.Username) ? fallback.Username : primary.Username,
            UseKey = primary.UseKey || fallback.UseKey,
            KeyPath = string.IsNullOrWhiteSpace(primary.KeyPath) ? fallback.KeyPath : primary.KeyPath,
            HardwareId = string.IsNullOrWhiteSpace(primary.HardwareId) ? fallback.HardwareId : primary.HardwareId
        };
    }

    private static async Task<IReadOnlyList<HostCandidate>> LabelDuplicateNetworkInterfacesAsync(
        IEnumerable<HostCandidate> hosts,
        CancellationToken cancellationToken)
    {
        var normalized = hosts
            .Select(host => host with { Name = BaseDisplayName(host.Name) })
            .ToList();
        var labeled = new List<HostCandidate>();

        foreach (var group in normalized.GroupBy(host => host.Name, StringComparer.OrdinalIgnoreCase))
        {
            var items = group.ToList();
            if (items.Count == 2 && LooksLikeSamePhysicalDevice(items))
            {
                var ordered = await OrderByLikelyNetworkSpeedAsync(items, cancellationToken);
                labeled.Add(ordered[0] with { Name = $"{ordered[0].Name} (kabel)" });
                labeled.Add(ordered[1] with { Name = $"{ordered[1].Name} (Wi-Fi)" });
                continue;
            }

            labeled.AddRange(items);
        }

        return SortHosts(labeled);
    }

    private static string BaseDisplayName(string name)
    {
        return Regex.Replace(name, @"\s*\((?:kabel|wi-fi)\)\s*$", "", RegexOptions.IgnoreCase).Trim();
    }

    private static bool LooksLikeSamePhysicalDevice(IReadOnlyList<HostCandidate> hosts)
    {
        if (hosts.Any(host => string.IsNullOrWhiteSpace(host.HardwareId)))
        {
            return false;
        }

        var values = hosts
            .Select(host => MacToUInt64(host.HardwareId))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .Order()
            .ToList();

        return values.Count == 2 && values[1] - values[0] <= 2;
    }

    private static ulong? MacToUInt64(string hardwareId)
    {
        var hex = hardwareId.Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        return ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var value) ? value : null;
    }

    private static async Task<IReadOnlyList<HostCandidate>> OrderByLikelyNetworkSpeedAsync(
        IReadOnlyList<HostCandidate> hosts,
        CancellationToken cancellationToken)
    {
        var measured = new List<(HostCandidate Host, double Latency)>();
        foreach (var host in hosts)
        {
            measured.Add((host, await AveragePingLatencyAsync(host.Address, cancellationToken)));
        }

        return measured
            .OrderBy(item => item.Latency)
            .ThenBy(item => IpSortKey(item.Host.Address))
            .Select(item => item.Host)
            .ToList();
    }

    private static async Task<double> AveragePingLatencyAsync(string address, CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        var latencies = new List<long>();
        for (var i = 0; i < 4; i++)
        {
            try
            {
                var reply = await ping.SendPingAsync(address, 800);
                cancellationToken.ThrowIfCancellationRequested();
                if (reply.Status == IPStatus.Success)
                {
                    latencies.Add(reply.RoundtripTime);
                }
            }
            catch
            {
                // If ICMP is unavailable, IP ordering is used as a stable fallback.
            }
        }

        return latencies.Count == 0 ? double.MaxValue : latencies.Average();
    }

    private static uint IpSortKey(string address)
    {
        return IPAddress.TryParse(address, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork
            ? ToUInt32(parsed)
            : uint.MaxValue;
    }

    private static List<HostCandidate> SortHosts(IEnumerable<HostCandidate> hosts)
    {
        return hosts
            .OrderBy(item => !LooksLikePi(item))
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Address, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool LooksLikePi(HostCandidate candidate)
    {
        var text = $"{candidate.Name} {candidate.Source}".ToLowerInvariant();
        return text.Contains("raspberry", StringComparison.Ordinal) || text.Contains("pi", StringComparison.Ordinal);
    }

    private static string? PrimaryIpv4()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 80);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress FromUInt32(uint value)
    {
        return new IPAddress([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
    }

    private static void Log(string message) => LogWriter.Write(AppPaths.DiscoveryLogPath, message);

    private sealed class DeviceCacheItem
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
        [JsonPropertyName("address")]
        public string Address { get; set; } = "";
        [JsonPropertyName("source")]
        public string Source { get; set; } = "sparad";
        [JsonPropertyName("username")]
        public string? Username { get; set; } = "";
        [JsonPropertyName("use_key")]
        public bool UseKey { get; set; }
        [JsonPropertyName("key_path")]
        public string? KeyPath { get; set; } = "";
        [JsonPropertyName("hardware_id")]
        public string? HardwareId { get; set; } = "";
        [JsonPropertyName("updated_at")]
        public string? UpdatedAt { get; set; }
    }
}
