using System.Text.Json.Serialization;
using Netch.Utils;

namespace Netch.Models.Modes;

public abstract class Mode
{
    [JsonPropertyOrder(int.MinValue)]
    public abstract ModeType Type { get; }

    public Dictionary<string, string> Remark { get; set; } = new();

    [JsonIgnore]
    // File FullName
    // TODO maybe make it becomes mode dictionary key
    public string FullName { get; set; } = string.Empty;

    public override string ToString() => $"[{(int)Type + 1}] {i18NRemark}";

    [JsonIgnore]
    public string i18NRemark
    {
        // i18n 键制（05）：Remark 字典按 I18N.Culture.Name 取值，回退 en
        get => Remark.GetValueOrDefault(Netch.Localization.I18N.Culture.Name) ?? Remark.GetValueOrDefault("en") ?? "";
        set => Remark[Netch.Localization.I18N.Culture.Name] = value;
    }
}