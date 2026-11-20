namespace HotkeyHud.Core;

/// <summary>
/// 把「应用档案 + 系统档案」变成某一页的 <see cref="HudModel"/>。
/// <para>
/// §3.2 v1.5：匹配规则是**包含**该修饰键，不再是完全相等——Ctrl 面板要能看到
/// <c>Ctrl+Shift+K</c>、<c>Ctrl+Space</c>。
/// </para>
/// <para>
/// §3.2 v1.7：排序改为**组优先**——先按档案里的组顺序，组内再按修饰键数量升序
/// （<c>Ctrl+S</c> 在 <c>Ctrl+Shift+S</c> 之前），同数量保持档案里的原顺序。
/// v1.5 的「全局按修饰键数排序」会把同一组打散，Word 第 1 页因此出现 5 个组名。
/// </para>
/// <para>
/// §4.3 v1.7：熟记名单不再剔除条目，只把命中的组合降权到 <see cref="GeneralGroupTitle"/> 组，
/// 该组固定排在最后一页。
/// </para>
/// </summary>
public static class HudBuilder
{
    public const string DefaultGroupTitle = "常规";

    /// <summary>被降权的通用键所在组名（§4.3 v1.7）。固定排在所有应用组之后。</summary>
    public const string GeneralGroupTitle = "通用";

    /// <summary>每页条目上限（§6.1）。</summary>
    public const int PageSize = 12;

    public static HudModel Build(
        AppShortcutProfile? profile,
        Modifier modifier,
        KnownShortcutFilter filter,
        string fallbackAppName = "")
        => Build(profile, systemProfile: null, modifier, filter, fallbackAppName, pageIndex: 0);

    public static HudModel Build(
        AppShortcutProfile? profile,
        AppShortcutProfile? systemProfile,
        Modifier modifier,
        KnownShortcutFilter filter,
        string fallbackAppName = "",
        int pageIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(filter);

        List<Match> matches = Collect(profile, systemProfile, modifier, filter, out int deferred);

        // §3.2 v1.6：未收录应用不再拿系统档案的零星 Ctrl 条目冒充内容。
        // 除 Win 之外一律走引导空态——Win 面板的全部内容本来就是系统快捷键。
        if (profile is null && modifier != Modifier.Win)
        {
            matches.Clear();
        }

        if (matches.Count == 0)
        {
            // 未收录应用：返回空模型，绝不混入其他应用条目（§3.2 空结果规则）
            return new HudModel
            {
                AppName = ResolveAppName(profile, fallbackAppName),
                Modifier = modifier,
                Groups = Array.Empty<HudGroup>(),
                TotalCount = 0,
                DeferredKnownCount = deferred,
                HasProfile = profile is not null,
                PageIndex = 0,
                PageCount = 1,
                CellBudget = 0,
            };
        }

        List<List<Match>> pages = Paginate(matches);
        int page = Math.Clamp(pageIndex, 0, pages.Count - 1);
        List<Match> slice = pages[page];

        var groupOrder = new List<string>();
        var groupItems = new Dictionary<string, List<HudItem>>(StringComparer.Ordinal);
        foreach (Match match in slice)
        {
            string title = match.Title;

            if (!groupItems.TryGetValue(title, out List<HudItem>? items))
            {
                items = new List<HudItem>();
                groupItems[title] = items;
                groupOrder.Add(title);
            }

            // 键帽只显示「去掉触发修饰键之后」的部分：Ctrl+Shift+K 在 Ctrl 面板显示 Shift+K
            Combo display = match.Entry.Combo with
            {
                Modifiers = match.Entry.Combo.Modifiers & ~modifier,
            };

            items.Add(new HudItem { Keys = Keys.Format(display), Action = match.Entry.Action });
        }

        var groups = new HudGroup[groupOrder.Count];
        for (int i = 0; i < groupOrder.Count; i++)
        {
            groups[i] = new HudGroup { Title = groupOrder[i], Items = groupItems[groupOrder[i]].ToArray() };
        }

        return new HudModel
        {
            AppName = ResolveAppName(profile, fallbackAppName),
            Modifier = modifier,
            Groups = groups,
            TotalCount = matches.Count,
            DeferredKnownCount = deferred,
            HasProfile = profile is not null,
            PageIndex = page,
            PageCount = pages.Count,
            CellBudget = pages.Count > 1 ? ComputeCellBudget(pages) : 0,
        };
    }

    private static List<Match> Collect(
        AppShortcutProfile? profile,
        AppShortcutProfile? systemProfile,
        Modifier modifier,
        KnownShortcutFilter filter,
        out int deferred)
    {
        deferred = 0;
        var matches = new List<Match>();

        // 应用条目在前，系统条目在后（§2.5）。
        // 系统条目按组合去重：Word 的 Ctrl+Backspace 与系统档案的同名条目不能各占一格。
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        Append(matches, profile, modifier, filter, ref deferred, seen, ranks, dedupe: false);
        Append(matches, systemProfile, modifier, filter, ref deferred, seen, ranks, dedupe: true);

        // §3.2 v1.7：组优先（组顺序 = 档案里首次出现的顺序），组内按修饰键数量升序。
        // OrderBy 是稳定排序，同组同数量保持档案里的原顺序。
        return matches
            .OrderBy(match => match.GroupRank)
            .ThenBy(match => match.ModifierCount)
            .ToList();
    }

