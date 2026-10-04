using System.Text;
using System.Text.RegularExpressions;
using NetCat.Core;

namespace NetCat.Engine;

public sealed record OpenVpnBundle(string Config, string Username, string Password)
{
    public static readonly HashSet<string> FileOptions = ["pkcs12", "ca", "cert", "key", "tls-auth", "tls-crypt", "tls-crypt-v2", "crl-verify", "auth-user-pass"];
    public static string[] Tokens(string line) => Regex.Matches(line, "\"([^\"]*)\"|'([^']*)'|([^\\s]+)")
        .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Value)
        .TakeWhile(t => !t.StartsWith('#') && !t.StartsWith(';')).ToArray();

    // Selected .ovpn authorizes dependencies in its own directory tree only.
    // Outside files require an explicit caller policy; no implicit traversal/UNC reads.
    public static OpenVpnBundle Read(string path, bool allowOutsideDirectory = false)
    {
        string root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var output = new StringBuilder(); string? block = null; var inline = new List<string>();
        string username = "", password = "";
        void Credentials(string text)
        {
            var lines = text.Replace("\r", "").Split('\n');
            if (lines.Length < 2 || lines[0].Length == 0) throw new InvalidDataException("Файл auth-user-pass должен содержать логин и пароль на двух строках.");
            username = lines[0]; password = lines[1];
        }
        foreach (var line in File.ReadLines(path))
        {
            var t = line.Trim();
            if (block != null)
            {
                if (t == "</" + block + ">")
                {
                    if (block == "auth-user-pass") { Credentials(string.Join("\n", inline)); output.AppendLine("auth-user-pass"); }
                    else { output.AppendLine("<" + block + ">"); foreach (var item in inline) output.AppendLine(item); output.AppendLine(t); }
                    block = null; inline.Clear();
                }
                else inline.Add(line);
                continue;
            }
            if (t.StartsWith('<') && t.EndsWith('>')) { block = t[1..^1]; continue; }
            var args = Tokens(t); var kind = args.FirstOrDefault()?.TrimStart('-') ?? "";
            if (!FileOptions.Contains(kind) || args.Length == 1 || args[1] == "[inline]") { output.AppendLine(line); continue; }
            if (args.Length > 3 || args.Length == 3 && (kind != "tls-auth" || args[2] is not ("0" or "1")))
                throw new InvalidDataException("Неподдерживаемые параметры OpenVPN: " + kind);
            var full = Path.GetFullPath(args[1], root);
            var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (!allowOutsideDirectory && !full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("OpenVPN: " + kind + " ссылается за пределы папки профиля. Поместите зависимость рядом с .ovpn и исправьте ссылку.");
            for (FileSystemInfo? node = new FileInfo(full); node != null; node = node is DirectoryInfo dir ? dir.Parent : ((FileInfo)node).Directory)
            {
                if (node.Exists && (node.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Ссылки файловой системы в OpenVPN bundle запрещены.");
            }
            if (!File.Exists(full)) throw new FileNotFoundException("Не найден файл OpenVPN: " + kind + " " + Path.GetFileName(full) + ". Ожидался рядом с выбранным .ovpn.");
            if (new FileInfo(full).Length > 8 * 1024 * 1024) throw new InvalidDataException("Слишком большая зависимость OpenVPN: " + kind);
            if (kind == "auth-user-pass") { Credentials(File.ReadAllText(full)); output.AppendLine("auth-user-pass"); continue; }
            var content = kind == "pkcs12" ? Convert.ToBase64String(File.ReadAllBytes(full), Base64FormattingOptions.InsertLineBreaks) : File.ReadAllText(full);
            if (kind != "pkcs12" && Regex.IsMatch(content, @"(?m)^\s*</?" + Regex.Escape(kind) + @">")) throw new InvalidDataException("Некорректное содержимое зависимости " + kind);
            output.AppendLine("<" + kind + ">").AppendLine(content).AppendLine("</" + kind + ">");
            if (args.Length == 3) output.AppendLine("key-direction " + args[2]);
        }
        if (block != null) throw new InvalidDataException("Незакрытый inline блок OpenVPN: " + block);
        var config = OpenVpnConfiguration.Normalize(output.ToString()); OpenVpnConfiguration.Validate(config);
        return new(config, username, password);
    }
    public Profile ToProfile(string name)
    {
        var result = ProfileImporter.Parse(Config, name);
        if (result.Errors.Count != 0 || result.Profiles.Count != 1) throw new InvalidDataException(string.Join("\n", result.Errors));
        var p = result.Profiles[0]; p.Username = Username; p.Password = Password; return p;
    }
}
