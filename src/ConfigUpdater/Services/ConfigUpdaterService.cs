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

    // اگر فایل باینری xray-knife را در tools گذاشتی، این مسیر درست است
    private static readonly string XrayKnifePath = Path.Combine(ToolsDir, "xray-knife");

    // فایل python را هم در tools قرار بده
    private static readonly string V2ray2JsonPath = Path.Combine(ToolsDir, "v2ray2json.py");

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static readonly Dictionary<string, (string? countryCode, string? flag, string? cityName, string? isp)> GeoCache = new();

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
            "https://raw.githubusercontent.com/barry-far/V2ray-config/main/All_Configs_base64_Sub.txt",
            "https://raw.githubusercontent.com/SoliSpirit/v2ray-configs/refs/heads/main/all_configs.txt",
            "https://raw.githubusercontent.com/10ium/V2rayCollectorLite/main/mixed_iran.txt",
            "https://raw.githubusercontent.com/iboxz/free-v2ray-collector/main/main/mix",
            "https://raw.githubusercontent.com/galaxy-vpn-2023/vpn/refs/heads/main/configs.txt"
        };

        foreach (var url in urls)
        {
            await RunProcessAsync(
                fileName: XrayKnifePath,
                arguments: $"subs fetch -u \"{url}\" -o \"{TempTxtPath}\"");

            if (!File.Exists(TempTxtPath))
                continue;

            var currentValid = await File.ReadAllTextAsync(ValidTxtPath);
            var tempContent = await File.ReadAllTextAsync(TempTxtPath);

            await File.WriteAllTextAsync(ValidTxtPath, currentValid + tempContent);
        }

        await RunProcessAsync(
            fileName: XrayKnifePath,
            arguments:
            $"http -p -a 2000 -d 500 -u https://music.youtube.com/ -s -f \"{ValidTxtPath}\" -t 30 -x csv -o \"{ValidCsvPath}\"");

        await UpdateConfigsAsync();
    }

    private static async Task UpdateConfigsAsync()
    {
        const int flagOffset = 0x1F1E6;
        const int asciiOffset = 0x41;

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
            if (!line.Contains("passed"))
                continue;

            var parts = line.Split(',');
            if (parts.Length < 10)
                continue;

            var vpnConfig = parts[0];
            if (vpnConfig.Length < 10)
                continue;

            var configForValidation = vpnConfig.Contains("vmess://") ? vpnConfig : vpnConfig.Split('#')[0];
            if (await ConvertConfigAsync(configForValidation) == null)
                continue;

            await Task.Delay(1500);

            string? countryCode = null;
            string? isp = null;

            if (vpnConfig.Contains("vmess://"))
            {
                try
                {
                    if (!IsBase64String(vpnConfig.Replace("vmess://", "")))
                        continue;

                    vpnConfig = Encoding.UTF8.GetString(Convert.FromBase64String(vpnConfig.Replace("vmess://", "")));
                    dynamic t = JObject.Parse(vpnConfig);

                    string url;
                    if (!object.ReferenceEquals(t.sni, null))
                        url = t.sni.ToString();
                    else if (!object.ReferenceEquals(t.host, null))
                        url = t.host.ToString();
                    else
                        url = t.add.ToString();

                    var countryInfo = await GetCountryInfoAsync(url);
                    isp = countryInfo.isp;

                    countryCode = parts[9];

                    if (string.IsNullOrWhiteSpace(countryCode) || countryCode.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                        countryCode.Equals("ru", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (isp == null ||
                        (!isp.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) &&
                         !isp.Contains("asiatech", StringComparison.OrdinalIgnoreCase)))
                    {
                        Console.WriteLine("ISP: " + isp);
                        continue;
                    }

                    Console.WriteLine("ISP: " + isp);

                    var firstChar = char.ConvertToUtf32(countryCode, 0) - asciiOffset + flagOffset;
                    var secondChar = char.ConvertToUtf32(countryCode, 1) - asciiOffset + flagOffset;
                    var flag = char.ConvertFromUtf32(firstChar) + char.ConvertFromUtf32(secondChar);

                    if (parts.Length > 4 && parts[4] != "null")
                    {
                        await Task.Delay(500);
                        countryInfo = await GetCountryInfoAsync(parts[4]);
                    }

                    t.ps = flag + " " + countryInfo.cityName;
                    vpnConfig = "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(t.ToString()));
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    continue;
                }
            }
            else if (vpnConfig.Contains("vless://") || vpnConfig.Contains("trojan://"))
            {
                try
                {
                    var queryPart = vpnConfig.Split('?')[1].Split('#')[0];
                    var paramsCollection = HttpUtility.ParseQueryString(queryPart);

                    string url;
                    if (paramsCollection.Get("sni") != null)
                        url = paramsCollection.Get("sni")!;
                    else if (paramsCollection.Get("host") != null)
                        url = paramsCollection.Get("host")!;
                    else
                        url = vpnConfig.Split('?')[0].Split('@')[1].Split(':')[0];

                    var countryInfo = await GetCountryInfoAsync(url);
                    isp = countryInfo.isp;

                    countryCode = parts[9];

                    if (string.IsNullOrWhiteSpace(countryCode) || countryCode.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                        countryCode.Equals("ru", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (isp == null ||
                        (!isp.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) &&
                         !isp.Contains("asiatech", StringComparison.OrdinalIgnoreCase)))
                    {
                        Console.WriteLine("ISP: " + isp);
                        continue;
                    }

                    Console.WriteLine("ISP: " + isp);

                    var firstChar = char.ConvertToUtf32(countryCode, 0) - asciiOffset + flagOffset;
                    var secondChar = char.ConvertToUtf32(countryCode, 1) - asciiOffset + flagOffset;
                    var flag = char.ConvertFromUtf32(firstChar) + char.ConvertFromUtf32(secondChar);

                    if (parts.Length > 4 && parts[4] != "null")
                    {
                        await Task.Delay(500);
                        countryInfo = await GetCountryInfoAsync(parts[4]);
                    }

                    vpnConfig = vpnConfig.Split('#')[0] + "#" + flag + " " + countryInfo.cityName;
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    continue;
                }
            }
            else if (vpnConfig.Contains("ss://"))
            {
                try
                {
                    var url = vpnConfig.Split('@')[1].Split('#')[0].Split(':')[0];
                    var countryInfo = await GetCountryInfoAsync(url);
                    isp = countryInfo.isp;

                    countryCode = parts[9];

                    if (string.IsNullOrWhiteSpace(countryCode) || countryCode.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                        countryCode.Equals("ru", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (isp == null ||
                        (!isp.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) &&
                         !isp.Contains("asiatech", StringComparison.OrdinalIgnoreCase)))
                    {
                        Console.WriteLine("ISP: " + isp);
                        continue;
                    }

                    Console.WriteLine("ISP: " + isp);

                    var firstChar = char.ConvertToUtf32(countryCode, 0) - asciiOffset + flagOffset;
                    var secondChar = char.ConvertToUtf32(countryCode, 1) - asciiOffset + flagOffset;
                    var flag = char.ConvertFromUtf32(firstChar) + char.ConvertFromUtf32(secondChar);

                    if (parts.Length > 4 && parts[4] != "null")
                    {
                        await Task.Delay(500);
                        countryInfo = await GetCountryInfoAsync(parts[4]);
                    }

                    vpnConfig = vpnConfig.Split('#')[0] + "#" + flag + " " + countryInfo.cityName;
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    continue;
                }
            }
            else
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(countryCode) || countryCode.Equals("null", StringComparison.OrdinalIgnoreCase))
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
            Console.WriteLine("Config " + configNum + " Added");
        }

        await WriteIfNotNullAsync("Bests.txt", bestsVpnConfigs);
        await WriteIfNotNullAsync("Netherlands.txt", netherlandsVpnConfigs);
        await WriteIfNotNullAsync("Germany.txt", germanyVpnConfigs);
        await WriteIfNotNullAsync("USA.txt", usaVpnConfigs);
        await WriteIfNotNullAsync("Canada.txt", canadaVpnConfigs);
        await WriteIfNotNullAsync("England.txt", englandVpnConfigs);
        await WriteIfNotNullAsync("France.txt", franceVpnConfigs);
        await WriteIfNotNullAsync("Spain.txt", spainVpnConfigs);

        Console.WriteLine("Update Configs Finished");
    }

    private static async Task<string?> ConvertConfigAsync(string config)
    {
        try
        {
            var output = await RunProcessAsync(
                fileName: "python3",
                arguments: $"\"{V2ray2JsonPath}\" \"{EscapeForShellArg(config)}\"",
                timeoutMs: 5000);

            return output.Contains("_comment") ? output : null;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
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
        catch (Exception e)
        {
            Console.WriteLine(e);
            return false;
        }
    }

    private static async Task<(string? countryCode, string? flag, string? cityName, string? isp)> GetCountryInfoAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return (null, null, null, null);

        if (GeoCache.TryGetValue(url, out var cached))
            return cached;

        const int flagOffset = 0x1F1E6;
        const int asciiOffset = 0x41;

        try
        {
            using var response = await HttpClient.GetAsync("http://ip-api.com/json/" + url);
            Console.WriteLine("Country Info Response " + response);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                Console.WriteLine("Country Info Response Code " + response.StatusCode);
                var fail = (null, null, null, null);
                GeoCache[url] = fail;
                return fail;
            }

            var json = await response.Content.ReadAsStringAsync();
            dynamic responseJson = JObject.Parse(json);

            if (!responseJson.status.ToString().Equals("success", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Country Info Response Status " + responseJson.status);
                var fail = (null, null, null, null);
                GeoCache[url] = fail;
                return fail;
            }

            var countryCode = responseJson.countryCode.ToString();
            var cityName = responseJson.city.ToString();
            var isp = responseJson.isp.ToString();

            var firstChar = char.ConvertToUtf32(countryCode, 0) - asciiOffset + flagOffset;
            var secondChar = char.ConvertToUtf32(countryCode, 1) - asciiOffset + flagOffset;
            var flag = char.ConvertFromUtf32(firstChar) + char.ConvertFromUtf32(secondChar);

            var result = (countryCode, flag, cityName, isp);
            GeoCache[url] = result;
            return result;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            var fail = (null, null, null, null);
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
            Arguments = arguments
        };

        using var process = Process.Start(processStartInfo);
        if (process == null)
            throw new InvalidOperationException($"Failed to start process: {fileName}");

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        var exited = process.WaitForExit(timeoutMs);
        if (!exited)
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
            Console.WriteLine(stdErr);
        }

        return stdOut;
    }

    private static void AppendUnique(ref string? target, string value)
    {
        if (target == null)
        {
            target = value + Environment.NewLine;
            return;
        }

        if (!target.Contains(value))
            target += value + Environment.NewLine;
    }

    private static async Task WriteIfNotNullAsync(string fileName, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return;

        var path = Path.Combine(OutputDir, fileName);
        await File.WriteAllTextAsync(path, content);
    }

    private static string EscapeForShellArg(string value)
    {
        return value.Replace("\"", "\\\"");
    }
}
