using System.Diagnostics;
using System.Net;
using System.Text;
using System.Web;
using Newtonsoft.Json.Linq;

namespace ConfigUpdater.Services;

public static class SetAdsRouteService
{
    private static readonly string RepoRoot = Directory.GetCurrentDirectory();
    private static readonly string DataDir = Path.Combine(RepoRoot, "data");
    private static readonly string OutputDir = Path.Combine(RepoRoot, "output");
    private static readonly string ToolsDir = Path.Combine(RepoRoot, "tools");

    private static readonly string ValidTxtPath = Path.Combine(DataDir, "valid.txt");
    private static readonly string TempTxtPath = Path.Combine(DataDir, "temp.txt");
    private static readonly string ValidCsvPath = Path.Combine(DataDir, "valid.csv");

    private static readonly string XrayKnifePath = Path.Combine(ToolsDir, "xray-knife");
    private static readonly string V2ray2JsonPath = Path.Combine(ToolsDir, "v2ray2json.py");

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static readonly Dictionary<string, GeoInfo> GeoCache = new(StringComparer.OrdinalIgnoreCase);

    private sealed class GeoInfo
    {
        public string? CountryCode { get; init; }
        public string? Flag { get; init; }
        public string? CityName { get; init; }
        public string? Isp { get; init; }
    }

    public static async Task RunOnceAsync()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(OutputDir);
        Directory.CreateDirectory(ToolsDir);

