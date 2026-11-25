using System.Text;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin.Tools.Skills;

/// <summary>一条技能。Folder 是 skills 目录下的子目录名，和名称不一定相同。</summary>
internal sealed record SkillDefinition(string Name, string Description, string Instructions, bool Enabled, string Folder, DateTimeOffset Modified);

/// <summary>
/// 技能保存在 data/skills/&lt;目录&gt;/SKILL.md，格式和常见的 SKILL.md 一致：YAML frontmatter 写 name、description，正文是操作说明。
/// 可以直接把别处的技能目录复制进来，也可以在 Dashboard 的技能页面编辑；变化后通过 <see cref="Changed"/> 通知重建 skill 工具。
/// </summary>
internal sealed class SkillStore
{
    public const int MaxSkills = 200;
    public const int MaxName = 64;
    public const int MaxDescription = 1000;
    public const int MaxInstructions = 32000;
    private const string FileName = "SKILL.md";
    private const string MigratedMarker = ".migrated-from-config";

    private readonly string _directory;
    private readonly Lock _gate = new();
    private IReadOnlyList<SkillDefinition> _all = [];

    public SkillStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        Reload();
    }

    public string DirectoryPath => _directory;
    public IReadOnlyList<SkillDefinition> All => _all;
    public event Action? Changed;

    /// <summary>重新读取目录，手动改过文件后使用。无效或重名的技能跳过并记日志。</summary>
    public void Reload()
    {
        lock (_gate) _all = Load();
        Changed?.Invoke();
    }

    private List<SkillDefinition> Load()
    {
        var skills = new List<SkillDefinition>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in Directory.EnumerateDirectories(_directory).OrderBy(path => path, StringComparer.Ordinal))
        {
            var path = Path.Combine(folder, FileName);
            if (!File.Exists(path)) continue;
            try
            {
                var parsed = Parse(File.ReadAllText(path, Encoding.UTF8));
                if (!names.Add(parsed.Name)) throw new ArgumentException($"名称 {parsed.Name} 重复。");
                if (skills.Count >= MaxSkills) throw new ArgumentException($"最多 {MaxSkills} 个技能。");
                skills.Add(parsed with { Folder = Path.GetFileName(folder), Modified = File.GetLastWriteTimeUtc(path) });
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                BotLog.Warning($"[AiChat] 跳过技能 {Path.GetFileName(folder)}/{FileName}: {ex.Message}");
            }
        }
        return skills.OrderBy(skill => skill.Name, StringComparer.Ordinal).ToList();
    }

    public SkillDefinition? Get(string name) => _all.FirstOrDefault(skill => skill.Name == name);

    /// <summary>新建或修改技能。originalName 为空表示新建；改名时保留原目录。</summary>
    public SkillDefinition Save(string name, string description, string instructions, bool enabled, string? originalName)
    {
        var skill = Validate(name, description, instructions, enabled);
        SkillDefinition saved;
        lock (_gate)
        {
            var existing = originalName is null ? null : _all.FirstOrDefault(item => item.Name == originalName)
                ?? throw new ArgumentException($"找不到技能 {originalName}，可能已被删除，请刷新。");
            if (_all.Any(item => item.Name == skill.Name && item != existing))
                throw new ArgumentException($"已经有名为 {skill.Name} 的技能。");
            if (existing is null && _all.Count >= MaxSkills) throw new ArgumentException($"最多 {MaxSkills} 个技能。");
            var folder = existing?.Folder ?? NewFolder(skill.Name);
            var path = Path.Combine(_directory, folder, FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, Format(skill), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
            saved = skill with { Folder = folder, Modified = File.GetLastWriteTimeUtc(path) };
            _all = _all.Where(item => item != existing).Append(saved).OrderBy(item => item.Name, StringComparer.Ordinal).ToList();
        }
        Changed?.Invoke();
        return saved;
    }

    public SkillDefinition SetEnabled(string name, bool enabled)
    {
        var skill = Get(name) ?? throw new ArgumentException($"找不到技能 {name}，请刷新。");
        return Save(skill.Name, skill.Description, skill.Instructions, enabled, skill.Name);
    }

    /// <summary>删除技能目录，包括目录里的其他文件。</summary>
    public void Delete(string name)
    {
        lock (_gate)
        {
            var skill = _all.FirstOrDefault(item => item.Name == name) ?? throw new ArgumentException($"找不到技能 {name}，可能已被删除。");
            var folder = Path.GetFullPath(Path.Combine(_directory, skill.Folder));
            if (Path.GetDirectoryName(folder) != _directory) throw new InvalidOperationException("技能目录不在 skills 目录下。");
            Directory.Delete(folder, recursive: true);
            _all = _all.Where(item => item != skill).ToList();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// 把旧版 config.toml 里 [[skills.entries]] 的技能搬到目录里。迁移过的名称记在标记文件中，
    /// 之后在页面里删掉也不会因为配置里还留着而重新出现。
    /// </summary>
    public int MigrateLegacy(IEnumerable<CustomSkillEntry> entries)
    {
        var markerPath = Path.Combine(_directory, MigratedMarker);
        var migrated = File.Exists(markerPath)
            ? File.ReadAllLines(markerPath, Encoding.UTF8).Where(line => line.Length > 0).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var entry in entries)
        {
            var name = entry.Name.Trim();
            if (name.Length == 0 || migrated.Contains(name)) continue;
            migrated.Add(name);
            if (Get(name) is not null) continue;
            try { Save(name, entry.Description, entry.Instructions, entry.Enabled, null); count++; }
            catch (ArgumentException ex) { BotLog.Warning($"[AiChat] 旧版技能 {name} 未迁移: {ex.Message}"); }
        }
        if (migrated.Count > 0) File.WriteAllLines(markerPath, migrated.Order(StringComparer.Ordinal), new UTF8Encoding(false));
        return count;
    }

    internal static SkillDefinition Validate(string name, string description, string instructions, bool enabled)
    {
        name = (name ?? "").Trim();
        description = (description ?? "").Trim();
        instructions = (instructions ?? "").Trim().Replace("\r\n", "\n");
        if (name.Length is < 1 or > MaxName || name.Any(char.IsControl))
            throw new ArgumentException($"名称需要 1-{MaxName} 个字符，不能包含控制字符。");
        if (description.Length is < 1 or > MaxDescription)
            throw new ArgumentException($"用途需要 1-{MaxDescription} 个字符。");
        if (instructions.Length is < 1 or > MaxInstructions)
            throw new ArgumentException($"操作说明需要 1-{MaxInstructions} 个字符。");
        return new SkillDefinition(name, description, instructions, enabled, "", default);
    }

    private string NewFolder(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var slug = new string(name.Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '-' : c).ToArray()).Trim('-', '.');
        if (slug.Length == 0) slug = "skill";
        var folder = slug;
        for (var i = 2; Directory.Exists(Path.Combine(_directory, folder)); i++) folder = $"{slug}-{i}";
        return folder;
    }

    /// <summary>解析 SKILL.md。frontmatter 只支持技能文件常用的写法：key: value、引号字符串和 | &gt; 多行块。</summary>
    internal static SkillDefinition Parse(string markdown)
    {
        var text = (markdown ?? "").TrimStart('\uFEFF').Replace("\r\n", "\n");
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
            throw new ArgumentException("SKILL.md 开头需要 --- 包住的 frontmatter，至少写 name 和 description。");
        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) throw new ArgumentException("frontmatter 没有用 --- 结束。");
        var afterEnd = text.IndexOf('\n', end + 4);
        var body = afterEnd < 0 ? "" : text[(afterEnd + 1)..];
        var lines = text[4..end].Split('\n');
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line.TrimStart().StartsWith('#')) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (value.Length > 0 && value[0] is '|' or '>')
            {
                var folded = value[0] == '>';
                var block = new List<string>();
                while (i + 1 < lines.Length && (lines[i + 1].Length == 0 || char.IsWhiteSpace(lines[i + 1][0]))) block.Add(lines[++i].Trim());
                while (block.Count > 0 && block[^1].Length == 0) block.RemoveAt(block.Count - 1);
                value = folded ? string.Join(" ", block.Where(part => part.Length > 0)) : string.Join("\n", block);
            }
            else value = Unquote(value);
            values[key] = value;
        }
        var enabled = !values.TryGetValue("enabled", out var flag) || !flag.Equals("false", StringComparison.OrdinalIgnoreCase);
        return Validate(values.GetValueOrDefault("name") ?? "", values.GetValueOrDefault("description") ?? "", body, enabled);
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            var builder = new StringBuilder();
            for (var i = 1; i < value.Length - 1; i++)
            {
                if (value[i] != '\\' || i + 1 >= value.Length - 1) { builder.Append(value[i]); continue; }
                var next = value[++i];
                builder.Append(next switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => next });
            }
            return builder.ToString();
        }
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') return value[1..^1].Replace("''", "'");
        var comment = value.IndexOf(" #", StringComparison.Ordinal);
        return comment >= 0 ? value[..comment].TrimEnd() : value;
    }

    internal static string Format(SkillDefinition skill)
    {
        static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "").Replace("\t", "\\t") + "\"";
        var builder = new StringBuilder("---\n")
            .Append("name: ").Append(Quote(skill.Name)).Append('\n')
            .Append("description: ").Append(Quote(skill.Description)).Append('\n');
        if (!skill.Enabled) builder.Append("enabled: false\n");
        return builder.Append("---\n\n").Append(skill.Instructions).Append('\n').ToString();
    }
}
