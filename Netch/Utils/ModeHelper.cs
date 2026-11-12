using System.Text.Json;
using System.Text.Json.Serialization;
using Netch.JsonConverter;
using Netch.Models.Modes;
using Netch.Models.Modes.ProcessMode;
using Netch.Models.Modes.ShareMode;
using Netch.Models.Modes.TunMode;
using Netch.Services;

namespace Netch.Utils;

public static class ModeHelper
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = Global.NewCustomJsonSerializerOptions();

    static ModeHelper()
    {
        JsonSerializerOptions.Converters.Add(new ModeConverterWithTypeDiscriminator());
        JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        // 读侧放宽：允许 // 注释与尾随逗号（手写/模板模式文件自文档化；程序保存产物不受影响）
        JsonSerializerOptions.ReadCommentHandling = JsonCommentHandling.Skip;
        JsonSerializerOptions.AllowTrailingCommas = true;
    }

    public static Mode LoadMode(string file)
    {
        if (file.EndsWith(".json"))
            return LoadJsonMode(file);

        if (file.EndsWith(".txt"))
            return ReadTxtMode(file);

        throw new NotSupportedException();
    }

    private static Mode LoadJsonMode(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var mode = JsonSerializer.Deserialize<Mode>(fs, JsonSerializerOptions) ?? throw new ArgumentNullException();
        mode.FullName = file;
        return mode;
    }

    public static void WriteFile(this Mode mode)
    {
        if (!mode.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Only JSON modes are writable");

        // RE-1§2.7：重命名同步等批量写入不能在中途截断原模式
        var tempFile = mode.FullName + ".tmp";
        using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            JsonSerializer.Serialize(fs, mode, JsonSerializerOptions);

        if (File.Exists(mode.FullName))
            File.Replace(tempFile, mode.FullName, mode.FullName + ".bak");
        else
            File.Move(tempFile, mode.FullName);
    }

    private static Mode ReadTxtMode(string file)
    {
        Mode mode;
        var ls = File.ReadAllLines(file);
        string modeTypeNum;

        if (ls.First().First() != '#')
            throw new FormatException("Not a valid txt mode that begins with meta line");

        var heads = ls[0][1..].Split(",", StringSplitOptions.TrimEntries);
        switch (modeTypeNum = heads.ElementAtOrDefault(1) ?? "0")
        {
            case "0":
                mode = new Redirector { FullName = file };
                break;
            case "1":
            case "2":
                mode = new TunMode { FullName = file };
                break;
            case "6":
                mode = new ShareMode { FullName = file };
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        mode.Remark.Add("en", heads[0]);

        foreach (var l in ls.Skip(1))
        {
            if (l.IsNullOrWhiteSpace())
                continue;

            if (l.StartsWith("//"))
                continue;

            Mode? includeMode = null;
            if (l.StartsWith("#include"))
            {
                var relativePath = l["#include ".Length..].Replace("<", "").Replace(">", "").Replace(".h", ".txt").Trim();
                includeMode = ReadTxtMode(ModeService.Instance.GetFullPath(relativePath));
            }

            switch (mode)
            {
                case Redirector processMode:
                    if (includeMode is Redirector pMode)
                    {
                        processMode.Bypass.AddRange(pMode.Bypass);
                        processMode.Handle.AddRange(pMode.Handle);
                    }
                    else if (l.StartsWith("!"))
                        processMode.Bypass.Add(l);
                    else
                        processMode.Handle.Add(l);

                    break;
                case ShareMode shareMode:
                    shareMode.Argument = l;
                    break;
                case TunMode tunMode:
                    if (includeMode is TunMode tMode)
                    {
                        tunMode.Bypass.AddRange(tMode.Bypass);
                        tMode.Handle.AddRange(tMode.Handle);
                        break;
                    }

                    switch (modeTypeNum)
                    {
                        case "1":
                            tunMode.Handle.Add(l);
                            break;
                        case "2":
                            tunMode.Bypass.Add(l);
                            break;
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        if (modeTypeNum == "2")
            ((TunMode)mode).Handle.Add("0.0.0.0/0");

        return mode;
    }
}
