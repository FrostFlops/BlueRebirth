using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlueOath.Server.Protocols;

/// <summary>
/// 仓库随附的 future-chapter.mod：主线第三篇「未来編」里一张 COMING SOON 占位章节。日服 2023-02 最终热更把原来的
/// 占位 id 17 做成了真实章节（開拓編第三章），Mod 接管它后开拓編少了这一章，未来編里点开它提示「指挥官达到 0 级后开放」。
/// 这里核对 main.lua 的 CHAPTER_ID 与客户端配置：不在 config_chapter 里；大于主线（config_chapter_plot_type 1）
/// 最后一章，客户端 CopyLogic:GetPassChapterInfoByClassIdAndPartId 的 chapterId &gt;= v.id 才不会把它算进已开放；
/// 小于 9000（TeachCopyLogic 把 id &gt;= 9000 的章节都当成教学章节）。并核对接管判定 owns_placeholder 仍守在每个改动处。
/// 只读文件与客户端配置（BLUEOATH_CLIENT_PATH 可指向未热更的 1.4.0 客户端，两者都要通过）。由 Program.cs 注册。
/// </summary>
internal static class FutureChapterModTests
{
    private const int MainPlotType = 1, TeachChapterStart = 9000;

    private static void Assert(bool condition, string message) => TestSupport.Assert(condition, message);

    public static Task ShippedModTest()
    {
        string main = File.ReadAllText(Path.Combine(TestSupport.FindRepositoryRoot(), "Mods", "future-chapter.mod", "main.lua"));
        int chapterId = LuaConstant(main, "CHAPTER_ID");
        int templateId = LuaConstant(main, "LAST_REAL_CHAPTER_ID");
        int futurePart = LuaConstant(main, "FUTURE_PART");

        // 撞上真实章节时 Mod 必须整体不生效：篇调整与三个 CopyLogic 包装都要先确认占位是自己注入的。
        Assert(Regex.IsMatch(main, @"row\.__blueoath_empty_chapter == true") &&
               main.Contains("placeholder.__blueoath_empty_chapter = true", StringComparison.Ordinal),
            "main.lua no longer marks and checks its own placeholder (__blueoath_empty_chapter)");
        Assert(Regex.IsMatch(main, @"local function ensure_future_part\(plot_type\)(?:(?!\nend).)*if not owns_placeholder\(\) then",
                RegexOptions.Singleline),
            "ensure_future_part rearranges the main-story parts without checking owns_placeholder()");
        foreach (string method in new[] { "CheckPlotChapterLock", "CheckPlotChapterItemLock", "GetAllPlotChapterInfoById" })
            Assert(Regex.IsMatch(main, $@"copy_logic\.{method} = function(?:(?!\n  end).)*owns_placeholder\(\)",
                    RegexOptions.Singleline),
                $"the CopyLogic.{method} wrapper treats CHAPTER_ID as the placeholder without checking owns_placeholder()");

        string configDir = ConfigDbLoader.BuildConfigDir(TestSupport.FindClientPath());
        var chapterPlotTypes = new Dictionary<int, int>();
        ConfigDbLoader.LoadRows(configDir, "config_chapter.db", (id, _, json) =>
        {
            using JsonDocument document = JsonDocument.Parse(json);
            chapterPlotTypes[id] = document.RootElement.TryGetProperty("chapter_plot_type", out JsonElement plotType) &&
                                   plotType.ValueKind == JsonValueKind.Number ? plotType.GetInt32() : 0;
        });
        JsonElement? mainPlot = null;
        ConfigDbLoader.LoadRows(configDir, "config_chapter_plot_type.db", (id, _, json) =>
        {
            if (id != MainPlotType) return;
            using JsonDocument document = JsonDocument.Parse(json);
            mainPlot = document.RootElement.Clone();
        });
        Assert(chapterPlotTypes.Count > 0 && mainPlot is not null, $"the client config in {configDir} could not be read");

        int[] chapterList = [.. mainPlot!.Value.GetProperty("chapter_list").EnumerateArray().Select(x => x.GetInt32())];
        int[][] parts = [.. mainPlot.Value.GetProperty("chapter_list2").EnumerateArray()
            .Select(part => part.EnumerateArray().Select(x => x.GetInt32()).ToArray())];
        int lastMainChapter = chapterList.Concat(parts.SelectMany(x => x)).DefaultIfEmpty(0).Max();
        Assert(chapterList.Length > 0 && lastMainChapter > 0,
            $"config_chapter_plot_type {MainPlotType} has no main-story chapters in {configDir}");

        Assert(!chapterPlotTypes.ContainsKey(chapterId),
            $"placeholder CHAPTER_ID {chapterId} is a real config_chapter row (plot type {chapterPlotTypes.GetValueOrDefault(chapterId)}); " +
            "the mod would disable itself");
        Assert(chapterId > lastMainChapter,
            $"placeholder CHAPTER_ID {chapterId} is not above the last main-story chapter {lastMainChapter}, so it would count as opened");
        Assert(chapterId < TeachChapterStart,
            $"placeholder CHAPTER_ID {chapterId} is in the teaching-chapter range (>= {TeachChapterStart})");

        // 占位复制这一章的字段；未来編接在现有的篇之后，不能覆盖真实的篇。
        Assert(chapterPlotTypes.GetValueOrDefault(templateId) == MainPlotType && chapterList.Contains(templateId),
            $"LAST_REAL_CHAPTER_ID {templateId} is not a main-story chapter in {configDir}");
        Assert(futurePart == parts.Length + 1,
            $"FUTURE_PART {futurePart} does not follow the client's {parts.Length} main-story parts");
        return Task.CompletedTask;
    }

    private static int LuaConstant(string source, string name)
    {
        Match match = Regex.Match(source, $@"^local {name} = (\d+)\r?$", RegexOptions.Multiline);
        Assert(match.Success, $"main.lua has no `local {name} = <number>` line");
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }
}
