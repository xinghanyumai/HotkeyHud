namespace HotkeyHud.Core;

/// <summary>
/// 把「应用档案 + 系统档案」变成某一页的 <see cref="HudModel"/>。
/// <para>
/// §3.2 v1.5：匹配规则是**包含**该修饰键，不再是完全相等——Ctrl 面板要能看到
/// <c>Ctrl+Shift+K</c>、<c>Ctrl+Space</c>。排序按修饰键数量升序（<c>Ctrl+S</c> 在
/// <c>Ctrl+Shift+S</c> 之前），同数量保持档案里的原顺序。
/// </para>
/// </summary>
public static class HudBuilder
{
    public const string DefaultGroupTitle = "常规";

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

        List<Match> matches = Collect(profile, systemProfile, modifier, filter, out int hidden);

        // §3.2 v1.6：未收录应用不再拿系统档案的零星 Ctrl 条目冒充内容。
        // 除 Win 之外一律走引导空态——Win 面板的全部内容本来就是系统快捷键。
        if (profile is null && modifier != Modifier.Win)
        {
            matches.Clear();
        }

        int total = matches.Count;
        int pageCount = total == 0 ? 1 : (total + PageSize - 1) / PageSize;
        int page = Math.Clamp(pageIndex, 0, pageCount - 1);

        if (total == 0)
        {
            // 未收录应用：返回空模型，绝不混入其他应用条目（§3.2 空结果规则）
            return new HudModel
            {
                AppName = ResolveAppName(profile, fallbackAppName),
                Modifier = modifier,
                Groups = Array.Empty<HudGroup>(),
                TotalCount = 0,
                HiddenByKnownCount = hidden,
                HasProfile = profile is not null,
                PageIndex = 0,
                PageCount = 1,
                CellBudget = 0,
            };
        }

        List<Match> slice = matches.Skip(page * PageSize).Take(PageSize).ToList();

        var groupOrder = new List<string>();
        var groupItems = new Dictionary<string, List<HudItem>>(StringComparer.Ordinal);
        foreach (Match match in slice)
        {
            string title = string.IsNullOrWhiteSpace(match.Entry.Group)
                ? DefaultGroupTitle
                : match.Entry.Group!.Trim();

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
            TotalCount = total,
            HiddenByKnownCount = hidden,
            HasProfile = profile is not null,
            PageIndex = page,
            PageCount = pageCount,
            CellBudget = pageCount > 1 ? ComputeCellBudget(matches) : 0,
        };
    }

    private static List<Match> Collect(
        AppShortcutProfile? profile,
        AppShortcutProfile? systemProfile,
        Modifier modifier,
        KnownShortcutFilter filter,
        out int hidden)
    {
        hidden = 0;
        var matches = new List<Match>();

        // 应用条目在前，系统条目在后（§2.5）。
        // 系统条目按组合去重：Word 的 Ctrl+Backspace 与系统档案的同名条目不能各占一格。
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Append(matches, profile, modifier, filter, ref hidden, seen, dedupe: false);
        Append(matches, systemProfile, modifier, filter, ref hidden, seen, dedupe: true);

        // 修饰键数量少的在前；OrderBy 是稳定排序，同数量保持原顺序
        return matches.OrderBy(match => match.ModifierCount).ToList();
    }

    private static void Append(
        List<Match> matches,
        AppShortcutProfile? profile,
        Modifier modifier,
        KnownShortcutFilter filter,
        ref int hidden,
        HashSet<string> seen,
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

            if (filter.IsKnown(entry.Combo))
            {
                hidden++;
                continue;
            }

            matches.Add(new Match(entry, CountModifiers(entry.Combo.Modifiers)));
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
    /// 多页时的格数预算 = 12 条 + 各页里最多的组名数。
    /// 取各页最大值而不是本页值，才能保证翻页时面板高度不变。
    /// </summary>
    private static int ComputeCellBudget(List<Match> matches)
    {
        int maxGroups = 1;
        int pageCount = (matches.Count + PageSize - 1) / PageSize;

        for (int page = 0; page < pageCount; page++)
        {
            var titles = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in matches.Skip(page * PageSize).Take(PageSize))
            {
                titles.Add(string.IsNullOrWhiteSpace(match.Entry.Group)
                    ? DefaultGroupTitle
                    : match.Entry.Group!.Trim());
            }

            maxGroups = Math.Max(maxGroups, titles.Count);
        }

        return PageSize + maxGroups;
    }

    private static string ResolveAppName(AppShortcutProfile? profile, string fallbackAppName)
        => profile is null || string.IsNullOrWhiteSpace(profile.DisplayName)
            ? fallbackAppName
            : profile.DisplayName;

    private readonly record struct Match(ShortcutEntry Entry, int ModifierCount);
}