    private static void Append(
        List<Match> matches,
        AppShortcutProfile? profile,
        Modifier modifier,
        KnownShortcutFilter filter,
        ref int deferred,
        HashSet<string> seen,
        Dictionary<string, int> ranks,
        bool dedupe)
    {
        if (profile is null)
        {
            return;
        }

        foreach (ShortcutEntry entry in profile.Shortcuts)
        {
            if (!entry.IsValid)
            {
                continue; // keys 解析失败的条目在加载时已丢弃并计数
            }

            // v1.5：包含该修饰键即可，不再要求完全相等
            if ((entry.Combo.Modifiers & modifier) != modifier || entry.Combo.VirtualKey == 0)
            {
                continue;
            }

            string comboKey = Keys.Format(entry.Combo);
            if (dedupe && !seen.Add(comboKey))
            {
                continue; // 应用档案里已有同一组合
            }

            seen.Add(comboKey);

            // §4.3 v1.7：命中熟记名单不剔除，只降权到「通用」组（排最后一页）
            bool isGeneral = filter.IsKnown(entry.Combo);
            if (isGeneral)
            {
                deferred++;
            }

            string title = isGeneral
                ? GeneralGroupTitle
                : string.IsNullOrWhiteSpace(entry.Group) ? DefaultGroupTitle : entry.Group!.Trim();

            int rank;
            if (isGeneral)
            {
                rank = int.MaxValue;
            }
            else if (!ranks.TryGetValue(title, out rank))
            {
                rank = ranks.Count;
                ranks[title] = rank;
            }

            matches.Add(new Match(entry, title, rank, CountModifiers(entry.Combo.Modifiers)));
        }
    }

    private static int CountModifiers(Modifier modifiers)
    {
        int count = 0;
        if ((modifiers & Modifier.Ctrl) != 0)
        {
            count++;
        }

        if ((modifiers & Modifier.Alt) != 0)
        {
            count++;
        }

        if ((modifiers & Modifier.Shift) != 0)
        {
            count++;
        }

        if ((modifiers & Modifier.Win) != 0)
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// 分页：**按组对齐**，不把一组从中间切开（除非该组自己超过一页）。
    /// <para>
    /// 为什么不是「凑满 12 条就翻页」：那样 Word 第 1 页会横跨 3 个组、再被拆成
    /// 「字体 / 字体（续）/ 段落 / 段落（续）」5 个组名。按组对齐后第 1 页只剩
    /// 「插入 / 字体 / 字体（续）」3 个组名，同组条目也必然连续。
    /// </para>
    /// <para>
    /// 「通用」组另外强制另起一页——§4.3 v1.7 要求它出现在最后一页。
    /// </para>
    /// </summary>
    private static List<List<Match>> Paginate(List<Match> matches)
    {
        var pages = new List<List<Match>>();
        var current = new List<Match>();
        int index = 0;

        while (index < matches.Count)
        {
            string title = matches[index].Title;
            int end = index;
            while (end < matches.Count && string.Equals(matches[end].Title, title, StringComparison.Ordinal))
            {
                end++;
            }

            bool isGeneral = string.Equals(title, GeneralGroupTitle, StringComparison.Ordinal);
            if (isGeneral && current.Count > 0)
            {
                pages.Add(current);
                current = new List<Match>();
            }

            // 整组装得下就整组放，装不下就换页（不把组切成两页）
            if (current.Count > 0 && current.Count + (end - index) > PageSize)
            {
                pages.Add(current);
                current = new List<Match>();
            }

            int cursor = index;
            bool continuation = false;
            while (cursor < end)
            {
                if (current.Count >= PageSize)
                {
                    pages.Add(current);
                    current = new List<Match>();
                    continuation = true;
                }

                int room = PageSize - current.Count;
                int take = Math.Min(room, end - cursor);
                for (int i = 0; i < take; i++)
                {
                    Match match = matches[cursor + i];
                    current.Add(continuation ? match with { Title = ContinueTitle(title) } : match);
                }

                cursor += take;
                continuation = true;
            }

            index = end;
        }

        if (current.Count > 0)
        {
            pages.Add(current);
        }

        if (pages.Count == 0)
        {
            pages.Add(new List<Match>());
        }

        return pages;
    }

    /// <summary>
    /// 装填余量：顺序装列时，一组恰好卡在列尾会浪费掉最后几格（换列重试）。
    /// 三列最多浪费两格，留三格余量后每页都能在同一个容量下装完。
    /// </summary>
    private const int PackingSlack = 3;

    /// <summary>
    /// 多页时的格数预算 = 各页「条目数 + 组名数」的最大值 + 装填余量。
    /// 取最大值而不是本页值，才能保证翻页时面板高度不变。
    /// </summary>
    private static int ComputeCellBudget(List<List<Match>> pages)
    {
        int max = 1;
        foreach (List<Match> page in pages)
        {
            var titles = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in page)
            {
                titles.Add(match.Title);
            }

            max = Math.Max(max, page.Count + titles.Count);
        }

        return max + PackingSlack;
    }

    private static string ContinueTitle(string title)
        => title.EndsWith("（续）", StringComparison.Ordinal) ? title : title + "（续）";

    private static string ResolveAppName(AppShortcutProfile? profile, string fallbackAppName)
        => profile is null || string.IsNullOrWhiteSpace(profile.DisplayName)
            ? fallbackAppName
            : profile.DisplayName;

    private readonly record struct Match(ShortcutEntry Entry, string Title, int GroupRank, int ModifierCount);
}
