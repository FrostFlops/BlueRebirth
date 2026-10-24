using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// 仓库随附的 cn-exclusive.mod 的语音时长（SETTINGS.voice.durations）。data.lua 每个语音库的 durations 与 events 出自
/// tools/export-cn-exclusive.py 的同一次生成：每个改写的事件恰好一行，jp 是游戏发出的日服模型事件名（它的 ID 现在播放
/// 中文录音），cn 是那段中文录音的事件名；两个名字的 Wwise ID（FNV-1 32）与事件改写对得上，并且都在客户端音频库的
/// 清单（GeneratedSoundBanks/Windows/&lt;库&gt;.txt）里。时长本身由 main.lua 在运行时从客户端的 SoundBanksInfoProfile
/// 读取，这里不核对。仓库里没有 Lua 运行环境，main.lua 只做静态核对：时长补丁接在 LoadSFX 钩子里、语音设置之后，
/// 并且只取中文语音生效、所在的库确实带着改写挂上的行；写入、跳过与重新应用的行为要在客户端里验证。由 Program.cs 注册。
/// </summary>
internal static class CnExclusiveVoiceTests
{
    // 宁海、平海各自的语音库改写 38 / 37 个事件，宿舍与情书共享库里各互换 2 个。
    private static readonly Dictionary<string, int> ExpectedRows = new() { ["cn_voice_ninghai"] = 40, ["cn_voice_pinghai"] = 39 };

    private static void Assert(bool condition, string message) => TestSupport.Assert(condition, message);

    private sealed record VoiceEvent(string Feature, bool Swap, uint From, uint To, string Name);

    private sealed record DurationRow(string Feature, string Jp, string Cn);

    public static Task ShippedDurationsTest()
    {
        string modDir = Path.Combine(TestSupport.FindRepositoryRoot(), "Mods", "cn-exclusive.mod");
        string data = File.ReadAllText(Path.Combine(modDir, "data.lua")).ReplaceLineEndings("\n");
        string main = File.ReadAllText(Path.Combine(modDir, "main.lua")).ReplaceLineEndings("\n");
        Assert(Regex.IsMatch(main, @"^\s*durations = true,", RegexOptions.Multiline),
            "main.lua SETTINGS.voice has no durations switch that defaults to true");
        string hook = Function(main, "wrap_load_sfx");
        Assert(Regex.IsMatch(hook, @"safely\(""voice setup"", setup_voice\).*safely\(""duration patch"", apply_durations\)" +
                @".*return previous\(\.\.\.\)", RegexOptions.Singleline),
            "main.lua wrap_load_sfx does not apply the durations after the voice setup and before the original LoadSFX");
        string gate = Function(main, "duration_rows");
        Assert(gate.Contains("local served = voice_served[bank.target]", StringComparison.Ordinal) &&
               Regex.IsMatch(gate, @"if voice_active\[row\.feature\] and .*served\[row\.feature\] then"),
            "main.lua duration_rows does not require both an active Chinese voice and a bank served with it");

        int voice = data.IndexOf("\n  voice = {\n", StringComparison.Ordinal);
        int captions = voice < 0 ? -1 : data.IndexOf("\n    captions = {", voice, StringComparison.Ordinal);
        Assert(voice >= 0 && captions > voice, "data.lua has no voice.banks block");
        MatchCollection banks = Regex.Matches(data[voice..captions],
            @"\n      \{\n        source = ""(?<source>\w+)"",.*?\n        target = ""(?<target>\w+)"",.*?\n      \},",
            RegexOptions.Singleline);
        Assert(banks.Count > 0, "data.lua lists no voice banks");

        string bankDir = Path.Combine(TestSupport.FindClientPath(), "blueoath_Data", "StreamingAssets", "Audio",
            "GeneratedSoundBanks", "Windows");
        var counts = new Dictionary<string, int>();
        foreach (Match bank in banks)
        {
            string source = bank.Groups["source"].Value, target = bank.Groups["target"].Value;
            List<VoiceEvent> events = [.. Regex.Matches(Block(bank.Value, "events"),
                    @"\{\s*feature = ""(\w+)"",\s*(swap = true,\s*)?from = (\d+),\s*to = (\d+),\s*name = ""(\w+)"",?\s*\}")
                .Select(m => new VoiceEvent(m.Groups[1].Value, m.Groups[2].Success, UInt(m.Groups[3].Value),
                    UInt(m.Groups[4].Value), m.Groups[5].Value))];
            List<DurationRow> rows = [.. Regex.Matches(Block(bank.Value, "durations"),
                    @"\{\s*feature = ""(\w+)"",\s*jp = ""(\w+)"",\s*cn = ""(\w+)"",?\s*\}")
                .Select(m => new DurationRow(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value))];
            Assert(events.Count > 0 && rows.Count == events.Count,
                $"data.lua bank {source}: {rows.Count} durations rows for {events.Count} events");

            Dictionary<string, uint> sourceEvents = BankEvents(bankDir, source);
            Dictionary<string, uint> targetEvents = source == target ? sourceEvents : BankEvents(bankDir, target);
            foreach ((VoiceEvent ev, DurationRow row) in events.Zip(rows))
            {
                // 舰娘库整库改名：中文事件 name 的 ID 改成 name_jp 的 ID。共享库互换：日服模型事件 name 的 ID 与中文事件互换。
                bool ok = ev.Swap
                    ? row.Jp == ev.Name && Fnv1(row.Jp) == ev.From && Fnv1(row.Cn) == ev.To &&
                      Listed(sourceEvents, row.Jp, ev.From) && Listed(sourceEvents, row.Cn, ev.To)
                    : row.Cn == ev.Name && row.Jp == ev.Name + "_jp" && Fnv1(row.Cn) == ev.From && Fnv1(row.Jp) == ev.To &&
                      Listed(sourceEvents, row.Cn, ev.From) && Listed(targetEvents, row.Jp, ev.To);
                Assert(ok && row.Feature == ev.Feature,
                    $"data.lua bank {source}: durations row {row.Jp} <- {row.Cn} does not match the event {ev.Name}");
                counts[row.Feature] = counts.GetValueOrDefault(row.Feature) + 1;
            }
        }
        Assert(counts.Count == ExpectedRows.Count && ExpectedRows.All(x => counts.GetValueOrDefault(x.Key) == x.Value),
            "data.lua durations rows per feature: " + string.Join(", ", counts.Select(x => $"{x.Key} {x.Value}")));
        return Task.CompletedTask;
    }

