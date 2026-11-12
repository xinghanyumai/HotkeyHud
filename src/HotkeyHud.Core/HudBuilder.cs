namespace HotkeyHud.Core;

/// <summary>§3.2 查询 + §4.3 列表过滤 + 分组。UI 拿到结果后不再加工。</summary>
public static class HudBuilder
{
    public const string DefaultGroupTitle = "常规";

    public static HudModel Build(
        AppShortcutProfile? profile,
        Modifier modifier,
        KnownShortcutFilter filter,
        string fallbackAppName = "")
    {
        ArgumentNullException.ThrowIfNull(filter);

        if (profile is null)
        {
            // 未收录应用：返回空模型，绝不混入其他应用条目（§3.2 空结果规则）
            return new HudModel
            {
                AppName = fallbackAppName,
                Modifier = modifier,
                Groups = Array.Empty<HudGroup>(),
                TotalCount = 0,
                HiddenByKnownCount = 0,
                HasProfile = false,
            };
        }

        var groupOrder = new List<string>();
        var groupItems = new Dictionary<string, List<HudItem>>(StringComparer.Ordinal);
        int hidden = 0;
        int total = 0;

        foreach (ShortcutEntry entry in profile.Shortcuts)
        {
            if (!entry.IsValid)
            {
                continue; // keys 解析失败的条目在加载时已丢弃并计数
            }

            if (entry.Combo.Modifiers != modifier)
            {
                continue; // 精确匹配：Ctrl+Shift+K 不得出现在 Ctrl 面板
            }

            if (filter.IsKnown(entry.Combo))
            {
                hidden++;
                continue;
            }

            string title = string.IsNullOrWhiteSpace(entry.Group) ? DefaultGroupTitle : entry.Group!;
            if (!groupItems.TryGetValue(title, out List<HudItem>? items))
            {
                items = new List<HudItem>();
                groupItems[title] = items;
                groupOrder.Add(title);
            }

            items.Add(new HudItem { Keys = entry.Keys, Action = entry.Action });
            total++;
        }

        var groups = new HudGroup[groupOrder.Count];
        for (int i = 0; i < groupOrder.Count; i++)
        {
            string title = groupOrder[i];
            groups[i] = new HudGroup { Title = title, Items = groupItems[title] };
        }

        return new HudModel
        {
            AppName = profile.DisplayName,
            Modifier = modifier,
            Groups = groups,
            TotalCount = total,
            HiddenByKnownCount = hidden,
            HasProfile = true,
        };
    }
}