        await GetConfigsAsync();
    }

    private static async Task GetConfigsAsync()
    {
        await File.WriteAllTextAsync(ValidTxtPath, string.Empty);

        string[] urls =
        {
            "https://raw.githubusercontent.com/barry-far/V2ray-config/main/Sub1.txt",
            "https://raw.githubusercontent.com/SoliSpirit/v2ray-configs/main/Subscriptions/Sub5.txt",
            "https://raw.githubusercontent.com/10ium/V2rayCollectorLite/main/ss_iran.txt",
            "https://raw.githubusercontent.com/iboxz/free-v2ray-collector/main/main/mix"
        };

        foreach (var url in urls)
        {
            Console.WriteLine($"Fetching configs from: {url}");

            await RunProcessAsync(
                fileName: XrayKnifePath,
                arguments: $"subs fetch -u \"{url}\" -o \"{TempTxtPath}\"");

            if (!File.Exists(TempTxtPath))
                continue;

            var currentValid = await File.ReadAllTextAsync(ValidTxtPath);
            var tempContent = await File.ReadAllTextAsync(TempTxtPath);

            await File.WriteAllTextAsync(ValidTxtPath, currentValid + tempContent);
        }

        Console.WriteLine("Testing configs with xray-knife...");

        await RunProcessAsync(
            fileName: XrayKnifePath,
            arguments:
                $"http -p -a 2000 -d 500 -u https://music.youtube.com/ -s -f \"{ValidTxtPath}\" -t 30 -x csv -o \"{ValidCsvPath}\"",
                timeoutMs: 30 * 60 * 1000); // 30 minutes

        await UpdateConfigsAsync();
    }

    private static async Task UpdateConfigsAsync()
    {
        string? bestsVpnConfigs = null;
        string? netherlandsVpnConfigs = null;
        string? germanyVpnConfigs = null;
        string? usaVpnConfigs = null;
        string? canadaVpnConfigs = null;
        string? englandVpnConfigs = null;
        string? franceVpnConfigs = null;
        string? spainVpnConfigs = null;

        var configNum = 0;

        if (!File.Exists(ValidCsvPath))
        {
            Console.WriteLine("valid.csv not found.");
            return;
        }

        using var reader = new StreamReader(ValidCsvPath);

        while (await reader.ReadLineAsync() is { } line)
        {
            var parts = line.Split(',');
            if (parts.Length < 10)
                continue;

            var vpnConfig = parts[0]?.Trim();

			if (!line.Contains("passed", StringComparison.OrdinalIgnoreCase) &&
				!vpnConfig.Contains("hy2://", StringComparison.OrdinalIgnoreCase) &&
				!vpnConfig.Contains("hysteria2://", StringComparison.OrdinalIgnoreCase))
                continue;
			
            if (string.IsNullOrWhiteSpace(vpnConfig) || vpnConfig.Length < 10)
                continue;

            var configForValidation = vpnConfig.Contains("vmess://", StringComparison.OrdinalIgnoreCase)
                ? vpnConfig
                : vpnConfig.Split('#')[0];

            if (!vpnConfig.Contains("hy2://", StringComparison.OrdinalIgnoreCase) &&
				!vpnConfig.Contains("hysteria2://", StringComparison.OrdinalIgnoreCase) &&
				await ConvertConfigAsync(configForValidation) == null)
                continue;

            await Task.Delay(1500);

            string? countryCode = null;
            string? isp = null;

            if (vpnConfig.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var vmessJson = DecodeVmessJson(vpnConfig);
                    if (vmessJson == null)
                        continue;

                    var routeHost = GetVmessRouteHost(vmessJson);

					if (parts.Length > 4 && string.Equals(parts[1], "passed", StringComparison.OrdinalIgnoreCase))
						routeHost = parts[4];

					if (string.IsNullOrWhiteSpace(routeHost))
                        continue;

					var countryInfo = await GetCountryInfoAsync(routeHost);
                    isp = countryInfo.Isp;
					countryCode = countryInfo.CountryCode;

					if (ShouldSkipCountry(countryCode))
                        continue;

                    if (!IsAllowedIsp(isp))
                    {
                        Console.WriteLine("ISP rejected");
                        continue;
                    }

                    Console.WriteLine("ISP accepted: " + isp);

                    var flag = CountryCodeToFlag(countryCode!);
                    var city = countryInfo.CityName ?? string.Empty;

                    vmessJson["ps"] = $"{flag} {city}".Trim();
                    vpnConfig = "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(vmessJson.ToString()));
                }
                catch (Exception e)
                {
                    Console.WriteLine("VMESS processing error: " + e);
                    continue;
                }
            }
            else if (vpnConfig.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) ||
                     vpnConfig.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase) ||
                     vpnConfig.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase) ||
                     vpnConfig.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var routeHost = GetVlessOrTrojanOrHysteriaRouteHost(vpnConfig);

					if (parts.Length > 4 && string.Equals(parts[1], "passed", StringComparison.OrdinalIgnoreCase))
						routeHost = parts[4];

					if (string.IsNullOrWhiteSpace(routeHost))
                        continue;

					var countryInfo = await GetCountryInfoAsync(routeHost);
                    isp = countryInfo.Isp;
					countryCode = countryInfo.CountryCode;

					if (ShouldSkipCountry(countryCode))
                        continue;

                    if (!IsAllowedIsp(isp))
                    {
                        Console.WriteLine("ISP rejected");
                        continue;
                    }

                    Console.WriteLine("ISP accepted: " + isp);

                    var flag = CountryCodeToFlag(countryCode!);
                    var city = countryInfo.CityName ?? string.Empty;

                    vpnConfig = vpnConfig.Split('#')[0] + "#" + $"{flag} {city}".Trim();
                }
                catch (Exception e)
                {
                    Console.WriteLine("VLESS/TROJAN/HYSTERIA processing error: " + e);
                    continue;
                }
            }
            else if (vpnConfig.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var routeHost = GetShadowsocksRouteHost(vpnConfig);

					if (parts.Length > 4 && string.Equals(parts[1], "passed", StringComparison.OrdinalIgnoreCase))
						routeHost = parts[4];

					if (string.IsNullOrWhiteSpace(routeHost))
                        continue;

					var countryInfo = await GetCountryInfoAsync(routeHost);
                    isp = countryInfo.Isp;
					countryCode = countryInfo.CountryCode;

					if (ShouldSkipCountry(countryCode))
                        continue;

                    if (!IsAllowedIsp(isp))
                    {
                        Console.WriteLine("ISP rejected");
                        continue;
                    }

                    Console.WriteLine("ISP accepted: " + isp);

                    var flag = CountryCodeToFlag(countryCode!);
                    var city = countryInfo.CityName ?? string.Empty;

                    vpnConfig = vpnConfig.Split('#')[0] + "#" + $"{flag} {city}".Trim();
                }
                catch (Exception e)
                {
                    Console.WriteLine("SS processing error: " + e);
                    continue;
                }
            }
            else
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(countryCode))
                continue;

            AppendUnique(ref bestsVpnConfigs, vpnConfig);

            switch (countryCode.ToLowerInvariant())
            {
                case "nl":
                    AppendUnique(ref netherlandsVpnConfigs, vpnConfig);
                    break;
                case "de":
                    AppendUnique(ref germanyVpnConfigs, vpnConfig);
                    break;
                case "us":
                    AppendUnique(ref usaVpnConfigs, vpnConfig);
                    break;
                case "ca":
                    AppendUnique(ref canadaVpnConfigs, vpnConfig);
                    break;
                case "gb":
                    AppendUnique(ref englandVpnConfigs, vpnConfig);
                    break;
                case "fr":
                    AppendUnique(ref franceVpnConfigs, vpnConfig);
                    break;
                case "es":
                    AppendUnique(ref spainVpnConfigs, vpnConfig);
                    break;
            }

            configNum++;
            Console.WriteLine($"Config {configNum} added");
        }

        await WriteIfNotNullAsync("Bests.txt", bestsVpnConfigs);
        await WriteIfNotNullAsync("Netherlands.txt", netherlandsVpnConfigs);
        await WriteIfNotNullAsync("Germany.txt", germanyVpnConfigs);
        await WriteIfNotNullAsync("USA.txt", usaVpnConfigs);
        await WriteIfNotNullAsync("Canada.txt", canadaVpnConfigs);
        await WriteIfNotNullAsync("England.txt", englandVpnConfigs);
        await WriteIfNotNullAsync("France.txt", franceVpnConfigs);
        await WriteIfNotNullAsync("Spain.txt", spainVpnConfigs);

        Console.WriteLine("Update configs finished.");
    }

    private static async Task<string?> ConvertConfigAsync(string config)
	{
    	try
    	{
        	if (string.IsNullOrWhiteSpace(config))
            	return null;

        	var output = await RunProcessAsync(
            	fileName: "python3",
            	arguments: $"\"{V2ray2JsonPath}\" \"{config}\"",
            	timeoutMs: 10000);

        	return output.Contains("_comment", StringComparison.OrdinalIgnoreCase) ? output : null;
    	}
    	catch (Exception e)
    	{
        	Console.WriteLine("ConvertConfigAsync error: " + e);
        	return null;
    	}
	}

    private static JObject? DecodeVmessJson(string vmessConfig)
    {
        try
        {
            var raw = vmessConfig.Replace("vmess://", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (!IsBase64String(raw))
                return null;

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(raw));
            return JObject.Parse(json);
        }
        catch (Exception e)
        {
            Console.WriteLine("DecodeVmessJson error: " + e);
            return null;
        }
    }

    private static string? GetVmessRouteHost(JObject vmessJson)
    {
        var sni = vmessJson["sni"]?.ToString();
        if (!string.IsNullOrWhiteSpace(sni))
            return sni;

        var host = vmessJson["host"]?.ToString();
        if (!string.IsNullOrWhiteSpace(host))
            return host;

        var add = vmessJson["add"]?.ToString();
        return string.IsNullOrWhiteSpace(add) ? null : add;
    }

    private static string? GetVlessOrTrojanOrHysteriaRouteHost(string config)
    {
        try
        {
            var questionIndex = config.IndexOf('?');
            if (questionIndex >= 0)
            {
                var afterQuestion = config[(questionIndex + 1)..];
                var hashIndex = afterQuestion.IndexOf('#');
                if (hashIndex >= 0)
                    afterQuestion = afterQuestion[..hashIndex];

                var paramsCollection = HttpUtility.ParseQueryString(afterQuestion);

                var sni = paramsCollection.Get("sni");
                if (!string.IsNullOrWhiteSpace(sni))
                    return sni;

                var host = paramsCollection.Get("host");
                if (!string.IsNullOrWhiteSpace(host))
                    return host;
            }

            var beforeQuery = config.Split('?')[0];
            var atIndex = beforeQuery.IndexOf('@');
            if (atIndex < 0)
                return null;

            var hostPort = beforeQuery[(atIndex + 1)..];
            var colonIndex = hostPort.IndexOf(':');
            if (colonIndex > 0)
                return hostPort[..colonIndex];

            return hostPort;
        }
        catch (Exception e)
        {
            Console.WriteLine("GetVlessOrTrojanOrHysteriaRouteHost error: " + e);
            return null;
        }
    }

    private static string? GetShadowsocksRouteHost(string config)
    {
        try
        {
            var beforeHash = config.Split('#')[0];
            var atIndex = beforeHash.IndexOf('@');
            if (atIndex < 0)
                return null;

            var hostPort = beforeHash[(atIndex + 1)..];
            var colonIndex = hostPort.IndexOf(':');
            if (colonIndex > 0)
                return hostPort[..colonIndex];

            return hostPort;
        }
        catch (Exception e)
        {
            Console.WriteLine("GetShadowsocksRouteHost error: " + e);
            return null;
        }
    }

    private static bool IsBase64String(string base64)
    {
        try
        {
            var buffer = new Span<byte>(new byte[base64.Length]);
            return Convert.TryFromBase64String(base64, buffer, out _);
        }
        catch
        {
            return false;
        }
    }

    private static bool ShouldSkipCountry(string? countryCode)
    {
        return string.IsNullOrWhiteSpace(countryCode) ||
               string.Equals(countryCode, "null", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(countryCode, "ru", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeCountryCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (trimmed.Length < 2)
            return null;

        return trimmed.ToLowerInvariant();
    }

    private static bool IsAllowedIsp(string? isp)
    {
        if (string.IsNullOrWhiteSpace(isp))
            return false;

		return true;
    }

    private static string CountryCodeToFlag(string countryCode)
    {
        const int flagOffset = 0x1F1E6;
        const int asciiOffset = 0x41;

        var cc = countryCode.ToUpperInvariant();
        if (cc.Length < 2)
            return string.Empty;

        var firstChar = char.ConvertToUtf32(cc, 0) - asciiOffset + flagOffset;
        var secondChar = char.ConvertToUtf32(cc, 1) - asciiOffset + flagOffset;
        return char.ConvertFromUtf32(firstChar) + char.ConvertFromUtf32(secondChar);
    }

    private static async Task<GeoInfo> GetCountryInfoAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new GeoInfo();

        if (GeoCache.TryGetValue(url, out var cached))
            return cached;

        try
        {
            using var response = await HttpClient.GetAsync("http://ip-api.com/json/" + url);
            Console.WriteLine("Country info response: " + response.StatusCode);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                var fail = new GeoInfo();
                GeoCache[url] = fail;
                return fail;
            }

            var json = await response.Content.ReadAsStringAsync();
            var obj = JObject.Parse(json);

            var status = obj["status"]?.ToString();
            if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Country info status: " + status);
                var fail = new GeoInfo();
                GeoCache[url] = fail;
                return fail;
            }

            string? countryCode = obj["countryCode"]?.ToString();
            string? cityName = obj["city"]?.ToString();
            string? isp = obj["isp"]?.ToString();

            string? flag = null;
            if (!string.IsNullOrWhiteSpace(countryCode) && countryCode.Length >= 2)
            {
                countryCode = countryCode.ToUpperInvariant();
                flag = CountryCodeToFlag(countryCode);
            }

            var result = new GeoInfo
            {
                CountryCode = countryCode,
                Flag = flag,
                CityName = cityName,
                Isp = isp
            };

            GeoCache[url] = result;
            return result;
        }
        catch (Exception e)
        {
            Console.WriteLine("GetCountryInfoAsync error: " + e);
            var fail = new GeoInfo();
            GeoCache[url] = fail;
            return fail;
        }
    }

    private static async Task<string> RunProcessAsync(string fileName, string arguments, int timeoutMs = 120000)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            Arguments = arguments
        };

        using var process = Process.Start(processStartInfo);
        if (process == null)
            throw new InvalidOperationException($"Failed to start process: {fileName}");

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        var waitTask = process.WaitForExitAsync();
        var completed = await Task.WhenAny(waitTask, Task.Delay(timeoutMs));

        if (completed != waitTask)
        {
            try
            {
                process.Kill(true);
            }
            catch
            {
                // ignored
            }

            throw new TimeoutException($"Process timed out: {fileName} {arguments}");
        }

        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        if (process.ExitCode != 0)
        {
            Console.WriteLine($"Process failed: {fileName} {arguments}");
            if (!string.IsNullOrWhiteSpace(stdErr))
                Console.WriteLine(stdErr);
        }

        return stdOut;
    }

    private static void AppendUnique(ref string? target, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (target == null)
        {
            target = value + Environment.NewLine;
            return;
        }

        if (!target.Contains(value, StringComparison.Ordinal))
            target += value + Environment.NewLine;
    }

    private static async Task WriteIfNotNullAsync(string fileName, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return;

        var path = Path.Combine(OutputDir, fileName);
        await File.WriteAllTextAsync(path, content);
    }
}