    /// <summary>库条目里 "name = {" 到同缩进 "}," 之间的文本；没有这一项时为空。</summary>
    private static string Block(string bank, string name)
    {
        Match match = Regex.Match(bank, @"\n        " + name + @" = \{(.*?)\n        \},", RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value : "";
    }

    /// <summary>main.lua 里顶层 "local function name(" 到行首 "end" 之间的函数体；没有这个函数时为空。</summary>
    private static string Function(string main, string name)
    {
        Match match = Regex.Match(main, @"\nlocal function " + name + @"\(.*?\)\n(.*?)\nend\n", RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value : "";
    }

    private static uint UInt(string text) => uint.Parse(text, CultureInfo.InvariantCulture);

    private static bool Listed(Dictionary<string, uint> events, string name, uint id) =>
        events.TryGetValue(name, out uint listed) && listed == id;

    /// <summary>音频库清单 &lt;库&gt;.txt 的 Event 段：事件名 -> ID。</summary>
    private static Dictionary<string, uint> BankEvents(string bankDir, string bank)
    {
        string path = Path.Combine(bankDir, bank + ".txt");
        Assert(File.Exists(path), $"the client bank listing {path} is missing");
        var events = new Dictionary<string, uint>(StringComparer.Ordinal);
        string? section = null;
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0)
                section = null;
            else if (line[0] != '\t')
                section = line.Split('\t')[0];
            else if (section == "Event")
            {
                string[] parts = line.Split('\t');
                if (parts.Length > 2) events[parts[2]] = UInt(parts[1]);
            }
        }
        return events;
    }

    /// <summary>AK::SoundEngine::GetIDFromString：UTF-8 字节上的 FNV-1 32 位哈希，只把 A–Z 转成小写。</summary>
    private static uint Fnv1(string name)
    {
        uint hash = 2166136261;
        foreach (byte value in Encoding.UTF8.GetBytes(name))
        {
            hash = unchecked(hash * 16777619);
            hash ^= value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;
        }
        return hash;
    }
}
